using System.Net.Http.Json;
using System.Text.Json;

namespace MorseSafetyAlerts;

public class ExpoPushClient
{
    private readonly HttpClient _http;
    private readonly AlertsOptions _opt;
    private readonly ILogger<ExpoPushClient> _log;

    public ExpoPushClient(HttpClient http, AlertsOptions opt, ILogger<ExpoPushClient> log)
    {
        _http = http;
        _opt = opt;
        _log = log;
    }

    public async Task<ExpoSendResult> SendAsync(
        IReadOnlyList<string> expoPushTokens,
        string title,
        string body,
        string sound,
        object? data,
        CancellationToken ct)
    {
        if (expoPushTokens.Count == 0)
        {
            return new ExpoSendResult(true, "{\"data\":[]}", Array.Empty<ExpoPushTicket>());
        }

        var payload = expoPushTokens.Select(t => new Dictionary<string, object?>
        {
            ["to"] = t,
            ["title"] = title,
            ["body"] = body,
            ["sound"] = sound,
            ["priority"] = "high",
            ["data"] = data,
        }).ToList();

        using var req = new HttpRequestMessage(HttpMethod.Post, _opt.Expo.PushSendUrl)
        {
            Content = JsonContent.Create(payload),
        };

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Expo push send HTTP {Status}: {Raw}", (int)resp.StatusCode, raw);
            return new ExpoSendResult(false, raw, Array.Empty<ExpoPushTicket>());
        }

        var parsed = JsonSerializer.Deserialize<ExpoPushTicketResponse>(raw, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        var tickets = parsed?.Data ?? new List<ExpoPushTicket>();

        return new ExpoSendResult(true, raw, tickets);
    }
}
