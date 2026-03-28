using System.Net;
using System.Text;
using System.Text.Json;

namespace MorseSafetyAlerts;

public class NwsClient
{
    private readonly HttpClient _http;
    private readonly AlertsOptions _opt;
    private readonly ILogger<NwsClient> _log;

    public NwsClient(HttpClient http, AlertsOptions opt, ILogger<NwsClient> log)
    {
        _http = http;
        _opt = opt;
        _log = log;
    }

    public async Task<NwsStormStatus> GetStormStatusAsync(string? previousEtag, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _opt.Nws.ActiveAlertsUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", _opt.Nws.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "application/geo+json, application/json");
        if (!string.IsNullOrWhiteSpace(previousEtag))
        {
            req.Headers.TryAddWithoutValidation("If-None-Match", previousEtag);
        }

        using var resp = await _http.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.NotModified)
        {
            return new NwsStormStatus(
                Active: false,
                Severity: 1,
                Title: "(not-modified)",
                Message: "",
                Events: Array.Empty<string>(),
                ETag: previousEtag
            );
        }

        resp.EnsureSuccessStatusCode();

        var etag = resp.Headers.ETag?.Tag;
        var json = await resp.Content.ReadAsStringAsync(ct);

        var data = JsonSerializer.Deserialize<NwsActiveAlertsResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? new NwsActiveAlertsResponse();

        var events = new List<string>();
        var headlines = new List<string>();
        var maxSeverity = (byte)1;

        foreach (var f in data.Features)
        {
            var evt = f?.Properties?.Event?.Trim();
            if (string.IsNullOrWhiteSpace(evt)) continue;

            if (_opt.Nws.StormEventAllowlist.Count > 0 && !_opt.Nws.StormEventAllowlist.Contains(evt, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            events.Add(evt);

            var hl = f?.Properties?.Headline?.Trim();
            if (!string.IsNullOrWhiteSpace(hl))
            {
                headlines.Add(hl);
            }

            var sev = NormalizeSeverity(f?.Properties?.Severity);
            if (sev > maxSeverity) maxSeverity = sev;
        }

        var active = events.Count > 0;

        var title = active
            ? $"Storm Alert ({events.Count})"
            : "No active storm alerts";

        var msg = active
            ? BuildMessage(events, headlines)
            : "";

        _log.LogInformation("NWS storm status: active={Active} events={Count} etag={ETag}", active, events.Count, etag);

        return new NwsStormStatus(
            Active: active,
            Severity: maxSeverity,
            Title: title,
            Message: msg,
            Events: events,
            ETag: etag
        );
    }

    private static string BuildMessage(List<string> events, List<string> headlines)
    {
        var sb = new StringBuilder();
        // De-dupe while preserving order.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        sb.AppendLine("Active alerts near Morse Reservoir:");

        foreach (var e in events)
        {
            if (!seen.Add(e)) continue;
            sb.Append("- ").AppendLine(e);
        }

        if (headlines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Details:");
            foreach (var hl in headlines.Take(3))
            {
                sb.Append("- ").AppendLine(hl);
            }
        }

        var s = sb.ToString().Trim();
        // Expo message body has practical limits; keep it short.
        if (s.Length > 900) s = s.Substring(0, 900) + "…";
        return s;
    }

    private static byte NormalizeSeverity(string? nwsSeverity)
    {
        // Map NWS severity strings to our 1..5.
        return nwsSeverity?.Trim().ToLowerInvariant() switch
        {
            "extreme" => 5,
            "severe" => 4,
            "moderate" => 3,
            "minor" => 2,
            _ => 1,
        };
    }
}
