using System.Net;
using System.Text;
using System.Text.Json;
using AIClockBridge.Linux;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Microsoft.Extensions.Logging.Abstractions;

// Dependency-free integration runner. All credentials, HTTP responses and
// app-server messages are synthetic; tests never contact account APIs.
if (args.FirstOrDefault() == "app-server")
{
    var home = Environment.GetEnvironmentVariable("CODEX_HOME")!;
    var calls = Path.Combine(home, "rpc-calls.txt");
    var scenario = Environment.GetEnvironmentVariable("AICLOCK_FAKE_RPC") ?? "refresh";
    var refreshed = false;
    if (Environment.GetEnvironmentVariable("OPENAI_API_KEY") != null) Environment.Exit(10);
    if (scenario.StartsWith("startup-"))
    {
        Console.Error.WriteLine(scenario switch
        {
            "startup-permission" => "could not find bubblewrap on PATH; Error: Permission denied (os error 13) secret-must-not-leak",
            "startup-bubblewrap" => "Codex could not find bubblewrap on PATH secret-must-not-leak",
            _ => "Error loading config: TOML parse error secret-must-not-leak"
        });
        Environment.Exit(23);
    }
    while (Console.ReadLine() is { } line)
    {
        using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
        var method = root.GetProperty("method").GetString();
        File.AppendAllText(calls, method + "\n");
        if (!root.TryGetProperty("id", out var id)) continue;
        if (scenario == "exit") Environment.Exit(2);
        object? result = new { }; object? error = null;
        if (method == "account/read")
        {
            if (root.GetProperty("params").GetProperty("refreshToken").GetBoolean())
            { refreshed = true; File.AppendAllText(calls, "refreshed\n"); }
            result = new { account = new { type = "chatgpt" } };
        }
        if (method == "model/list")
        {
            if (scenario == "message-model-error") error = new { code = -1, message = "secret-must-not-leak" };
            else result = new { data = new[] { new { id = "picker-id", model = "test-model", displayName = "Test Model", isDefault = true, inputModalities = new[] { "text" } } }, nextCursor = (string?)null };
        }
        if (method == "account/rateLimits/read")
        {
            if (scenario == "error") error = new { code = -1, message = "secret-must-not-leak" };
            else if (scenario == "message-quota-error") error = new { code = -1, message = "secret-must-not-leak" };
            else if (!refreshed && !scenario.StartsWith("message")) error = new { code = -1, message = "401 unauthorized secret-must-not-leak" };
            else result = new { rateLimits = new { primary = new { usedPercent = 27, windowDurationMins = 300, resetsAt = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds() } } };
        }
        if (method is "thread/start" or "turn/start")
        {
            if (!scenario.StartsWith("message")) Environment.Exit(11);
            var parameters = root.GetProperty("params");
            if (method == "thread/start")
            {
                if (!parameters.GetProperty("ephemeral").GetBoolean() || parameters.GetProperty("sandbox").GetString() != "read-only"
                    || parameters.GetProperty("model").GetString() != "test-model" || parameters.GetProperty("cwd").GetString() == home) Environment.Exit(12);
                result = new { thread = new { id = "test-thread" } };
            }
            else
            {
                if (parameters.GetProperty("input")[0].GetProperty("text").GetString() != "只回复 OK。") Environment.Exit(13);
                if (scenario == "message-tool")
                    Console.WriteLine("{\"id\":100,\"method\":\"item/commandExecution/requestApproval\",\"params\":{}}");
                if (scenario == "message-slow") await Task.Delay(200);
                // Complete before the turn/start acknowledgement, with unrelated
                // notifications mixed in, to test the streaming race.
                Console.WriteLine(JsonSerializer.Serialize(new { method = "item/completed", @params = new { threadId = "other-thread", turnId = "other-turn", item = new { type = "agentMessage", id = "other", text = "WRONG" } } }));
                var item = new { type = "agentMessage", id = "reply", phase = "final_answer", text = "OK <b>literal</b>" };
                Console.WriteLine(JsonSerializer.Serialize(new { method = "item/completed", @params = new { threadId = "test-thread", turnId = "test-turn", item } }));
                Console.WriteLine(JsonSerializer.Serialize(new { method = "turn/completed", @params = new { threadId = "test-thread", turn = new
                { id = "test-turn", status = scenario == "message-failed" ? "failed" : "completed", items = new[] { item }, error = scenario == "message-failed" ? new { message = "429 usage limit secret-must-not-leak" } : null } } }));
                result = new { turn = new { id = "test-turn", status = "inProgress" } };
            }
        }
        Console.WriteLine("{\"method\":\"account/updated\",\"params\":{}}");
        Console.WriteLine(error == null ? JsonSerializer.Serialize(new { id = id.GetInt32(), result }) : JsonSerializer.Serialize(new { id = id.GetInt32(), error }));
    }
    return;
}

var tests = new List<(string Name, Func<Task> Run)>();
var rootDir = Path.Combine(Path.GetTempPath(), "aiclock-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(rootDir);
var envNames = new[] { "AICLOCK_DATA_DIR", "AICLOCK_PORT", "AICLOCK_CODEX_HOME", "AICLOCK_CLAUDE_HOME", "AICLOCK_CODEX_MODE", "AICLOCK_ADVERTISED_HOST", "AICLOCK_CODEX_EXECUTABLE", "AICLOCK_FAKE_RPC", "OPENAI_API_KEY" };
var savedEnv = envNames.ToDictionary(n => n, Environment.GetEnvironmentVariable);
foreach (var n in envNames) Environment.SetEnvironmentVariable(n, null);
const string weeklyJson = "{\"rate_limit\":{\"primary_window\":{\"used_percent\":42,\"limit_window_seconds\":604800,\"reset_at\":2000000000},\"secondary_window\":null}}";

SettingsStore Store(string name)
{
    var path = Path.Combine(rootDir, name); Directory.CreateDirectory(path);
    Environment.SetEnvironmentVariable("AICLOCK_DATA_DIR", path);
    Environment.SetEnvironmentVariable("AICLOCK_CODEX_EXECUTABLE", Environment.ProcessPath);
    var store = new SettingsStore();
    var codex = Path.Combine(path, "codex-readonly"); var managedCodex = Path.Combine(path, "codex-managed"); var claude = Path.Combine(path, "claude");
    Directory.CreateDirectory(codex); Directory.CreateDirectory(managedCodex); Directory.CreateDirectory(claude);
    File.WriteAllText(Path.Combine(codex, "auth.json"), "{\"tokens\":{\"access_token\":\"fake-access-one\",\"account_id\":\"fake-account\"}}");
    store.Save(store.Current with { CodexHome = codex, CodexManagedHome = managedCodex, ClaudeHome = claude, ClaudeEnabled = false }); return store;
}
void Check(bool value, string message) { if (!value) throw new Exception(message); }
async Task ExpectError(Func<Task> run, string part)
{
    try { await run(); throw new Exception("Expected failure"); }
    catch (BridgeException e) { Check(e.Message.Contains(part), "Unexpected safe error: " + e.Message); }
}
void Add(string name, Func<Task> run) => tests.Add((name, run));

Add("Codex weekly-only window and null secondary", () =>
{
    using var json = JsonDocument.Parse(weeklyJson); var (p, w) = QuotaParser.Codex(json.RootElement);
    Check(p == null && w?.UsedPercent == 42 && w.WindowMinutes == 10080, "Weekly classification failed"); return Task.CompletedTask;
});
Add("RPC prefers Codex bucket, uses actual durations", () =>
{
    using var json = JsonDocument.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":99}},\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":12,\"windowDurationMins\":300,\"resetsAt\":2000000000},\"secondary\":{\"usedPercent\":31,\"windowDurationMins\":10080}}}}" );
    var (p, w) = QuotaParser.Codex(json.RootElement, true); Check(p?.UsedPercent == 12 && w?.UsedPercent == 31, "Bucket mismatch"); return Task.CompletedTask;
});
Add("Claude percentages and absolute reset times", () =>
{
    using var json = JsonDocument.Parse("{\"five_hour\":{\"utilization\":8.5,\"resets_at\":\"2030-01-01T00:00:00Z\"},\"seven_day\":{\"utilization\":101}}" );
    var (p, w) = QuotaParser.Claude(json.RootElement); Check(p?.UsedPercent == 8.5 && p.ResetsAt == 1893456000 && w?.UsedPercent == 100, "Claude parse failed"); return Task.CompletedTask;
});
Add("Settings persist and invalid inputs are rejected", async () =>
{
    var s = Store("settings"); s.Save(s.Current with { Port = 8877, RefreshSeconds = 180, DeviceHost = "192.168.1.55" });
    var reload = new SettingsStore(); Check(reload.EffectivePort == 8877 && reload.Current.RefreshSeconds == 180, "Not persisted");
    await ExpectError(() => { s.Save(s.Current with { RefreshSeconds = 5 }); return Task.CompletedTask; }, "刷新间隔");
    await ExpectError(() => { SettingsStore.ValidateHost("192.168.1.2/path"); return Task.CompletedTask; }, "地址");
    await ExpectError(() => { s.Save(s.Current with { CodexHome = "~/.codex" }); return Task.CompletedTask; }, "绝对路径");
});
Add("Readonly credentials, cache and firmware protocol", async () =>
{
    var s = Store("readonly"); var clock = new TestClock(DateTimeOffset.UtcNow); var handler = new FakeHttp((request, _) =>
    { Check(request.Headers.Authorization?.Parameter == "fake-access-one", "Credential header missing"); return Task.FromResult(FakeHttp.Response(200, weeklyJson)); });
    using var service = new QuotaService(s, new HttpClient(handler), clock); await service.Refresh(true, default);
    var usage = service.Snapshot()["codex"]; Check(!usage.Stale && usage.Weekly?.UsedPercent == 42, "No usage");
    using var status = JsonDocument.Parse(JsonSerializer.Serialize(service.FirmwareStatus()));
    var epoch = status.RootElement.GetProperty("ts").GetInt64();
    var offset = status.RootElement.GetProperty("utc_offset_min").GetInt32();
    Check(DateTimeOffset.FromUnixTimeSeconds(epoch).ToOffset(TimeSpan.FromMinutes(offset)).DateTime
        == DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime, "Firmware timezone does not match bridge local time");
    Check(status.RootElement.GetProperty("codex").GetProperty("weekly_pct").GetDouble() == 42, "Firmware fields mismatch");
    Check(status.RootElement.GetProperty("codex").GetProperty("weekly_reset_at").GetInt64() == usage.Weekly!.ResetsAt,
        "Firmware lost the fixed reset instant");
    Check(status.RootElement.GetProperty("codex").GetProperty("primary_pct").ValueKind == JsonValueKind.Null, "Invented primary window");
    var cache = File.ReadAllText(Path.Combine(s.DataDirectory, "usage-cache.json")); Check(!cache.Contains("fake-access"), "Secret persisted in quota cache");
    clock.Advance(TimeSpan.FromSeconds(60));
    using var restart = new QuotaService(s, new HttpClient(new FakeHttp((_, _) => Task.FromResult(FakeHttp.Response(503, "")))), clock);
    await restart.Refresh(true, default); var stale = restart.Snapshot()["codex"];
    Check(stale.Stale && stale.Weekly?.UsedPercent == 42 && stale.Error != null, "Lost weekly-only cache on error");
});
Add("Mapped credentials are re-opened after file replacement", async () =>
{
    var s = Store("replace"); using (var first = new QuotaService(s, new HttpClient(new FakeHttp((_, _) => Task.FromResult(FakeHttp.Response(200, weeklyJson)))))) await first.Refresh(true, default);
    var auth = Path.Combine(s.Current.CodexHome, "auth.json"); File.WriteAllText(auth + ".new", "{\"tokens\":{\"access_token\":\"fake-access-two\"}}"); File.Move(auth + ".new", auth, true);
    using var fresh = new QuotaService(s, new HttpClient(new FakeHttp((request, _) =>
    { Check(request.Headers.Authorization?.Parameter == "fake-access-two", "Read stale file handle"); return Task.FromResult(FakeHttp.Response(200, weeklyJson)); })));
    Check(fresh.Snapshot()["codex"].FetchedAt == null, "Old account cache leaked"); await fresh.Refresh(true, default);
});
Add("429 backs off and manual refresh cannot bypass it", async () =>
{
    var s = Store("backoff"); var handler = new FakeHttp((_, _) => Task.FromResult(FakeHttp.Response(429, "")));
    using var service = new QuotaService(s, new HttpClient(handler)); await service.Refresh(true, default); await service.Refresh(true, default);
    Check(handler.Count == 1 && service.Snapshot()["codex"].NextAttemptAt > DateTimeOffset.UtcNow.AddMinutes(4), "Backoff bypassed");
});
Add("Background checks preserve the configured query interval", async () =>
{
    var s = Store("interval"); s.Save(s.Current with { RefreshSeconds = 180 });
    var handler = new FakeHttp((_, _) => Task.FromResult(FakeHttp.Response(200, weeklyJson)));
    using var service = new QuotaService(s, new HttpClient(handler)); await service.Refresh(false, default);
    var due = service.Snapshot()["codex"].NextAttemptAt;
    await service.Refresh(false, default); await service.Refresh(true, default);
    Check(handler.Count == 1 && service.Snapshot()["codex"].NextAttemptAt == due && due > DateTimeOffset.UtcNow.AddSeconds(170), "Cooldown shortened configured interval");
});
Add("Disabled provider and changed settings discard in-flight results", async () =>
{
    var s = Store("inflight"); var entered = new TaskCompletionSource(); var complete = new TaskCompletionSource();
    var handler = new FakeHttp(async (_, ct) => { entered.SetResult(); await complete.Task.WaitAsync(ct); return FakeHttp.Response(200, weeklyJson); });
    using var service = new QuotaService(s, new HttpClient(handler)); var pending = service.Refresh(true, default);
    await entered.Task; s.Save(s.Current with { CodexEnabled = false }); service.SettingsChanged(); complete.SetResult(); await pending;
    Check(!service.Snapshot()["codex"].Enabled && service.Snapshot()["codex"].Weekly == null, "Disabled data retained");
});
Add("Device edits preserve quota; interval edits reschedule polling", async () =>
{
    var s = Store("settings-preserve");
    using var service = new QuotaService(s, new HttpClient(new FakeHttp((_, _) => Task.FromResult(FakeHttp.Response(200, weeklyJson)))));
    await service.Refresh(false, default); var at = service.Snapshot()["codex"].FetchedAt;
    s.Save(s.Current with { DeviceHost = "192.168.1.50", RefreshSeconds = 240 }); service.SettingsChanged();
    var state = service.Snapshot()["codex"];
    Check(state.Weekly?.UsedPercent == 42 && state.FetchedAt == at && state.NextAttemptAt == at?.AddSeconds(240), "Unrelated settings cleared quota");
});
Add("Adaptive settings migrate, persist and validate bounds", async () =>
{
    var s = Store("adaptive-settings");
    var path = Path.Combine(s.DataDirectory, "settings.json");
    var old = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    foreach (var key in new[] { "adaptiveRefreshEnabled", "minRefreshSeconds", "maxRefreshSeconds", "refreshUnchangedThreshold" }) old.Remove(key);
    old["refreshSeconds"] = 900;
    File.WriteAllText(path, old.ToJsonString());
    var migrated = new SettingsStore().Current;
    Check(migrated.AdaptiveRefreshEnabled && migrated.RefreshSeconds == 900 && migrated.MinRefreshSeconds == 60
        && migrated.MaxRefreshSeconds == 600 && migrated.RefreshUnchangedThreshold == 3, "Legacy defaults lost");
    s.Save(migrated with { MinRefreshSeconds = 90, MaxRefreshSeconds = 3600, RefreshUnchangedThreshold = 5 });
    var saved = new SettingsStore().Current;
    Check(saved.MinRefreshSeconds == 90 && saved.MaxRefreshSeconds == 3600 && saved.RefreshUnchangedThreshold == 5, "Adaptive settings not persisted");
    foreach (var invalid in new[] { saved with { MinRefreshSeconds = 59 }, saved with { MaxRefreshSeconds = 86401 }, saved with { MinRefreshSeconds = 3601 } })
        await ExpectError(() => { s.Save(invalid); return Task.CompletedTask; }, "刷新间隔");
    foreach (var threshold in new[] { 0, 101 })
        await ExpectError(() => { s.Save(saved with { RefreshUnchangedThreshold = threshold }); return Task.CompletedTask; }, "无变化次数");
});
Add("Adaptive polling backs off after consecutive unchanged windows and speeds up on changes", async () =>
{
    var s = Store("adaptive-cadence"); var clock = new TestClock(DateTimeOffset.UtcNow);
    var percent = 42.0;
    var handler = new FakeHttp((_, _) => Task.FromResult(FakeHttp.Response(200,
        JsonSerializer.Serialize(new { rate_limit = new { primary_window = new { used_percent = percent, limit_window_seconds = 604800, reset_at = 2000000000 } } }))));
    using var service = new QuotaService(s, new HttpClient(handler), clock);
    await service.Refresh(false, default);
    Check(service.Snapshot()["codex"].CurrentRefreshSeconds == 120 && service.Snapshot()["codex"].UnchangedRefreshCount == 0, "First response treated as unchanged");
    async Task Next()
    {
        var due = service.Snapshot()["codex"].NextAttemptAt!.Value;
        clock.Advance(due - clock.GetUtcNow() - TimeSpan.FromSeconds(1));
        var count = handler.Count; await service.Refresh(false, default);
        Check(handler.Count == count && !service.Snapshot()["codex"].Stale, "Polled early or idle data marked stale");
        clock.Advance(TimeSpan.FromSeconds(1)); await service.Refresh(false, default);
        Check(handler.Count == count + 1, "Did not poll at adaptive deadline");
    }
    for (var i = 0; i < 12; i++) await Next();
    Check(service.Snapshot()["codex"].CurrentRefreshSeconds == 600, "Maximum interval not enforced");
    // A weekly-only change, including fractions below the rounded display precision,
    // must wake the cadence without inventing a primary window.
    foreach (var expected in new[] { 300, 150, 75, 60, 60 })
    {
        percent += 0.01; await Next(); var state = service.Snapshot()["codex"];
        Check(state.CurrentRefreshSeconds == expected && state.UnchangedRefreshCount == 0 && state.Primary == null, "Change did not shorten interval within bounds");
    }
});
Add("Codex and Claude learn independent refresh intervals", async () =>
{
    var s = Store("adaptive-independent"); s.Save(s.Current with { ClaudeEnabled = true });
    File.WriteAllText(Path.Combine(s.Current.ClaudeHome, ".credentials.json"), "{\"claudeAiOauth\":{\"accessToken\":\"fake-claude\"}}");
    var clock = new TestClock(DateTimeOffset.UtcNow); var percent = 0;
    var handler = new FakeHttp((request, _) => Task.FromResult(FakeHttp.Response(200, request.RequestUri!.Host == "chatgpt.com"
        ? JsonSerializer.Serialize(new { rate_limit = new { primary_window = new { used_percent = ++percent, limit_window_seconds = 18000, reset_at = 2000000000 } } })
        : "{\"five_hour\":{\"utilization\":8.5,\"resets_at\":\"2030-01-01T00:00:00Z\"}}")));
    using var service = new QuotaService(s, new HttpClient(handler), clock);
    await service.Refresh(false, default);
    for (var i = 0; i < 3; i++) { clock.Advance(TimeSpan.FromSeconds(120)); await service.Refresh(false, default); }
    var data = service.Snapshot();
    Check(data["codex"].CurrentRefreshSeconds == 60 && data["claude"].CurrentRefreshSeconds == 240, "Provider schedules coupled");
});
Add("Errors break unchanged streaks and adaptive settings cannot bypass rate limits", async () =>
{
    var s = Store("adaptive-errors"); s.Save(s.Current with { RefreshUnchangedThreshold = 2 });
    var clock = new TestClock(DateTimeOffset.UtcNow); var status = 200;
    var handler = new FakeHttp((_, _) => Task.FromResult(FakeHttp.Response(status, weeklyJson)));
    using var service = new QuotaService(s, new HttpClient(handler), clock);
    async Task Next() { clock.Advance(service.Snapshot()["codex"].NextAttemptAt!.Value - clock.GetUtcNow()); await service.Refresh(false, default); }
    await service.Refresh(false, default); await Next();
    Check(service.Snapshot()["codex"].UnchangedRefreshCount == 1, "No unchanged streak");
    status = 503; await Next(); var failed = service.Snapshot()["codex"];
    Check(failed.UnchangedRefreshCount == 0 && failed.CurrentRefreshSeconds == 120 && failed.Stale && failed.Weekly?.UsedPercent == 42, "Error treated as idle or cache lost");
    status = 200; await Next();
    Check(service.Snapshot()["codex"].CurrentRefreshSeconds == 120, "Error counted toward threshold");
    await Next(); Check(service.Snapshot()["codex"].CurrentRefreshSeconds == 240, "Recovery no longer adapts");
    status = 429; await Next(); var until = service.Snapshot()["codex"].NextAttemptAt;
    s.Save(s.Current with { RefreshSeconds = 60, MinRefreshSeconds = 60, MaxRefreshSeconds = 60 }); service.SettingsChanged();
    Check(service.Snapshot()["codex"].NextAttemptAt == until, "Settings bypassed 429");
    var count = handler.Count; await service.Refresh(true, default);
    Check(handler.Count == count, "Manual refresh bypassed 429");
    service.RecordCodexUsage(s.Current, null, new QuotaWindow(43, 10080, 2000000000));
    Check(service.Snapshot()["codex"].NextAttemptAt == until, "Message quota bypassed 429");
    clock.Advance(until!.Value - clock.GetUtcNow()); status = 200; await service.Refresh(false, default);
    Check(handler.Count == count + 1 && !service.Snapshot()["codex"].Stale, "Did not recover after 429");
});
Add("Adaptive policy edits reset cadence, device edits preserve it and fixed mode stays fixed", async () =>
{
    var s = Store("adaptive-policy"); s.Save(s.Current with { RefreshUnchangedThreshold = 1 });
    var clock = new TestClock(DateTimeOffset.UtcNow);
    using var service = new QuotaService(s, new HttpClient(new FakeHttp((_, _) => Task.FromResult(FakeHttp.Response(200, weeklyJson)))), clock);
    async Task Next() { clock.Advance(service.Snapshot()["codex"].NextAttemptAt!.Value - clock.GetUtcNow()); await service.Refresh(false, default); }
    await service.Refresh(false, default); await Next();
    s.Save(s.Current with { DeviceHost = "192.168.1.50" }); service.SettingsChanged();
    Check(service.Snapshot()["codex"].CurrentRefreshSeconds == 240, "Device edit reset learning");
    s.Save(s.Current with { MinRefreshSeconds = 180, MaxRefreshSeconds = 180 }); service.SettingsChanged();
    Check(service.Snapshot()["codex"].CurrentRefreshSeconds == 180 && service.Snapshot()["codex"].NextAttemptAt == clock.GetUtcNow().AddSeconds(180), "Policy not clamped/rescheduled");
    await Next(); Check(service.Snapshot()["codex"].CurrentRefreshSeconds == 180, "Equal bounds not enforced");
    s.Save(s.Current with { AdaptiveRefreshEnabled = false, RefreshSeconds = 300 }); service.SettingsChanged();
    for (var i = 0; i < 3; i++) await Next();
    Check(service.Snapshot()["codex"].CurrentRefreshSeconds == 300 && service.Snapshot()["codex"].UnchangedRefreshCount == 0, "Fixed mode adapted");
});
Add("Successful message activates the shortest cadence and credential changes reset learning", async () =>
{
    var s = Store("adaptive-activity"); s.Save(s.Current with { RefreshUnchangedThreshold = 1 });
    var clock = new TestClock(DateTimeOffset.UtcNow);
    var handler = new FakeHttp((_, _) => Task.FromResult(FakeHttp.Response(200, weeklyJson)));
    using var service = new QuotaService(s, new HttpClient(handler), clock);
    await service.Refresh(false, default); clock.Advance(TimeSpan.FromSeconds(120)); await service.Refresh(false, default);
    service.RecordCodexUsage(s.Current, null, new QuotaWindow(43, 10080, 2000000000));
    Check(service.Snapshot()["codex"].CurrentRefreshSeconds == 60 && service.Snapshot()["codex"].NextAttemptAt == clock.GetUtcNow().AddSeconds(60), "Message did not activate shortest interval");
    var auth = Path.Combine(s.Current.CodexHome, "auth.json");
    File.WriteAllText(auth, "{\"tokens\":{\"access_token\":\"fake-new-account-token\"}}");
    clock.Advance(TimeSpan.FromSeconds(60)); await service.Refresh(false, default);
    Check(service.Snapshot()["codex"].CurrentRefreshSeconds == 120 && service.Snapshot()["codex"].UnchangedRefreshCount == 0, "New identity inherited old cadence");
});
Add("Restart probes cached quota but preserves failed-request cooldowns", async () =>
{
    var s = Store("adaptive-cache"); s.Save(s.Current with { RefreshUnchangedThreshold = 1 });
    var clock = new TestClock(DateTimeOffset.UtcNow); var status = 200;
    var handler = new FakeHttp((_, _) => Task.FromResult(FakeHttp.Response(status, weeklyJson)));
    using (var service = new QuotaService(s, new HttpClient(handler), clock))
    {
        await service.Refresh(false, default); clock.Advance(TimeSpan.FromSeconds(120)); await service.Refresh(false, default);
        Check(service.Snapshot()["codex"].CurrentRefreshSeconds == 240, "Cache setup failed");
    }
    using (var restart = new QuotaService(s, new HttpClient(handler), clock))
    {
        Check(restart.Snapshot()["codex"].Stale && restart.Snapshot()["codex"].CurrentRefreshSeconds == 120, "Stale cadence restored");
        var count = handler.Count; await restart.Refresh(false, default);
        Check(handler.Count == count, "Startup bypassed minimum throttle");
        clock.Advance(TimeSpan.FromSeconds(60));
        status = 429; await restart.Refresh(false, default);
        Check(handler.Count == count + 1, "Startup did not probe cached quota");
    }
    using var limited = new QuotaService(s, new HttpClient(handler), clock);
    var before = handler.Count; await limited.Refresh(true, default);
    Check(handler.Count == before && limited.Snapshot()["codex"].NextAttemptAt == clock.GetUtcNow().AddSeconds(300), "Restart bypassed failed-request cooldown");
});
Add("Codex RPC initializes, ignores notifications, refreshes once without turns", async () =>
{
    var s = Store("rpc"); Environment.SetEnvironmentVariable("AICLOCK_FAKE_RPC", "refresh"); Environment.SetEnvironmentVariable("OPENAI_API_KEY", "fake-api-should-not-inherit");
    var (p, _) = await new CodexRpc(s.CodexExecutable).Fetch(s.Current.CodexHome, default); Check(p?.UsedPercent == 27, "No RPC usage");
    var calls = File.ReadAllLines(Path.Combine(s.Current.CodexHome, "rpc-calls.txt"));
    Check(calls.Count(c => c == "refreshed") == 1 && !calls.Any(c => c.StartsWith("turn/") || c.StartsWith("thread/")), "Unexpected model execution");
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
});
Add("RPC errors are sanitized and early exit is handled", async () =>
{
    var s = Store("rpc-errors"); Environment.SetEnvironmentVariable("AICLOCK_FAKE_RPC", "error");
    await ExpectError(async () => await new CodexRpc(s.CodexExecutable).Fetch(s.Current.CodexHome, default), "账户接口请求失败");
    Environment.SetEnvironmentVariable("AICLOCK_FAKE_RPC", "exit");
    await ExpectError(async () => await new CodexRpc(s.CodexExecutable).Fetch(s.Current.CodexHome, default), "提前退出");
});
Add("RPC startup failures report safe categories and exit codes without leaking stderr", async () =>
{
    var s = Store("rpc-startup-errors");
    foreach (var (scenario, reason) in new[] { ("startup-permission", "权限不足"), ("startup-bubblewrap", "缺少 bubblewrap"), ("startup-config", "配置加载失败") })
    {
        Environment.SetEnvironmentVariable("AICLOCK_FAKE_RPC", scenario);
        try { await new CodexRpc(s.CodexExecutable).Fetch(s.Current.CodexHome, default); throw new Exception("Expected startup failure"); }
        catch (BridgeException e)
        {
            Check(e.StatusCode == 502 && e.Message.Contains(reason) && e.Message.Contains("退出码 23"), "Missing startup diagnostics: " + e.Message);
            Check(!e.Message.Contains("secret-must-not-leak"), "Raw stderr leaked");
        }
    }
});
Add("Message model discovery and streaming completion preserve reply and quota", async () =>
{
    var s = Store("message-rpc"); Environment.SetEnvironmentVariable("AICLOCK_FAKE_RPC", "message");
    var rpc = new CodexRpc(s.CodexExecutable);
    var models = await rpc.Models(s.Current.CodexHome, default);
    Check(models.Single().Id == "test-model" && models[0].IsDefault, "Wrong model id");
    var result = await rpc.Send(s.Current.CodexHome, Path.Combine(s.DataDirectory, "work"), "test-model", "只回复 OK。", default);
    Check(result.Reply == "OK <b>literal</b>" && result.Primary?.UsedPercent == 27 && result.QuotaError == null, "Missing or duplicate reply/quota");
    var calls = File.ReadAllLines(Path.Combine(s.Current.CodexHome, "rpc-calls.txt"));
    Check(calls.Count(c => c == "turn/start") == 1, "Duplicate generation");
});
Add("Unavailable models fail before starting a paid turn", async () =>
{
    var s = Store("message-unavailable"); Environment.SetEnvironmentVariable("AICLOCK_FAKE_RPC", "message");
    await ExpectError(async () => await new CodexRpc(s.CodexExecutable).Send(s.Current.CodexHome, Path.Combine(s.DataDirectory, "work"), "unavailable", "只回复 OK。", default), "模型列表");
    Check(!File.ReadAllText(Path.Combine(s.Current.CodexHome, "rpc-calls.txt")).Contains("thread/start"), "Started unavailable model");
});
Add("Completed reply survives quota failure and model errors stay sanitized", async () =>
{
    var s = Store("message-failures"); var rpc = new CodexRpc(s.CodexExecutable);
    Environment.SetEnvironmentVariable("AICLOCK_FAKE_RPC", "message-quota-error");
    var result = await rpc.Send(s.Current.CodexHome, Path.Combine(s.DataDirectory, "work"), "test-model", "只回复 OK。", default);
    Check(result.Reply.StartsWith("OK") && result.QuotaError != null && result.Primary == null, "Lost successful reply");
    Environment.SetEnvironmentVariable("AICLOCK_FAKE_RPC", "message-failed");
    await ExpectError(async () => await rpc.Send(s.Current.CodexHome, Path.Combine(s.DataDirectory, "work"), "test-model", "只回复 OK。", default), "额度不足");
    Environment.SetEnvironmentVariable("AICLOCK_FAKE_RPC", "message-tool");
    await ExpectError(async () => await rpc.Send(s.Current.CodexHome, Path.Combine(s.DataDirectory, "work"), "test-model", "只回复 OK。", default), "工具或人工");
});
Add("Daily schedule handles midnight, grace period and once per day", () =>
{
    var now = new DateTimeOffset(2026, 10, 10, 8, 0, 10, TimeSpan.FromHours(8));
    Check(MessageScheduleService.IsDue(now, "08:00", null), "Missed scheduled time");
    Check(!MessageScheduleService.IsDue(now.AddMinutes(-1), "08:00", null), "Sent early");
    Check(!MessageScheduleService.IsDue(now.AddMinutes(5), "08:00", null), "Caught up missed schedule");
    Check(!MessageScheduleService.IsDue(now, "08:00", "2026-10-10"), "Duplicate daily send");
    Check(MessageScheduleService.IsDue(now.Date.AddDays(1).AddSeconds(15), "00:00", "2026-10-10"), "Midnight/new day failed");
    return Task.CompletedTask;
});
Add("Schedule settings persist and reject readonly/invalid configuration", async () =>
{
    var s = Store("message-settings"); var plan = new MessageSchedule { Enabled = true, Model = "test-model", Time = "08:30" };
    await ExpectError(() => { s.Save(s.Current with { MessageSchedule = plan }); return Task.CompletedTask; }, "自动续期");
    s.Save(s.Current with { CodexMode = "managed", MessageSchedule = plan });
    Check(new SettingsStore().Current.MessageSchedule == plan, "Schedule not persisted");
    await ExpectError(() => { s.Save(s.Current with { MessageSchedule = plan with { Time = "25:00" } }); return Task.CompletedTask; }, "发送时间");
    await ExpectError(() => { s.Save(s.Current with { MessageSchedule = plan with { Message = "" } }); return Task.CompletedTask; }, "简单消息");
    await ExpectError(() => { s.Save(s.Current with { MessageSchedule = plan with { Model = "" } }); return Task.CompletedTask; }, "选择");
});
Add("Scheduled results survive restart, update quota and never send twice", async () =>
{
    var s = Store("message-schedule"); Environment.SetEnvironmentVariable("AICLOCK_FAKE_RPC", "message");
    var clock = new TestClock(new DateTimeOffset(2026, 10, 10, 8, 0, 10, TimeSpan.Zero));
    s.Save(s.Current with { CodexMode = "managed", MessageSchedule = new() { Enabled = true, Model = "test-model" } });
    using var quota = new QuotaService(s, new HttpClient(new FakeHttp((_, _) => throw new Exception("Unexpected HTTP"))));
    using var service = new MessageScheduleService(s, quota, NullLogger<MessageScheduleService>.Instance, clock);
    var run = await service.Run(true, default);
    Check(run?.Status == "succeeded" && run.Reply == "OK <b>literal</b>" && quota.Snapshot()["codex"].Primary?.UsedPercent == 27, "No persisted result/updated quota");
    Check(await service.Run(true, default) == null, "Repeated scheduled send");
    await ExpectError(async () => await service.Run(false, default), "60 秒");
    using var restart = new MessageScheduleService(s, quota, NullLogger<MessageScheduleService>.Instance, clock);
    Check(await restart.Run(true, default) == null, "Repeated scheduled send after restart");
    var persisted = File.ReadAllText(Path.Combine(s.DataDirectory, "message-history.json"));
    Check(persisted.Contains("succeeded") && !persisted.Contains("fake-access"), "Missing record or leaked credentials");
    var calls = File.ReadAllLines(Path.Combine(Credentials.CodexHome(s.Current), "rpc-calls.txt"));
    Check(calls.Count(c => c == "turn/start") == 1, "More than one paid task");
});
Add("Failed scheduled turns and interrupted records never auto-retry", async () =>
{
    var s = Store("message-interrupted"); Environment.SetEnvironmentVariable("AICLOCK_FAKE_RPC", "message-failed");
    var clock = new TestClock(new DateTimeOffset(2026, 10, 10, 8, 0, 10, TimeSpan.Zero));
    s.Save(s.Current with { CodexMode = "managed", MessageSchedule = new() { Enabled = true, Model = "test-model" } });
    using var quota = new QuotaService(s, new HttpClient(new FakeHttp((_, _) => throw new Exception("Unexpected HTTP"))));
    using (var service = new MessageScheduleService(s, quota, NullLogger<MessageScheduleService>.Instance, clock))
    {
        Check((await service.Run(true, default))?.Status == "failed", "Failure not recorded");
        Check(await service.Run(true, default) == null, "Retried failed paid task");
    }
    var path = Path.Combine(s.DataDirectory, "message-history.json");
    SettingsStore.AtomicWrite(path, new MessageHistory { LastScheduledDate = "2026-10-10", Runs = [new() { StartedAt = clock.GetUtcNow(), Model = "test-model" }] });
    using var restart = new MessageScheduleService(s, quota, NullLogger<MessageScheduleService>.Instance, clock);
    Check(await restart.Run(true, default) == null, "Retried interrupted task");
    Check(File.ReadAllText(path).Contains("interrupted"), "Restart did not mark unknown result");
});
Add("Manual send and quota polling serialize managed CLI sessions", async () =>
{
    var s = Store("message-concurrent"); Environment.SetEnvironmentVariable("AICLOCK_FAKE_RPC", "message-slow");
    var rpc = new CodexRpc(s.CodexExecutable);
    var send = rpc.Send(s.Current.CodexHome, Path.Combine(s.DataDirectory, "work"), "test-model", "只回复 OK。", default);
    var poll = new CodexRpc(s.CodexExecutable).Fetch(s.Current.CodexHome, default);
    await Task.WhenAll(send, poll);
    var calls = File.ReadAllLines(Path.Combine(s.Current.CodexHome, "rpc-calls.txt"));
    Check(calls.Count(c => c == "initialize") == 2 && Array.LastIndexOf(calls, "initialize") > Array.IndexOf(calls, "turn/start"), "Overlapping managed sessions");
});
Add("GIF conversion preserves slot dimensions, frames and timing", () =>
{
    using var sheet = new Image<Rgba32>(1536, 1872, new Rgba32(200, 100, 50));
    var gif = PetdexService.BuildGif(sheet, PetdexService.Animations[1], 111, 120); using var decoded = Image.Load<Rgba32>(gif);
    Check(decoded.Width == 111 && decoded.Height == 120 && decoded.Frames.Count == 8, "Invalid GIF");
    return Task.CompletedTask;
});
Add("Device HTTP upload, pairing, display and validation", async () =>
{
    var s = Store("device"); s.Save(s.Current with { DeviceHost = "192.168.1.77", AdvertisedHost = "192.168.1.20:8877" });
    var calls = new List<string>(); var handler = new FakeHttp(async (request, ct) =>
    {
        calls.Add(request.RequestUri!.AbsolutePath); var body = await request.Content!.ReadAsStringAsync(ct);
        if (request.RequestUri.AbsolutePath == "/sprite/codex") Check(body.Contains("pet.gif") && body.Contains("image/gif"), "No multipart GIF");
        if (request.RequestUri.AbsolutePath == "/api/bridge") Check(body.Contains("192.168.1.20%3A8877"), "Wrong advertised host");
        if (request.RequestUri.AbsolutePath == "/api/reset-time") Check(body == "format=datetime", "Wrong reset time format");
        return FakeHttp.Response(200, "ok");
    });
    var device = new DeviceService(s, new HttpClient(handler)); await device.Pair(default); await device.Display("dual", default); await device.Brightness(42, default); await device.Upload("codex", Encoding.ASCII.GetBytes("GIF89a"), default); await device.Reset("claude", default);
    await device.ResetTimeFormat(null, "datetime", default);
    Check(calls.Count == 6, "Missing request"); await ExpectError(() => device.Display("stock", default), "不支持"); await ExpectError(() => device.Brightness(101, default), "亮度");
    await ExpectError(() => device.ResetTimeFormat(null, "invalid", default), "不支持");
    Check(calls.Count == 6, "Invalid format reached device");
});

var failed = 0;
try
{
    foreach (var (name, run) in tests)
    { try { await run(); Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
}
finally
{
    foreach (var (name, value) in savedEnv) Environment.SetEnvironmentVariable(name, value);
    // Delete only the exact, randomly created test directory under the temp root.
    var resolved = Path.GetFullPath(rootDir);
    if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("aiclock-tests-")) Directory.Delete(resolved, true);
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed"); Environment.ExitCode = failed == 0 ? 0 : 1;

sealed class FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
{
    public int Count;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Interlocked.Increment(ref Count); return callback(request, ct); }
    public static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
