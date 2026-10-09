using System.Net;
using System.Text;
using System.Text.Json;
using AIClockBridge.Linux;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

// Dependency-free integration runner. All credentials, HTTP responses and
// app-server messages are synthetic; tests never contact account APIs.
if (args.FirstOrDefault() == "app-server")
{
    var home = Environment.GetEnvironmentVariable("CODEX_HOME")!;
    var calls = Path.Combine(home, "rpc-calls.txt");
    var scenario = Environment.GetEnvironmentVariable("AICLOCK_FAKE_RPC") ?? "refresh";
    var refreshed = false;
    if (Environment.GetEnvironmentVariable("OPENAI_API_KEY") != null) Environment.Exit(10);
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
        if (method == "account/rateLimits/read")
        {
            if (scenario == "error") error = new { code = -1, message = "secret-must-not-leak" };
            else if (!refreshed) error = new { code = -1, message = "401 unauthorized secret-must-not-leak" };
            else result = new { rateLimits = new { primary = new { usedPercent = 27, windowDurationMins = 300, resetsAt = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds() } } };
        }
        if (method is "thread/start" or "turn/start") Environment.Exit(11);
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
    var codex = Path.Combine(path, "codex"); var claude = Path.Combine(path, "claude");
    Directory.CreateDirectory(codex); Directory.CreateDirectory(claude);
    File.WriteAllText(Path.Combine(codex, "auth.json"), "{\"tokens\":{\"access_token\":\"fake-access-one\",\"account_id\":\"fake-account\"}}");
    store.Save(store.Current with { CodexHome = codex, ClaudeHome = claude, ClaudeEnabled = false }); return store;
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
    var s = Store("readonly"); var handler = new FakeHttp((request, _) =>
    { Check(request.Headers.Authorization?.Parameter == "fake-access-one", "Credential header missing"); return Task.FromResult(FakeHttp.Response(200, weeklyJson)); });
    using var service = new QuotaService(s, new HttpClient(handler)); await service.Refresh(true, default);
    var usage = service.Snapshot()["codex"]; Check(!usage.Stale && usage.Weekly?.UsedPercent == 42, "No usage");
    using var status = JsonDocument.Parse(JsonSerializer.Serialize(service.FirmwareStatus()));
    Check(status.RootElement.GetProperty("codex").GetProperty("weekly_pct").GetDouble() == 42, "Firmware fields mismatch");
    Check(status.RootElement.GetProperty("codex").GetProperty("primary_pct").ValueKind == JsonValueKind.Null, "Invented primary window");
    var cache = File.ReadAllText(Path.Combine(s.DataDirectory, "usage-cache.json")); Check(!cache.Contains("fake-access"), "Secret persisted in quota cache");
    using var restart = new QuotaService(s, new HttpClient(new FakeHttp((_, _) => Task.FromResult(FakeHttp.Response(503, "")))));
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
        return FakeHttp.Response(200, "ok");
    });
    var device = new DeviceService(s, new HttpClient(handler)); await device.Pair(default); await device.Display("dual", default); await device.Brightness(42, default); await device.Upload("codex", Encoding.ASCII.GetBytes("GIF89a"), default); await device.Reset("claude", default);
    Check(calls.Count == 5, "Missing request"); await ExpectError(() => device.Display("stock", default), "不支持"); await ExpectError(() => device.Brightness(101, default), "亮度");
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
