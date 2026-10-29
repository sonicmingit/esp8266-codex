using System.Net.Http.Headers;
using System.Text.Json;

namespace AIClockBridge.Linux;

public sealed class DeviceService(SettingsStore settings, HttpClient http)
{
    readonly SemaphoreSlim writes = new(1);
    DeviceEntry Target(string? id)
    {
        var devices = settings.Current.Devices;
        if (devices.Count == 0 && !string.IsNullOrWhiteSpace(settings.Current.DeviceHost))
            devices = [new DeviceEntry("legacy", "小屏幕 1", settings.Current.DeviceHost)];
        if (devices.Count == 0) throw new BridgeException("请先在基础设置中添加小屏幕。");
        return id is null ? devices[0] : devices.FirstOrDefault(d => d.Id == id)
            ?? throw new BridgeException("找不到这个小屏幕，请刷新页面后重试。");
    }
    Uri Resolve(string? id, string path) => new(SettingsStore.ValidateHost(Target(id).Host), path);

    public Task<JsonElement> Info(CancellationToken ct) => Info(null, ct);
    public async Task<JsonElement> Info(string? id, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await http.GetAsync(Resolve(id, "api/info"), timeout.Token);
        if (!response.IsSuccessStatusCode) throw new BridgeException("设备连接失败，请检查 IP 和 Wi-Fi。", 502);
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        if (!data.RootElement.TryGetProperty("sprite_rev", out _)) throw new BridgeException("此地址没有返回 AIClock 设备信息。", 502);
        return data.RootElement.Clone();
    }

    public Task Pair(CancellationToken ct) => Pair(null, ct);
    public Task Pair(string? id, CancellationToken ct)
    {
        var address = settings.Current.AdvertisedHost;
        if (address.Length == 0) throw new BridgeException("请填写设备可访问的服务地址，例如宿主机局域网 IP:端口。");
        return Post(id, "api/bridge", new() { ["host"] = address }, ct);
    }
    public Task Display(string mode, CancellationToken ct) => Display(null, mode, ct);
    public Task Display(string? id, string mode, CancellationToken ct)
    {
        if (mode is not ("auto" or "claude" or "codex" or "dual")) throw new BridgeException("显示模式不支持。");
        return Post(id, "api/display", new() { ["mode"] = mode }, ct);
    }
    public Task Brightness(int level, CancellationToken ct) => Brightness(null, level, ct);
    public Task Brightness(string? id, int level, CancellationToken ct)
    {
        if (level is < 0 or > 100) throw new BridgeException("亮度必须在 0～100 之间。");
        return Post(id, "api/brightness", new() { ["level"] = level.ToString() }, ct);
    }
    public async Task ApplySchedule(string id, string? mode, int brightness, CancellationToken ct)
    {
        if (mode is not null)
        {
            if (mode is not ("auto" or "claude" or "codex" or "dual")) throw new BridgeException("定时页面模式无效。");
            await Post(id, "api/display", new() { ["mode"] = mode }, ct);
        }
        await Brightness(id, brightness, ct);
    }
    public Task Reset(string slot, CancellationToken ct) => Reset(null, slot, ct);
    public Task Reset(string? id, string slot, CancellationToken ct) { ValidateSlot(slot); return Post(id, $"sprite/{slot}/reset", new(), ct); }
    public static void ValidateSlot(string slot) { if (slot is not ("claude" or "codex")) throw new BridgeException("宠物插槽无效。"); }

    async Task Post(string? id, string path, Dictionary<string, string> values, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(values);
        await Send(id, path, form, 8, ct);
    }
    public Task Upload(string slot, byte[] gif, CancellationToken ct) => Upload(null, slot, gif, ct);
    public async Task Upload(string? id, string slot, byte[] gif, CancellationToken ct)
    {
        ValidateSlot(slot);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(gif);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/gif");
        form.Add(file, "file", "pet.gif");
        await Send(id, $"sprite/{slot}", form, 90, ct);
    }
    async Task Send(string? id, string path, HttpContent body, int seconds, CancellationToken ct)
    {
        // The ESP8266 can decode only one upload at a time.
        await writes.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            using var response = await http.PostAsync(Resolve(id, path), body, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new BridgeException($"设备操作失败（HTTP {(int)response.StatusCode}），请检查连接后重试。", 502);
        }
        finally { writes.Release(); }
    }
}
