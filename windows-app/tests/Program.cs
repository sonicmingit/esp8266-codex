using System.Reflection;
using AIClockBridge;

// Synthetic quota only: no credentials, HTTP requests, or settings writes.
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
var flags = BindingFlags.Static | BindingFlags.NonPublic;
var parse = typeof(UsageFetcher).GetMethod("ResetEpoch", flags)!;
var reset = (double)parse.Invoke(null, new object[] { "2027-01-01T00:30:00+08:00" })!;
Check(reset == new DateTimeOffset(2026, 12, 31, 16, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
    "Lost the reset instant across a timezone/year boundary");
Console.WriteLine("PASS reset instant preserves timezone and year boundary");

var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + 61;
var quota = new ProviderUsage { PrimaryPct = 25, PrimaryResetAt = at };
Check(quota.PrimaryResetMin == 1, "Initial countdown mismatch");
Thread.Sleep(2100);
Check(quota.PrimaryResetMin == 0, "Cached countdown did not advance between API fetches");
Check(quota.PrimaryResetAt == at, "Reset instant moved while countdown ticked");
Console.WriteLine("PASS cached countdown ticks while reset instant stays fixed");

var merge = typeof(UsageFetcher).GetMethod("Merge", flags)!;
foreach (var old in new[] {
    new ProviderUsage { PrimaryPct = 25, PrimaryResetAt = at },
    new ProviderUsage { WeeklyPct = 99, WeeklyResetAt = at }
})
{
    var kept = (ProviderUsage)merge.Invoke(null, new object[] { old, new ProviderUsage { Error = "network unavailable" } })!;
    Check(kept.PrimaryResetAt == old.PrimaryResetAt && kept.WeeklyResetAt == old.WeeklyResetAt,
        "Failed refresh moved the cached reset instant");
    Check(kept.PrimaryPct == old.PrimaryPct && kept.WeeklyPct == old.WeeklyPct && kept.Error != null,
        "Failed refresh discarded known quota");
}
Console.WriteLine("PASS failed refresh retains both short-window and weekly-only deadlines");

var expired = new ProviderUsage { WeeklyResetAt = at - 3600 };
Check(expired.WeeklyResetMin == 0 && expired.WeeklyResetAt == at - 3600,
    "Expired countdown changed the original reset date");
Check(new ProviderUsage().PrimaryResetMin == null, "Unknown reset became a date");
Console.WriteLine("PASS expired and unknown reset handling");

foreach (var (status, expected) in new[] {
    (new CodexStatus { WeeklyPct = 1, WeeklyWindowMin = 10080 }, false),
    (new CodexStatus { PrimaryWindowMin = 300, WeeklyPct = 1 }, false),
    (new CodexStatus(), false),
    (new CodexStatus { PrimaryPct = -1, PrimaryWindowMin = 300 }, false),
    (new CodexStatus { PrimaryPct = 24, PrimaryWindowMin = 10080 }, false),
    (new CodexStatus { PrimaryPct = 24, PrimaryWindowMin = 0 }, false),
    (new CodexStatus { PrimaryPct = 24, PrimaryWindowMin = 1440 }, false),
    (new CodexStatus { PrimaryPct = 0, PrimaryWindowMin = 300 }, true),
    (new CodexStatus { PrimaryPct = 100, PrimaryWindowMin = 300 }, true),
    (new CodexStatus { PrimaryPct = 24 }, true), // Legacy bridge without duration
})
    Check(status.HasShortWindow == expected, "Mirror selected a missing or misclassified 5h window");
Console.WriteLine("PASS weekly-only, unknown, legacy and real 5h window selection");

// Real requests: a watch may read status without replacing the paired hardware.
var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
probe.Start();
var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
probe.Stop();
var http = new MiniHttpServer(port, new() { ["/status"] = () => System.Text.Encoding.UTF8.GetBytes("{\"codex\":{}}") });
var visits = new List<string>();
http.OnRequest = (path, ip) => { lock (visits) visits.Add(path); };
http.Rebind(System.Net.IPAddress.Loopback);
using var client = new HttpClient();
var watchJson = await client.GetStringAsync($"http://127.0.0.1:{port}/status?client=watch");
Check(watchJson.Contains("codex") && visits.Count == 0, "Watch was reported as a pairable hardware device");
await client.GetStringAsync($"http://127.0.0.1:{port}/status");
Check(visits.Count == 1 && visits[0] == "/status", "Hardware passive discovery stopped working");
await client.GetStringAsync($"http://127.0.0.1:{port}/status?x=1&client=watch");
Check(visits.Count == 1, "Viewer marker failed with other query parameters");
await client.GetStringAsync($"http://127.0.0.1:{port}/status?client=watchdog");
Check(visits.Count == 2, "Unrelated query suppressed hardware discovery");
Console.WriteLine("PASS watch HTTP viewer preserves hardware passive discovery");
