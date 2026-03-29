using System.Text.Json;

namespace MorseSafetyAlerts;

public class ServiceState
{
    public string? NwsEtag { get; set; }

    public bool? LastStormActive { get; set; }

    // Lightning rolling window state (best-effort persistence across restarts)
    public List<long> LightningRecentStrikeMs { get; set; } = new();

    public long LightningLastStrikeMs { get; set; } = 0;

    // TicketId -> processedUtc (used to avoid re-querying Expo receipts forever)
    public Dictionary<string, DateTime> ProcessedReceiptTicketsUtc { get; set; } = new();

    public DateTime UpdatedUtc { get; set; }
}

public class StateStore
{
    private readonly string _path;
    private readonly ILogger<StateStore> _log;

    public StateStore(string path, ILogger<StateStore> log)
    {
        _path = path;
        _log = log;
    }

    public ServiceState Load()
    {
        try
        {
            if (!File.Exists(_path)) return new ServiceState();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<ServiceState>(json) ?? new ServiceState();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load state file {Path}", _path);
            return new ServiceState();
        }
    }

    public void Save(ServiceState state)
    {
        try
        {
            state.UpdatedUtc = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to save state file {Path}", _path);
        }
    }
}
