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
    // Legacy readonly path is retained as CodexHome so existing settings migrate cleanly.
    public string CodexHome { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    public string CodexManagedHome { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aiclock", "codex");
    public string ClaudeHome { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    public int RefreshSeconds { get; init; } = 120;
    public MessageSchedule MessageSchedule { get; init; } = new();
}

public sealed record MessageSchedule
{
    public bool Enabled { get; init; }
    public string Time { get; init; } = "08:00";
    public string Model { get; init; } = "";
    public string Message { get; init; } = "只回复 OK。";
}

public sealed record CodexModel(string Id, string Name, bool IsDefault);
public sealed record MessageRun
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Source { get; init; } = "scheduled";
    public string Model { get; init; } = "";
    public string Message { get; init; } = "";
    public string Status { get; init; } = "running";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? Reply { get; init; }
    public string? Error { get; init; }
    public QuotaWindow? Primary { get; init; }
    public QuotaWindow? Weekly { get; init; }
    public string? QuotaError { get; init; }
}
public sealed record MessageHistory
{
    public string? LastScheduledDate { get; init; }
    public List<MessageRun> Runs { get; init; } = [];
}
public sealed record CodexMessageResult(string Reply, QuotaWindow? Primary, QuotaWindow? Weekly, string? QuotaError);

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
