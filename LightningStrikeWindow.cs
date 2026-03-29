using System.Security.Cryptography;
using System.Text;

namespace MorseSafetyAlerts;

public record LightningSnapshot(
    int CountInWindow,
    long LastStrikeMs,
    long WindowMs
);

public class LightningStrikeWindow
{
    private readonly LightningOptions _opt;
    private readonly object _lock = new();

    // event timestamps (ms)
    private readonly Queue<long> _recent = new();

    // de-dupe by hashed strike id (rounded + seconds)
    private readonly Dictionary<string, long> _seen = new(); // id -> seenAtMs

    private long _lastStrikeMs;

    public LightningStrikeWindow(LightningOptions opt)
    {
        _opt = opt;
    }

    public void SeedFromState(IEnumerable<long> recentMs, long lastStrikeMs)
    {
        lock (_lock)
        {
            _recent.Clear();
            foreach (var t in recentMs.OrderBy(x => x)) _recent.Enqueue(t);
            _lastStrikeMs = lastStrikeMs;
        }
    }

    public (List<long> RecentStrikeMs, long LastStrikeMs) ExportState(long nowMs)
    {
        lock (_lock)
        {
            Prune_NoLock(nowMs);
            return (_recent.ToList(), _lastStrikeMs);
        }
    }

    public void AddStrike(long eventMs, double lat, double lon)
    {
        var now = NowMs();

        // filter by radius
        var dMi = HaversineMiles(_opt.CenterLat, _opt.CenterLon, lat, lon);
        if (dMi > _opt.RadiusMiles) return;

        var id = HashId($"{Math.Round(lat, 4)}|{Math.Round(lon, 4)}|{eventMs / 1000}");

        lock (_lock)
        {
            Prune_NoLock(now);

            if (_seen.ContainsKey(id)) return;
            _seen[id] = now;

            _recent.Enqueue(eventMs);
            _lastStrikeMs = Math.Max(_lastStrikeMs, now);

            Prune_NoLock(now);
        }
    }

    public LightningSnapshot GetSnapshot(long nowMs)
    {
        lock (_lock)
        {
            Prune_NoLock(nowMs);
            return new LightningSnapshot(_recent.Count, _lastStrikeMs, WindowMs);
        }
    }

    public long WindowMs => _opt.WindowMinutes * 60_000L;

    private void Prune_NoLock(long nowMs)
    {
        var windowMs = WindowMs;
        while (_recent.Count > 0 && _recent.Peek() < nowMs - windowMs)
            _recent.Dequeue();

        // prune seen ids older than 30 minutes
        var cutoff = nowMs - (30 * 60_000L);
        var toRemove = _seen.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
        foreach (var k in toRemove) _seen.Remove(k);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string HashId(string s)
    {
        using var sha = SHA256.Create();
        var b = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(b);
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;

    private static double HaversineMiles(double lat1, double lon1, double lat2, double lon2)
    {
        const double Rm = 3958.7613; // earth radius miles
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
                * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Asin(Math.Sqrt(a));
        return Rm * c;
    }
}
