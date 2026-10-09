using System.Text.Json;

namespace AIClockBridge.Linux;

public sealed record BridgeSettings
{
    public int Port { get; init; } = 8765;
    // Kept to migrate settings created by the initial single-device bridge.
    public string DeviceHost { get; init; } = "";
    public List<DeviceEntry> Devices { get; init; } = [];
    public string AdvertisedHost { get; init; } = "";
    public bool CodexEnabled { get; init; } = true;
    public bool ClaudeEnabled { get; init; }
    public string CodexMode { get; init; } = "readonly";
    public string CodexHome { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    public string ClaudeHome { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    public int RefreshSeconds { get; init; } = 120;
}

public sealed record DeviceEntry(string Id, string Name, string Host)
{
    public int DayBrightness { get; init; } = 100;
    public bool PageScheduleEnabled { get; init; }
    public string PageMode { get; init; } = "auto";
    public string PageOnTime { get; init; } = "08:00";
    public string PageOffTime { get; init; } = "22:00";
    public int PageOffBrightness { get; init; }
    public bool NightModeEnabled { get; init; }
    public string NightStart { get; init; } = "22:00";
    public string NightEnd { get; init; } = "08:00";
    public int NightBrightness { get; init; } = 25;
}

public sealed record QuotaWindow(double UsedPercent, int WindowMinutes, long? ResetsAt);
public sealed record UsageState
{
    public QuotaWindow? Primary { get; init; }
    public QuotaWindow? Weekly { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }
    public DateTimeOffset? AttemptedAt { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public string? Error { get; init; }
    public bool Enabled { get; init; }
    public bool Stale { get; init; } = true;
}

public sealed class BridgeException(string message, int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class JsonValues
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string? String(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    public static double? Number(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    public static JsonElement? Object(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Object ? p : null;
}
