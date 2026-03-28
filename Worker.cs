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

    public Worker(
        ILogger<Worker> log,
        AlertsOptions opt,
        SqlRepository db,
        NwsClient nws,
        ExpoPushClient expo,
        StateStore stateStore)
    {
        _log = log;
        _opt = opt;
        _db = db;
        _nws = nws;
        _expo = expo;
        _stateStore = stateStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("MorseSafetyAlerts starting. TickSeconds={TickSeconds}", _opt.TickSeconds);

        var state = _stateStore.Load();

        while (!stoppingToken.IsCancellationRequested)
        {
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
                _log.LogError(ex, "Tick failed");
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
        // Phase 1: Storm alerts (NWS). Lightning relay will be added next.
        var storm = await _nws.GetStormStatusAsync(state.NwsEtag, ct);

        // If NWS returns 304, we don't have new information.
        if (storm.Title == "(not-modified)")
        {
            _log.LogDebug("NWS not modified.");
            return;
        }

        state.NwsEtag = storm.ETag;
        state.LastStormActive = storm.Active;

        await HandleStormAsync(storm, ct);
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
