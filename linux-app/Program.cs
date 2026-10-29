using AIClockBridge.Linux;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);
var settings = new SettingsStore();
builder.WebHost.UseUrls($"http://0.0.0.0:{settings.EffectivePort}");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddSingleton(settings);
// Distinct clients keep quota timeout and large pet/device transfers independent.
builder.Services.AddSingleton(sp => new QuotaService(settings, new HttpClient { Timeout = TimeSpan.FromSeconds(25) }));
builder.Services.AddHostedService(sp => sp.GetRequiredService<QuotaService>());
builder.Services.AddSingleton<CodexLoginService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MessageScheduleService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MessageScheduleService>());
builder.Services.AddSingleton(new DeviceService(settings, new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));
builder.Services.AddSingleton<DeviceScheduleService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeviceScheduleService>());
builder.Services.AddSingleton(new PetdexService(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }));
var app = builder.Build();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path == "/status") context.Response.Headers.CacheControl = "no-store";
    if (HttpMethods.IsPost(context.Request.Method))
    {
        // JSON-only mutation routes and origin checks stop other websites from
        // changing a LAN bridge via a browser. The device only needs GET /status.
        var origin = context.Request.Headers.Origin.ToString();
        if (!context.Request.HasJsonContentType() || (origin.Length > 0 &&
            (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Authority != context.Request.Host.Value || uri.Scheme != context.Request.Scheme)))
        { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "请从本服务页面发起操作，POST 请求须使用 application/json。" }); return; }
    }
    try { await next(context); }
    catch (BridgeException e) { if (!context.Response.HasStarted) { context.Response.StatusCode = e.StatusCode; await context.Response.WriteAsJsonAsync(new { error = e.Message }); } }
    catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
    { if (!context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted) { context.Response.StatusCode = 502; await context.Response.WriteAsJsonAsync(new { error = "请求失败或超时，请检查网络与设备连接后重试。" }); } }
    catch (System.Text.Json.JsonException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "JSON 数据格式错误。" }); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = "配置或数据目录不可写，请检查目录权限。" }); }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", version = "0.1.0" }));
app.MapGet("/status", (QuotaService quota) => quota.FirmwareStatus());
app.MapGet("/api/usage", (QuotaService quota) => quota.Snapshot());
app.MapPost("/api/usage/refresh", async (QuotaService quota, CancellationToken ct) => { await quota.Refresh(true, ct); return quota.Snapshot(); });
app.MapGet("/api/codex/login", (CodexLoginService login) => login.Snapshot());
app.MapPost("/api/codex/login/start", (CodexLoginService login, CancellationToken ct) => login.Start(settings.Current, ct));
app.MapPost("/api/codex/login/cancel", (CodexLoginService login) => login.Cancel());
app.MapPost("/api/codex/login/refresh", async (QuotaService quota, CancellationToken ct) => { await quota.RefreshAfterLogin(ct); return quota.Snapshot(); });
app.MapGet("/api/messages", (MessageScheduleService messages) => messages.Snapshot());
app.MapGet("/api/messages/models", (MessageScheduleService messages, CancellationToken ct) => messages.Models(ct));
app.MapPost("/api/messages/schedule", (MessageSchedule next) =>
{
    settings.Save(settings.Current with { MessageSchedule = next });
    return Results.Ok(new { ok = true });
});
app.MapPost("/api/messages/send", (MessageScheduleService messages, IHostApplicationLifetime lifetime) => messages.Run(false, lifetime.ApplicationStopping));
app.MapGet("/api/settings", () => new { settings = settings.Current, runtime = new { port = settings.EffectivePort, portFromEnvironment = settings.PortFromEnvironment, docker = settings.Docker } });
app.MapPost("/api/settings", (BridgeSettings next, QuotaService quota) =>
{
    settings.Save(next); quota.SettingsChanged();
    return Results.Ok(new { ok = true, restartRequired = !settings.PortFromEnvironment && next.Port != settings.EffectivePort });
});
app.MapGet("/api/devices", () => settings.Current.Devices);
app.MapPost("/api/devices", (DeviceSaveRequest request) =>
{
    var current = settings.Current;
    if (current.Devices.Count >= 16) throw new BridgeException("最多可以添加 16 个小屏幕。");
    var entry = NormalizeDevice(request, Guid.NewGuid().ToString("N"));
    if (current.Devices.Any(d => string.Equals(d.Host, entry.Host, StringComparison.OrdinalIgnoreCase))) throw new BridgeException("这个设备地址已经添加过了。");
    settings.Save(current with { DeviceHost = "", Devices = [.. current.Devices, entry] });
    return Results.Ok(entry);
});
app.MapPost("/api/devices/{id}/update", (string id, DeviceSaveRequest request) =>
{
    var current = settings.Current;
    var existing = current.Devices.FirstOrDefault(d => d.Id == id) ?? throw new BridgeException("找不到这个小屏幕，请刷新页面后重试。");
    var normalized = NormalizeDevice(request, id);
    var entry = existing with { Name = normalized.Name, Host = normalized.Host };
    if (current.Devices.Any(d => d.Id != id && string.Equals(d.Host, entry.Host, StringComparison.OrdinalIgnoreCase))) throw new BridgeException("这个设备地址已经添加过了。");
    settings.Save(current with { DeviceHost = "", Devices = current.Devices.Select(d => d.Id == id ? entry : d).ToList() });
    return Results.Ok(entry);
});
app.MapPost("/api/devices/{id}/delete", (string id) =>
{
    var current = settings.Current;
    if (!current.Devices.Any(d => d.Id == id)) throw new BridgeException("找不到这个小屏幕，请刷新页面后重试。");
    settings.Save(current with { DeviceHost = "", Devices = current.Devices.Where(d => d.Id != id).ToList() });
    return Results.Ok(new { ok = true });
});
app.MapGet("/api/devices/{id}/info", async (string id, DeviceService device, CancellationToken ct) =>
{
    var info = await device.Info(id, ct);
    var current = settings.Current;
    var entry = current.Devices.FirstOrDefault(d => d.Id == id);
    if (entry is { PageScheduleEnabled: false, NightModeEnabled: false }
        && info.TryGetProperty("brightness", out var value) && value.TryGetInt32(out var level) && level is >= 0 and <= 100
        && entry.DayBrightness != level)
        settings.Save(current with { Devices = current.Devices.Select(d => d.Id == id ? d with { DayBrightness = level } : d).ToList() });
    return info;
});
app.MapPost("/api/devices/{id}/pair", async (string id, DeviceService device, CancellationToken ct) => { await device.Pair(id, ct); return Results.Ok(new { ok = true }); });
app.MapPost("/api/devices/{id}/reset-time", async (string id, ResetTimeRequest request, DeviceService device, CancellationToken ct) => { await device.ResetTimeFormat(id, request.Format, ct); return Results.Ok(new { ok = true }); });
app.MapPost("/api/devices/{id}/display", async (string id, DisplayRequest request, DeviceService device, DeviceScheduleService schedule, CancellationToken ct) => { await device.Display(id, request.Mode, ct); schedule.Forget(id); return Results.Ok(new { ok = true }); });
app.MapPost("/api/devices/{id}/brightness", async (string id, BrightnessRequest request, DeviceService device, DeviceScheduleService schedule, CancellationToken ct) =>
{
    await device.Brightness(id, request.Level, ct);
    var current = settings.Current;
    if (!current.Devices.Any(d => d.Id == id)) throw new BridgeException("找不到这个小屏幕，请刷新页面后重试。");
    settings.Save(current with { Devices = current.Devices.Select(d => d.Id == id ? d with { DayBrightness = request.Level } : d).ToList() });
    schedule.Forget(id);
    return Results.Ok(new { ok = true });
});
app.MapPost("/api/devices/{id}/schedule", (string id, DeviceScheduleRequest request) =>
{
    var current = settings.Current;
    if (!current.Devices.Any(d => d.Id == id)) throw new BridgeException("找不到这个小屏幕，请刷新页面后重试。");
    var devices = current.Devices.Select(d => d.Id == id ? d with
    {
        PageScheduleEnabled = request.PageScheduleEnabled,
        PageMode = request.PageMode,
        PageOnTime = request.PageOnTime,
        PageOffTime = request.PageOffTime,
        PageOffBrightness = request.PageOffBrightness,
        NightModeEnabled = request.NightModeEnabled,
        NightStart = request.NightStart,
        NightEnd = request.NightEnd,
        NightBrightness = request.NightBrightness
    } : d).ToList();
    settings.Save(current with { Devices = devices });
    return Results.Ok(new { ok = true });
});
// Retain the original single-device endpoints for existing integrations.
app.MapGet("/api/device", (DeviceService device, CancellationToken ct) => device.Info(ct));
app.MapPost("/api/device/pair", async (DeviceService device, CancellationToken ct) => { await device.Pair(ct); return Results.Ok(new { ok = true }); });
app.MapPost("/api/device/display", async (DisplayRequest request, DeviceService device, CancellationToken ct) => { await device.Display(request.Mode, ct); return Results.Ok(new { ok = true }); });
app.MapPost("/api/device/brightness", async (BrightnessRequest request, DeviceService device, CancellationToken ct) => { await device.Brightness(request.Level, ct); return Results.Ok(new { ok = true }); });
app.MapGet("/api/pets", async (string? search, int? page, PetdexService pets, CancellationToken ct) =>
{
    var filtered = (await pets.Manifest(ct)).Where(p => string.IsNullOrWhiteSpace(search) || p.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) || p.Slug.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
    var index = Math.Clamp(page ?? 0, 0, Math.Max(0, (filtered.Length - 1) / 24));
    return new { pets = filtered.Skip(index * 24).Take(24).Select(p => new { p.Slug, p.DisplayName, p.Kind }), total = filtered.Length, page = index, pageSize = 24, animations = PetdexService.Animations.Select(a => new { a.Id, a.Label }) };
});
app.MapGet("/api/pets/{slug}/preview", async (string slug, string? animation, string? slot, PetdexService pets, CancellationToken ct) => Results.File(await pets.Gif(slug, animation ?? "idle", slot ?? "codex", ct), "image/gif"));
app.MapPost("/api/pets/{slug}/apply", async (string slug, PetRequest request, PetdexService pets, DeviceService device, CancellationToken ct) =>
{ var gif = await pets.Gif(slug, request.Animation, request.Slot, ct); await device.Upload(request.DeviceId, request.Slot, gif, ct); return Results.Ok(new { ok = true }); });
app.MapPost("/api/pets/reset", async (SlotRequest request, DeviceService device, CancellationToken ct) => { await device.Reset(request.DeviceId, request.Slot, ct); return Results.Ok(new { ok = true }); });

static DeviceEntry NormalizeDevice(DeviceSaveRequest request, string id)
{
    var name = request.Name?.Trim() ?? "";
    var host = request.Host?.Trim() ?? "";
    if (name.Length is < 1 or > 50) throw new BridgeException("小屏幕名称不能为空，且最多 50 个字符。");
    SettingsStore.ValidateHost(host);
    return new DeviceEntry(id, name, host);
}

app.Run();

record DisplayRequest(string Mode);
record ResetTimeRequest(string Format);
record BrightnessRequest(int Level);
record DeviceSaveRequest(string Name, string Host);
record DeviceScheduleRequest(bool PageScheduleEnabled, string PageMode, string PageOnTime, string PageOffTime, int PageOffBrightness, bool NightModeEnabled, string NightStart, string NightEnd, int NightBrightness);
record PetRequest(string Animation, string Slot, string DeviceId);
record SlotRequest(string Slot, string DeviceId);
