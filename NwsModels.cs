using System.Text.Json.Serialization;

namespace MorseSafetyAlerts;

public class NwsActiveAlertsResponse
{
    [JsonPropertyName("features")]
    public List<NwsFeature> Features { get; set; } = new();
}

public class NwsFeature
{
    [JsonPropertyName("properties")]
    public NwsProperties Properties { get; set; } = new();
}

public class NwsProperties
{
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    [JsonPropertyName("headline")]
    public string? Headline { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("sent")]
    public DateTimeOffset? Sent { get; set; }

    [JsonPropertyName("effective")]
    public DateTimeOffset? Effective { get; set; }

    [JsonPropertyName("ends")]
    public DateTimeOffset? Ends { get; set; }

    [JsonPropertyName("expires")]
    public DateTimeOffset? Expires { get; set; }
}

public record NwsStormStatus(
    bool Active,
    byte Severity,
    string Title,
    string Message,
    IReadOnlyList<string> Events,
    string? ETag
);
