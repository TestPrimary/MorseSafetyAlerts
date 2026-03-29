namespace MorseSafetyAlerts;

public class AlertsOptions
{
    public int TickSeconds { get; set; } = 60;

    public string GeofenceKey { get; set; } = "morse";

    // If GpsOnlyEnabled=true, geofence state must be updated within this window.
    public int GeofenceFreshMinutes { get; set; } = 30;

    public NwsOptions Nws { get; set; } = new();

    public LightningOptions Lightning { get; set; } = new();

    public ExpoPushOptions Expo { get; set; } = new();
}

public class LightningOptions
{
    // Blitzortung MQTT
    public string MqttHost { get; set; } = "blitzortung.ha.sed.pl";

    public int MqttPort { get; set; } = 1883;

    public List<string> Topics { get; set; } = new()
    {
        "blitzortung/1.1/d/p/4/#",
        "blitzortung/1.1/d/p/5/#",
    };

    // Morse center
    public double CenterLat { get; set; } = 40.10321;

    public double CenterLon { get; set; } = -86.03882;

    public double RadiusMiles { get; set; } = 15;

    public int WindowMinutes { get; set; } = 10;

    public int TriggerCount { get; set; } = 2;
}

public class NwsOptions
{
    public string ActiveAlertsUrl { get; set; } = "https://api.weather.gov/alerts/active?point=40.10321,-86.03882";

    // NWS requires a User-Agent with contact info.
    public string UserAgent { get; set; } = "MorseBoaters SafetyAlerts (contact: jimstryjewski@gmail.com)";

    // If non-empty, only these NWS event names are considered storm alerts.
    public List<string> StormEventAllowlist { get; set; } = new()
    {
        "Severe Thunderstorm Warning",
        "Tornado Warning",
        "Severe Thunderstorm Watch",
        "Tornado Watch",
        "Flash Flood Warning",
        "Flood Warning",
        "Special Marine Warning",
        "Severe Weather Statement",
    };
}

public class ExpoPushOptions
{
    public string PushSendUrl { get; set; } = "https://exp.host/--/api/v2/push/send";

    public string PushReceiptsUrl { get; set; } = "https://exp.host/--/api/v2/push/getReceipts";

    public int BatchSize { get; set; } = 100;

    public int ReceiptBatchSize { get; set; } = 500;

    public string DefaultSound { get; set; } = "default";
}
