using System.Text.Json;

namespace MorseSafetyAlerts;

public record ServiceStatus(
    DateTime StartedUtc,
    DateTime? LastTickStartUtc,
    DateTime? LastTickEndUtc,
    bool? LastStormActive,
    long? ActiveStormEpisodeId,
    int? LastTargetsCount,
    int? LastSentCount,
    string? LastError
);

public class StatusWriter
{
    private readonly string _path;
    private readonly ILogger<StatusWriter> _log;

    public StatusWriter(string path, ILogger<StatusWriter> log)
    {
        _path = path;
        _log = log;
    }

    public void Write(ServiceStatus status)
    {
        try
        {
            var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to write status file {Path}", _path);
        }
    }
}
