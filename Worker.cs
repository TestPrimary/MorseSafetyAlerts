using System.Text.Json;

namespace MorseSafetyAlerts;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _log;
    private readonly AlertsOptions _opt;
    private readonly SqlRepository _db;
    private readonly NwsClient _nws;
    private readonly ExpoPushClient _expo;
    private readonly StateStore _stateStore;
    private readonly StatusWriter _status;
    private readonly LightningStrikeWindow _lightning;

    public Worker(
        ILogger<Worker> log,
        AlertsOptions opt,
        SqlRepository db,
        NwsClient nws,
        ExpoPushClient expo,
        StateStore stateStore,
        StatusWriter status,
        LightningStrikeWindow lightning)
    {
        _log = log;
        _opt = opt;
        _db = db;
        _nws = nws;
        _expo = expo;
        _stateStore = stateStore;
        _status = status;
        _lightning = lightning;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startedUtc = DateTime.UtcNow;
        _log.LogInformation("MorseSafetyAlerts starting. TickSeconds={TickSeconds}", _opt.TickSeconds);

        var state = _stateStore.Load();

        // Seed lightning rolling window (best-effort) from persisted state.
        _lightning.SeedFromState(state.LightningRecentStrikeMs, state.LightningLastStrikeMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            var tickStart = DateTime.UtcNow;
            string? lastError = null;

            try
            {
                await TickAsync(state, stoppingToken);
                _stateStore.Save(state);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                _log.LogError(ex, "Tick failed");
            }
            finally
            {
                _status.Write(new ServiceStatus(
                    StartedUtc: startedUtc,
                    LastTickStartUtc: tickStart,
                    LastTickEndUtc: DateTime.UtcNow,
                    LastStormActive: state.LastStormActive,
                    ActiveStormEpisodeId: null,
                    LastTargetsCount: null,
                    LastSentCount: null,
                    LastError: lastError
                ));
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_opt.TickSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shutting down
            }
        }

        _log.LogInformation("MorseSafetyAlerts stopped.");
    }

    private async Task TickAsync(ServiceState state, CancellationToken ct)
    {
        // Storm alerts (NWS)
        var storm = await _nws.GetStormStatusAsync(state.NwsEtag, ct);

        // If NWS returns 304, we don't have new information.
        if (storm.Title != "(not-modified)")
        {
            state.NwsEtag = storm.ETag;
            state.LastStormActive = storm.Active;
            await HandleStormAsync(storm, ct);
        }
        else
        {
            _log.LogDebug("NWS not modified.");
        }

        // Lightning alerts (MQTT rolling window)
        await HandleLightningAsync(state, ct);

        // Persist lightning rolling window state.
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var exported = _lightning.ExportState(nowMs);
        state.LightningRecentStrikeMs = exported.RecentStrikeMs;
        state.LightningLastStrikeMs = exported.LastStrikeMs;

        // After sends, process receipts to clean up dead tokens.
        await ProcessReceiptsAsync(state, ct);
    }

    private async Task ProcessReceiptsAsync(ServiceState state, CancellationToken ct)
    {
        // Prune old processed ticket IDs.
        var cutoff = DateTime.UtcNow.AddDays(-2);
        var toRemove = state.ProcessedReceiptTicketsUtc.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();
        foreach (var k in toRemove) state.ProcessedReceiptTicketsUtc.Remove(k);

        var lookbackMinutes = 180;
        var sent = await _db.GetRecentSentDeliveriesForReceiptsAsync(lookbackMinutes, ct);
        if (sent.Count == 0) return;

        var ticketToDelivery = new Dictionary<string, SentDeliveryForReceiptRow>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in sent)
        {
            var ticketId = TryExtractTicketId(d.ResponseJson);
            if (string.IsNullOrWhiteSpace(ticketId)) continue;
            if (state.ProcessedReceiptTicketsUtc.ContainsKey(ticketId)) continue;

            // keep the newest delivery if duplicates happen
            if (!ticketToDelivery.TryGetValue(ticketId, out var existing) || d.UpdatedUtc > existing.UpdatedUtc)
            {
                ticketToDelivery[ticketId] = d;
            }
        }

        if (ticketToDelivery.Count == 0) return;

        var receiptBatchSize = Math.Max(1, _opt.Expo.ReceiptBatchSize);
        var ids = ticketToDelivery.Keys.ToList();

        _log.LogInformation("Receipt check: tickets={Count}", ids.Count);

        for (var i = 0; i < ids.Count; i += receiptBatchSize)
        {
            var batch = ids.Skip(i).Take(receiptBatchSize).ToList();
            var (ok, raw, receipts) = await _expo.GetReceiptsAsync(batch, ct);

            if (!ok)
            {
                _log.LogWarning("Receipt batch failed: {Raw}", raw);
                continue;
            }

            foreach (var id in batch)
            {
                if (!receipts.TryGetValue(id, out var r) || r is null)
                {
                    // not ready yet
                    continue;
                }

                state.ProcessedReceiptTicketsUtc[id] = DateTime.UtcNow;

                if (ticketToDelivery.TryGetValue(id, out var d))
                {
                    if (string.Equals(r.Status, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        // leave delivery as 'sent'
                        continue;
                    }

                    var msg = r.Message ?? "expo-receipt-error";
                    await _db.UpdateDeliveryAsync(d.DeliveryId, "failed", null, msg, JsonSerializer.Serialize(r), ct);

                    // Deactivate bad tokens if applicable.
                    if (r.Details is not null && r.Details.TryGetValue("error", out var errObj))
                    {
                        var err = errObj?.ToString();
                        if (string.Equals(err, "DeviceNotRegistered", StringComparison.OrdinalIgnoreCase))
                        {
                            _log.LogInformation("Deactivating PushTokenId={PushTokenId} due to DeviceNotRegistered", d.PushTokenId);
                            await _db.DeactivatePushTokenAsync(d.PushTokenId, ct);
                        }
                    }
                }
            }
        }
    }

    private static string? TryExtractTicketId(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                return id.GetString();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private async Task HandleStormAsync(NwsStormStatus storm, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var activeEpisode = await _db.GetActiveEpisodeAsync("storm", ct);

        if (storm.Active && activeEpisode is null)
        {
            _log.LogInformation("Storm became ACTIVE. Creating episode + sending pushes.");

            var episodeId = await _db.CreateEpisodeAsync(
                alertType: "storm",
                geofenceKey: _opt.GeofenceKey,
                title: storm.Title,
                message: storm.Message,
                severity: storm.Severity,
                startedUtc: now,
                endedUtc: null,
                ct: ct);

            var targets = await _db.GetStormTargetsForEpisodeAsync(episodeId, _opt.GeofenceKey, _opt.GeofenceFreshMinutes, ct);
            _log.LogInformation("Storm episode {EpisodeId}: targets={Count}", episodeId, targets.Count);

            if (targets.Count == 0) return;

            var deliveries = await _db.CreatePendingDeliveriesAsync(episodeId, targets, ct);
            await SendAndUpdateDeliveriesAsync(episodeId, storm.Title, storm.Message, deliveries, ct);

            return;
        }

        if (!storm.Active && activeEpisode is not null)
        {
            _log.LogInformation("Storm became CLEAR. Sending all-clear and ending episode {EpisodeId}.", activeEpisode.EpisodeId);

            // Determine the recipients that got the start alert.
            var recipients = await _db.GetRecipientsFromEpisodeAsync(activeEpisode.EpisodeId, ct);
            _log.LogInformation("All-clear recipients from episode {EpisodeId}: {Count}", activeEpisode.EpisodeId, recipients.Count);

            // Create a separate, already-ended episode for the All Clear notification.
            var clearEpisodeId = await _db.CreateEpisodeAsync(
                alertType: "storm",
                geofenceKey: _opt.GeofenceKey,
                title: "All Clear",
                message: "Storm alerts have cleared near Morse Reservoir.",
                severity: 1,
                startedUtc: now,
                endedUtc: now,
                ct: ct);

            if (recipients.Count > 0)
            {
                var clearDeliveries = await _db.CreatePendingDeliveriesAsync(clearEpisodeId, recipients, ct);
                await SendAndUpdateDeliveriesAsync(clearEpisodeId, "All Clear", "Storm alerts have cleared near Morse Reservoir.", clearDeliveries, ct);
            }

            await _db.EndEpisodeAsync(activeEpisode.EpisodeId, now, ct);

            return;
        }

        // No state transition.
        _log.LogDebug("Storm no-change. active={Active} episode={Episode}", storm.Active, activeEpisode?.EpisodeId);
    }

    private async Task HandleLightningAsync(ServiceState state, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var snap = _lightning.GetSnapshot(nowMs);
        var activeEpisode = await _db.GetActiveEpisodeAsync("lightning", ct);

        var trigger = Math.Max(1, _opt.Lightning.TriggerCount);
        var isActiveByWindow = snap.CountInWindow >= trigger;

        if (isActiveByWindow && activeEpisode is null)
        {
            _log.LogInformation("Lightning became ACTIVE. strikes={Count} windowMin={WindowMin}", snap.CountInWindow, _opt.Lightning.WindowMinutes);

            var title = "Lightning Alert";
            var msg = $"Lightning detected near Morse Reservoir: {snap.CountInWindow}+ strikes in the last {_opt.Lightning.WindowMinutes} minutes (within {_opt.Lightning.RadiusMiles:0} mi).";

            var episodeId = await _db.CreateEpisodeAsync(
                alertType: "lightning",
                geofenceKey: _opt.GeofenceKey,
                title: title,
                message: msg,
                severity: 4,
                startedUtc: now,
                endedUtc: null,
                ct: ct);

            var targets = await _db.GetLightningTargetsForEpisodeAsync(episodeId, _opt.GeofenceKey, _opt.GeofenceFreshMinutes, ct);
            _log.LogInformation("Lightning episode {EpisodeId}: targets={Count}", episodeId, targets.Count);

            if (targets.Count == 0) return;

            var deliveries = await _db.CreatePendingDeliveriesAsync(episodeId, targets, ct);
            await SendAndUpdateDeliveriesAsync(episodeId, title, msg, deliveries, ct);

            return;
        }

        if (!isActiveByWindow && activeEpisode is not null)
        {
            // All-clear rule: 0 strikes for the full window duration.
            var windowMs = snap.WindowMs;
            var lastStrikeMs = snap.LastStrikeMs;
            if (lastStrikeMs > 0 && (nowMs - lastStrikeMs) < windowMs)
            {
                _log.LogDebug("Lightning quiet but waiting all-clear window. sinceLastMs={Ms}", (nowMs - lastStrikeMs));
                return;
            }

            _log.LogInformation("Lightning became CLEAR. Sending all-clear and ending episode {EpisodeId}.", activeEpisode.EpisodeId);

            var recipients = await _db.GetRecipientsFromEpisodeAsync(activeEpisode.EpisodeId, ct);
            _log.LogInformation("Lightning all-clear recipients from episode {EpisodeId}: {Count}", activeEpisode.EpisodeId, recipients.Count);

            var clearEpisodeId = await _db.CreateEpisodeAsync(
                alertType: "lightning",
                geofenceKey: _opt.GeofenceKey,
                title: "All Clear",
                message: "Lightning has cleared near Morse Reservoir.",
                severity: 1,
                startedUtc: now,
                endedUtc: now,
                ct: ct);

            if (recipients.Count > 0)
            {
                var clearDeliveries = await _db.CreatePendingDeliveriesAsync(clearEpisodeId, recipients, ct);
                await SendAndUpdateDeliveriesAsync(clearEpisodeId, "All Clear", "Lightning has cleared near Morse Reservoir.", clearDeliveries, ct);
            }

            await _db.EndEpisodeAsync(activeEpisode.EpisodeId, now, ct);
            return;
        }

        _log.LogDebug("Lightning no-change. activeByWindow={Active} strikes={Count} episode={Episode}", isActiveByWindow, snap.CountInWindow, activeEpisode?.EpisodeId);
    }

    private async Task SendAndUpdateDeliveriesAsync(
        long episodeId,
        string title,
        string body,
        List<DeliveryRow> deliveries,
        CancellationToken ct)
    {
        var batchSize = Math.Max(1, _opt.Expo.BatchSize);
        var sentUtc = DateTime.UtcNow;

        for (var i = 0; i < deliveries.Count; i += batchSize)
        {
            var batch = deliveries.Skip(i).Take(batchSize).ToList();

            var tokens = batch.Select(d => d.Token).ToList();

            var result = await _expo.SendAsync(
                expoPushTokens: tokens,
                title: title,
                body: body,
                sound: _opt.Expo.DefaultSound,
                data: new { kind = "safety", episodeId },
                ct: ct);

            if (!result.Ok)
            {
                foreach (var d in batch)
                {
                    await _db.UpdateDeliveryAsync(d.DeliveryId, "failed", null, "expo-http-failure", result.RawJson, ct);
                }
                continue;
            }

            // Expo returns tickets in the same order as messages.
            for (var j = 0; j < batch.Count; j++)
            {
                var d = batch[j];
                var ticket = (j < result.Tickets.Count) ? result.Tickets[j] : null;

                if (ticket?.Status?.Equals("ok", StringComparison.OrdinalIgnoreCase) == true)
                {
                    await _db.UpdateDeliveryAsync(d.DeliveryId, "sent", sentUtc, null, JsonSerializer.Serialize(ticket), ct);
                }
                else
                {
                    var err = ticket?.Message ?? "expo-ticket-error";
                    await _db.UpdateDeliveryAsync(d.DeliveryId, "failed", null, err, JsonSerializer.Serialize(ticket), ct);
                }
            }
        }

        _log.LogInformation("Episode {EpisodeId}: send complete. deliveries={Count}", episodeId, deliveries.Count);
    }
}
