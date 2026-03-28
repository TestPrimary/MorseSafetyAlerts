using System.Text.Json.Serialization;

namespace MorseSafetyAlerts;

public class ExpoReceiptsResponse
{
    [JsonPropertyName("data")]
    public Dictionary<string, ExpoReceipt>? Data { get; set; }

    [JsonPropertyName("errors")]
    public List<ExpoError>? Errors { get; set; }
}

public class ExpoReceipt
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("details")]
    public Dictionary<string, object>? Details { get; set; }
}
