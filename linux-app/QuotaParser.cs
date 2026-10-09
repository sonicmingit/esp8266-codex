using System.Text.Json;

namespace AIClockBridge.Linux;

public static class QuotaParser
{
    public static (QuotaWindow? Primary, QuotaWindow? Weekly) Codex(JsonElement root, bool rpc = false)
    {
        JsonElement limits;
        if (rpc)
        {
            var buckets = JsonValues.Object(root, "rateLimitsByLimitId");
            limits = (buckets.HasValue ? JsonValues.Object(buckets.Value, "codex") : null)
                ?? JsonValues.Object(root, "rateLimits") ?? throw new BridgeException("Codex 未返回额度窗口。", 502);
        }
        else limits = JsonValues.Object(root, "rate_limit") ?? throw new BridgeException("Codex 额度响应格式不兼容。", 502);
        QuotaWindow? primary = null, weekly = null;
        foreach (var (key, fallback) in rpc ? new[] { ("primary", 300), ("secondary", 10080) }
                                          : new[] { ("primary_window", 300), ("secondary_window", 10080) })
        {
            var window = JsonValues.Object(limits, key);
            if (!window.HasValue) continue;
            var w = window.Value;
            var percent = JsonValues.Number(w, rpc ? "usedPercent" : "used_percent");
            if (!percent.HasValue) continue;
            var minutes = rpc ? JsonValues.Number(w, "windowDurationMins") : JsonValues.Number(w, "limit_window_seconds") / 60;
            var reset = JsonValues.Number(w, rpc ? "resetsAt" : "reset_at");
            var parsed = new QuotaWindow(Math.Clamp(percent.Value, 0, 100), Math.Max(1, (int)(minutes ?? fallback)), reset.HasValue ? (long)reset.Value : null);
            if (parsed.WindowMinutes >= 2880) weekly ??= parsed; else primary ??= parsed;
        }
        if (primary == null && weekly == null) throw new BridgeException("当前账号未返回可用额度窗口。", 502);
        return (primary, weekly);
    }

    public static (QuotaWindow? Primary, QuotaWindow? Weekly) Claude(JsonElement root)
    {
        QuotaWindow? Parse(string name, int minutes)
        {
            var w = JsonValues.Object(root, name);
            if (!w.HasValue) return null;
            var percent = JsonValues.Number(w.Value, "utilization");
            if (!percent.HasValue) return null;
            long? reset = DateTimeOffset.TryParse(JsonValues.String(w.Value, "resets_at"), out var time) ? time.ToUnixTimeSeconds() : null;
            return new(Math.Clamp(percent.Value, 0, 100), minutes, reset);
        }
        var result = (Parse("five_hour", 300), Parse("seven_day", 10080));
        if (result.Item1 == null && result.Item2 == null) throw new BridgeException("Claude 额度响应格式不兼容。", 502);
        return result;
    }
}
