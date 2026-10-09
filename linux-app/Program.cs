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
builder.Services.AddSingleton(new DeviceService(settings, new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));
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
app.MapGet("/api/settings", () => new { settings = settings.Current, runtime = new { port = settings.EffectivePort, portFromEnvironment = settings.PortFromEnvironment, docker = settings.Docker } });
app.MapPost("/api/settings", (BridgeSettings next, QuotaService quota) =>
{
    settings.Save(next); quota.SettingsChanged();
    return Results.Ok(new { ok = true, restartRequired = !settings.PortFromEnvironment && next.Port != settings.EffectivePort });
});
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
{ var gif = await pets.Gif(slug, request.Animation, request.Slot, ct); await device.Upload(request.Slot, gif, ct); return Results.Ok(new { ok = true }); });
app.MapPost("/api/pets/reset", async (SlotRequest request, DeviceService device, CancellationToken ct) => { await device.Reset(request.Slot, ct); return Results.Ok(new { ok = true }); });
app.Run();

record DisplayRequest(string Mode);
record BrightnessRequest(int Level);
record PetRequest(string Animation, string Slot);
record SlotRequest(string Slot);
