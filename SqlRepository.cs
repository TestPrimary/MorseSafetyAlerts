using Dapper;
using Microsoft.Data.SqlClient;

namespace MorseSafetyAlerts;

public class SqlRepository
{
    private readonly string _cs;
    private readonly ILogger<SqlRepository> _log;

    public SqlRepository(string connectionString, ILogger<SqlRepository> log)
    {
        _cs = connectionString;
        _log = log;
    }

    private SqlConnection Open() => new SqlConnection(_cs);

    public async Task<ActiveEpisodeRow?> GetActiveEpisodeAsync(string alertType, CancellationToken ct)
    {
        const string sql = @"
SELECT TOP 1
    EpisodeId,
    AlertType,
    GeofenceKey,
    Title,
    Message,
    Severity,
    StartedUtc,
    EndedUtc,
    CreatedUtc,
    UpdatedUtc
FROM SafetyAlertEpisodes
WHERE AlertType = @alertType AND EndedUtc IS NULL
ORDER BY StartedUtc DESC;
";

        await using var conn = Open();
        return await conn.QueryFirstOrDefaultAsync<ActiveEpisodeRow>(new CommandDefinition(sql, new { alertType }, cancellationToken: ct));
    }

    public async Task<long> CreateEpisodeAsync(
        string alertType,
        string? geofenceKey,
        string? title,
        string? message,
        byte severity,
        DateTime startedUtc,
        DateTime? endedUtc,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO SafetyAlertEpisodes (AlertType, GeofenceKey, Title, Message, Severity, StartedUtc, EndedUtc, CreatedUtc, UpdatedUtc)
OUTPUT INSERTED.EpisodeId
VALUES (@alertType, @geofenceKey, @title, @message, @severity, @startedUtc, @endedUtc, SYSUTCDATETIME(), SYSUTCDATETIME());
";

        await using var conn = Open();
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, new
        {
            alertType,
            geofenceKey,
            title,
            message,
            severity,
            startedUtc,
            endedUtc,
        }, cancellationToken: ct));

        return id;
    }

    public async Task EndEpisodeAsync(long episodeId, DateTime endedUtc, CancellationToken ct)
    {
        const string sql = @"
UPDATE SafetyAlertEpisodes
SET EndedUtc = @endedUtc,
    UpdatedUtc = SYSUTCDATETIME()
WHERE EpisodeId = @episodeId AND EndedUtc IS NULL;
";

        await using var conn = Open();
        await conn.ExecuteAsync(new CommandDefinition(sql, new { episodeId, endedUtc }, cancellationToken: ct));
    }

    public async Task<List<PushTargetRow>> GetStormTargetsForEpisodeAsync(
        long episodeId,
        string geofenceKey,
        int geofenceFreshMinutes,
        CancellationToken ct)
    {
        // Note: Tokens in our DB are Expo push tokens (ExponentPushToken[...] ).
        const string sql = @"
DECLARE @cutoff datetime2 = DATEADD(minute, -@geofenceFreshMinutes, SYSUTCDATETIME());

SELECT TOP (5000)
    t.PushTokenId,
    t.InstallId,
    t.Token
FROM SafetyPushTokens t
JOIN SafetyAlertSettings s ON t.InstallId = s.InstallId
LEFT JOIN SafetyGeofenceStates g ON t.InstallId = g.InstallId
WHERE t.Active = 1
  AND s.Enabled = 1
  AND s.StormEnabled = 1
  AND (
        s.GpsOnlyEnabled = 0
        OR (
            g.InstallId IS NOT NULL
            AND g.InGeofence = 1
            AND g.GeofenceKey = @geofenceKey
            AND g.UpdatedUtc >= @cutoff
        )
      )
  AND NOT EXISTS (
        SELECT 1
        FROM SafetyAlertDeliveries d
        WHERE d.EpisodeId = @episodeId
          AND d.PushTokenId = t.PushTokenId
      )
ORDER BY t.PushTokenId ASC;
";

        await using var conn = Open();
        var rows = await conn.QueryAsync<PushTargetRow>(new CommandDefinition(sql, new { episodeId, geofenceKey, geofenceFreshMinutes }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<List<PushTargetRow>> GetLightningTargetsForEpisodeAsync(
        long episodeId,
        string geofenceKey,
        int geofenceFreshMinutes,
        CancellationToken ct)
    {
        const string sql = @"
DECLARE @cutoff datetime2 = DATEADD(minute, -@geofenceFreshMinutes, SYSUTCDATETIME());

SELECT TOP (5000)
    t.PushTokenId,
    t.InstallId,
    t.Token
FROM SafetyPushTokens t
JOIN SafetyAlertSettings s ON t.InstallId = s.InstallId
LEFT JOIN SafetyGeofenceStates g ON t.InstallId = g.InstallId
WHERE t.Active = 1
  AND s.Enabled = 1
  AND s.LightningEnabled = 1
  AND (
        s.GpsOnlyEnabled = 0
        OR (
            g.InstallId IS NOT NULL
            AND g.InGeofence = 1
            AND g.GeofenceKey = @geofenceKey
            AND g.UpdatedUtc >= @cutoff
        )
      )
  AND NOT EXISTS (
        SELECT 1
        FROM SafetyAlertDeliveries d
        WHERE d.EpisodeId = @episodeId
          AND d.PushTokenId = t.PushTokenId
      )
ORDER BY t.PushTokenId ASC;
";

        await using var conn = Open();
        var rows = await conn.QueryAsync<PushTargetRow>(new CommandDefinition(sql, new { episodeId, geofenceKey, geofenceFreshMinutes }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<List<DeliveryRow>> CreatePendingDeliveriesAsync(long episodeId, IReadOnlyList<PushTargetRow> targets, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO SafetyAlertDeliveries (EpisodeId, PushTokenId, InstallId, Status, Attempt, SentUtc, AckUtc, Error, ResponseJson, CreatedUtc, UpdatedUtc)
OUTPUT INSERTED.DeliveryId, INSERTED.PushTokenId, INSERTED.InstallId
VALUES (@episodeId, @pushTokenId, @installId, 'pending', 1, NULL, NULL, NULL, NULL, SYSUTCDATETIME(), SYSUTCDATETIME());
";

        var list = new List<DeliveryRow>(targets.Count);

        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            foreach (var t in targets)
            {
                var row = await conn.QuerySingleAsync<(long DeliveryId, long PushTokenId, Guid InstallId)>(
                    new CommandDefinition(sql, new { episodeId, pushTokenId = t.PushTokenId, installId = t.InstallId }, transaction: tx, cancellationToken: ct)
                );

                list.Add(new DeliveryRow(row.DeliveryId, row.PushTokenId, row.InstallId, t.Token, TicketId: null));
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return list;
    }

    public async Task UpdateDeliveryAsync(long deliveryId, string status, DateTime? sentUtc, string? error, string? responseJson, CancellationToken ct)
    {
        const string sql = @"
UPDATE SafetyAlertDeliveries
SET Status = @status,
    SentUtc = COALESCE(@sentUtc, SentUtc),
    Error = @error,
    ResponseJson = @responseJson,
    UpdatedUtc = SYSUTCDATETIME()
WHERE DeliveryId = @deliveryId;
";

        await using var conn = Open();
        await conn.ExecuteAsync(new CommandDefinition(sql, new { deliveryId, status, sentUtc, error, responseJson }, cancellationToken: ct));
    }

    public async Task<List<SentDeliveryForReceiptRow>> GetRecentSentDeliveriesForReceiptsAsync(int lookbackMinutes, CancellationToken ct)
    {
        const string sql = @"
SELECT TOP (5000)
    DeliveryId,
    PushTokenId,
    InstallId,
    ResponseJson,
    UpdatedUtc
FROM SafetyAlertDeliveries
WHERE Status = 'sent'
  AND ResponseJson IS NOT NULL
  AND UpdatedUtc >= DATEADD(minute, -@lookbackMinutes, SYSUTCDATETIME())
ORDER BY UpdatedUtc DESC;
";

        await using var conn = Open();
        var rows = await conn.QueryAsync<SentDeliveryForReceiptRow>(new CommandDefinition(sql, new { lookbackMinutes }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task DeactivatePushTokenAsync(long pushTokenId, CancellationToken ct)
    {
        const string sql = @"
UPDATE SafetyPushTokens
SET Active = 0,
    UpdatedUtc = SYSUTCDATETIME()
WHERE PushTokenId = @pushTokenId;
";

        await using var conn = Open();
        await conn.ExecuteAsync(new CommandDefinition(sql, new { pushTokenId }, cancellationToken: ct));
    }

    public async Task<List<PushTargetRow>> GetRecipientsFromEpisodeAsync(long episodeId, CancellationToken ct)
    {
        const string sql = @"
SELECT DISTINCT
    t.PushTokenId,
    t.InstallId,
    t.Token
FROM SafetyAlertDeliveries d
JOIN SafetyPushTokens t ON d.PushTokenId = t.PushTokenId
WHERE d.EpisodeId = @episodeId
  AND d.Status IN ('sent','ack','pending')
  AND t.Active = 1;
";

        await using var conn = Open();
        var rows = await conn.QueryAsync<PushTargetRow>(new CommandDefinition(sql, new { episodeId }, cancellationToken: ct));
        return rows.ToList();
    }
}
