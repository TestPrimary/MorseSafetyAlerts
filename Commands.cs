using System.Text.Json;

namespace MorseSafetyAlerts;

public record ServiceCommands(
    int? SimulateLightningStrikes,
    bool? ClearLightningWindow
);

public class CommandProcessor
{
    private readonly string _commandsPath;
    private readonly string _archivePath;
    private readonly ILogger<CommandProcessor> _log;

    public CommandProcessor(string commandsPath, ILogger<CommandProcessor> log)
    {
        _commandsPath = commandsPath;
        _archivePath = Path.Combine(Path.GetDirectoryName(commandsPath) ?? AppContext.BaseDirectory, "commands.last.json");
        _log = log;
    }

    public ServiceCommands? TryLoadAndArchive()
    {
        try
        {
            if (!File.Exists(_commandsPath)) return null;

            var json = File.ReadAllText(_commandsPath);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var cmd = JsonSerializer.Deserialize<ServiceCommands>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            // Move aside so we don't re-run it every tick.
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_commandsPath) ?? ".");
                File.Move(_commandsPath, _archivePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to archive commands file; will attempt to delete");
                try { File.Delete(_commandsPath); } catch { }
            }

            return cmd;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load commands file");
            return null;
        }
    }
}
