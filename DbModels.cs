namespace MorseSafetyAlerts;

public record ActiveEpisodeRow(
    long EpisodeId,
    string AlertType,
    string? GeofenceKey,
    string? Title,
    string? Message,
    byte Severity,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    DateTime CreatedUtc,
    DateTime UpdatedUtc
);

public record PushTargetRow(
    long PushTokenId,
    Guid InstallId,
    string Token
);

public record DeliveryRow(
    long DeliveryId,
    long PushTokenId,
    Guid InstallId,
    string Token,
    string? TicketId
);

public record SentDeliveryForReceiptRow(
    long DeliveryId,
    long PushTokenId,
    Guid InstallId,
    string ResponseJson,
    DateTime UpdatedUtc
);
