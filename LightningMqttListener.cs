using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;

namespace MorseSafetyAlerts;

public class LightningMqttListener : BackgroundService
{
    private readonly ILogger<LightningMqttListener> _log;
    private readonly AlertsOptions _opt;
    private readonly LightningStrikeWindow _window;

    private IMqttClient? _client;

    public LightningMqttListener(ILogger<LightningMqttListener> log, AlertsOptions opt, LightningStrikeWindow window)
    {
        _log = log;
        _opt = opt;
        _window = window;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mqttFactory = new MqttFactory();
        _client = mqttFactory.CreateMqttClient();

        _client.ApplicationMessageReceivedAsync += e =>
        {
            try
            {
                var payload = e.ApplicationMessage?.PayloadSegment.ToArray();
                if (payload == null || payload.Length == 0) return Task.CompletedTask;

                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                var latlon = ParseLatLon(root);
                if (latlon == null) return Task.CompletedTask;

                var eventMs = ParseEventTimeMs(root);

                _window.AddStrike(eventMs, latlon.Value.lat, latlon.Value.lon);
            }
            catch
            {
                // ignore malformed payloads
            }

            return Task.CompletedTask;
        };

        _client.DisconnectedAsync += async e =>
        {
            if (stoppingToken.IsCancellationRequested) return;
            _log.LogWarning("Lightning MQTT disconnected. Reconnecting in 5s...");
            try { await Task.Delay(5000, stoppingToken); } catch { }

            try
            {
                if (_client?.Options != null)
                    await _client.ConnectAsync(_client.Options, stoppingToken);
            }
            catch
            {
                // outer loop will retry
            }
        };

        var options = new MqttClientOptionsBuilder()
            .WithClientId("morse-safety-alerts")
            .WithTcpServer(_opt.Lightning.MqttHost, _opt.Lightning.MqttPort)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
            .Build();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _log.LogInformation("Connecting to lightning MQTT {Host}:{Port}...", _opt.Lightning.MqttHost, _opt.Lightning.MqttPort);
                await _client.ConnectAsync(options, stoppingToken);

                foreach (var t in _opt.Lightning.Topics)
                {
                    await _client.SubscribeAsync(t, cancellationToken: stoppingToken);
                }

                _log.LogInformation("Subscribed to {Count} lightning topics.", _opt.Lightning.Topics.Count);

                // stay connected until disconnected/cancelled
                while (!stoppingToken.IsCancellationRequested && _client.IsConnected)
                {
                    await Task.Delay(10_000, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Lightning MQTT loop error; retrying in 10s");
                try { await Task.Delay(10_000, stoppingToken); } catch { }
            }
        }
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static long ParseEventTimeMs(JsonElement root)
    {
        // payload may have 'time' in ns or ms or seconds; best-effort
        if (!root.TryGetProperty("time", out var tEl))
            return NowMs();

        if (tEl.ValueKind == JsonValueKind.Number)
        {
            if (tEl.TryGetInt64(out var t))
            {
                if (t > 1_000_000_000_000_000) return t / 1_000_000; // ns -> ms
                if (t > 10_000_000_000) return t; // already ms
                if (t > 1_000_000_000) return t * 1000; // seconds
            }
        }

        return NowMs();
    }

    private static (double lat, double lon)? ParseLatLon(JsonElement root)
    {
        if (!root.TryGetProperty("lat", out var latEl)) return null;
        if (!root.TryGetProperty("lon", out var lonEl)) return null;
        if (latEl.ValueKind != JsonValueKind.Number || lonEl.ValueKind != JsonValueKind.Number) return null;

        var lat = latEl.GetDouble();
        var lon = lonEl.GetDouble();
        return (lat, lon);
    }
}
