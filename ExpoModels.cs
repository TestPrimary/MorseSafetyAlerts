using System.Text.Json.Serialization;

namespace MorseSafetyAlerts;

public class ExpoPushTicketResponse
{
    [JsonPropertyName("data")]
    public List<ExpoPushTicket>? Data { get; set; }

    [JsonPropertyName("errors")]
    public List<ExpoError>? Errors { get; set; }
}

public class ExpoPushTicket
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("details")]
    public Dictionary<string, object>? Details { get; set; }
}

public class ExpoError
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public record ExpoSendResult(
    bool Ok,
    string RawJson,
    IReadOnlyList<ExpoPushTicket> Tickets
);
