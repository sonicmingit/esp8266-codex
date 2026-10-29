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
