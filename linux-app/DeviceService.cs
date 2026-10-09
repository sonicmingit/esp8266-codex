using System.Net.Http.Headers;
using System.Text.Json;

namespace AIClockBridge.Linux;

public sealed class DeviceService(SettingsStore settings, HttpClient http)
{
    readonly SemaphoreSlim writes = new(1);
    Uri Resolve(string path)
    {
        var host = settings.Current.DeviceHost;
        if (string.IsNullOrEmpty(host)) throw new BridgeException("请先在基础设置中填写设备 IP。");
        return new Uri(SettingsStore.ValidateHost(host), path);
    }

    public async Task<JsonElement> Info(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await http.GetAsync(Resolve("api/info"), timeout.Token);
        if (!response.IsSuccessStatusCode) throw new BridgeException("设备连接失败，请检查 IP 和 Wi-Fi。", 502);
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        if (!data.RootElement.TryGetProperty("sprite_rev", out _)) throw new BridgeException("此地址没有返回 AIClock 设备信息。", 502);
        return data.RootElement.Clone();
    }

    public Task Pair(CancellationToken ct)
    {
        var address = settings.Current.AdvertisedHost;
        if (address.Length == 0) throw new BridgeException("请填写设备可访问的服务地址，例如宿主机局域网 IP:端口。");
        return Post("api/bridge", new() { ["host"] = address }, ct);
    }
    public Task Display(string mode, CancellationToken ct)
    {
        if (mode is not ("auto" or "claude" or "codex" or "dual")) throw new BridgeException("显示模式不支持。");
        return Post("api/display", new() { ["mode"] = mode }, ct);
    }
    public Task Brightness(int level, CancellationToken ct)
    {
        if (level is < 0 or > 100) throw new BridgeException("亮度必须在 0～100 之间。");
        return Post("api/brightness", new() { ["level"] = level.ToString() }, ct);
    }
    public Task Reset(string slot, CancellationToken ct) { ValidateSlot(slot); return Post($"sprite/{slot}/reset", new(), ct); }
    public static void ValidateSlot(string slot) { if (slot is not ("claude" or "codex")) throw new BridgeException("宠物插槽无效。"); }

    async Task Post(string path, Dictionary<string, string> values, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(values);
        await Send(path, form, 8, ct);
    }
    public async Task Upload(string slot, byte[] gif, CancellationToken ct)
    {
        ValidateSlot(slot);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(gif);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/gif");
        form.Add(file, "file", "pet.gif");
        await Send($"sprite/{slot}", form, 90, ct);
    }
    async Task Send(string path, HttpContent body, int seconds, CancellationToken ct)
    {
        // The ESP8266 can decode only one upload at a time.
        await writes.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            using var response = await http.PostAsync(Resolve(path), body, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new BridgeException($"设备操作失败（HTTP {(int)response.StatusCode}），请检查连接后重试。", 502);
        }
        finally { writes.Release(); }
    }
}
