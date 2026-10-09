using System.Text.Json;

namespace AIClockBridge.Linux;

public sealed class SettingsStore
{
    readonly object gate = new();
    BridgeSettings current;
    public string DataDirectory { get; }
    public int EffectivePort { get; }
    public bool PortFromEnvironment { get; }
    public string CodexExecutable { get; }
    public bool Docker => Environment.GetEnvironmentVariable("AICLOCK_DOCKER") == "1";
    public BridgeSettings Current { get { lock (gate) return current; } }

    public SettingsStore()
    {
        DataDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("AICLOCK_DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data"));
        Directory.CreateDirectory(DataDirectory);
        var path = Path.Combine(DataDirectory, "settings.json");
        try { current = JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(path), JsonValues.Options) ?? new(); }
        catch (FileNotFoundException) { current = new(); }
        catch (JsonException) { throw new BridgeException("settings.json 格式错误，请修复配置文件后重启。"); }
        if (current.Devices is null) current = current with { Devices = [] };
        // Keep existing single-screen installations working after upgrading.
        if (current.Devices.Count == 0 && !string.IsNullOrWhiteSpace(current.DeviceHost))
            current = current with { Devices = [new DeviceEntry("legacy", "小屏幕 1", current.DeviceHost)] };
        // Directory overrides seed the first run only; later edits in the page persist.
        if (!File.Exists(path)) current = current with
        {
            CodexHome = Environment.GetEnvironmentVariable("AICLOCK_CODEX_HOME") ?? current.CodexHome,
            ClaudeHome = Environment.GetEnvironmentVariable("AICLOCK_CLAUDE_HOME") ?? current.ClaudeHome,
            CodexMode = Environment.GetEnvironmentVariable("AICLOCK_CODEX_MODE") ?? current.CodexMode,
            AdvertisedHost = Environment.GetEnvironmentVariable("AICLOCK_ADVERTISED_HOST") ?? ""
        };
        Validate(current);
        var port = Environment.GetEnvironmentVariable("AICLOCK_PORT");
        PortFromEnvironment = !string.IsNullOrEmpty(port);
        if (PortFromEnvironment && (!int.TryParse(port, out var n) || n is < 1 or > 65535))
            throw new BridgeException("AICLOCK_PORT 必须是 1～65535 的整数。");
        EffectivePort = PortFromEnvironment ? int.Parse(port!) : current.Port;
        CodexExecutable = Environment.GetEnvironmentVariable("AICLOCK_CODEX_EXECUTABLE") ?? "codex";
    }

    public void Save(BridgeSettings settings)
    {
        Validate(settings);
        lock (gate)
        {
            AtomicWrite(Path.Combine(DataDirectory, "settings.json"), settings);
            current = settings;
        }
    }

    public static void AtomicWrite<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonValues.Options));
        File.Move(temp, path, true);
    }

    public static void Validate(BridgeSettings s)
    {
        if (s.DeviceHost == null || s.AdvertisedHost == null || s.Devices == null) throw new BridgeException("设备与服务地址不能为 null，未设置时请留空。");
        if (s.Port is < 1 or > 65535) throw new BridgeException("端口必须是 1～65535。");
        if (s.RefreshSeconds is < 60 or > 86400) throw new BridgeException("刷新间隔必须在 1～1440 分钟之间。");
        if (s.CodexMode is not ("readonly" or "managed")) throw new BridgeException("Codex 模式必须是 readonly 或 managed。");
        if (s.Devices.Count > 16) throw new BridgeException("最多可以添加 16 个小屏幕。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in s.Devices)
        {
            if (device is null || device.Name is null || device.Host is null) throw new BridgeException("小屏幕配置不完整，请删除后重新添加。");
            if (string.IsNullOrWhiteSpace(device.Id) || device.Id.Length > 64 || (device.Id != "legacy" && !Guid.TryParseExact(device.Id, "N", out _)))
                throw new BridgeException("小屏幕标识无效，请删除后重新添加。");
            if (!ids.Add(device.Id)) throw new BridgeException("小屏幕标识重复。");
            if (string.IsNullOrWhiteSpace(device.Name) || device.Name.Trim().Length > 50) throw new BridgeException("小屏幕名称不能为空，且最多 50 个字符。");
            if (!hosts.Add(device.Host.Trim())) throw new BridgeException("同一个设备地址不能重复添加。");
            ValidateHost(device.Host);
        }
        foreach (var path in new[] { s.CodexHome, s.ClaudeHome })
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new BridgeException("凭据目录必须是绝对路径，不能填写 ~。");
        if (!string.IsNullOrEmpty(s.AdvertisedHost)) ValidateHost(s.AdvertisedHost);
    }

    public static Uri ValidateHost(string host)
    {
        // Plain host[:port] keeps the firmware URL and outgoing device paths unambiguous.
        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) || host.IndexOfAny(['/', '\\', '?', '#', '@']) >= 0
            || !Uri.TryCreate("http://" + host, UriKind.Absolute, out var uri) || uri.HostNameType == UriHostNameType.Unknown
            || uri.Port is < 1 or > 65535)
            throw new BridgeException("地址请填写 IP 或主机名及可选端口，例如 192.168.1.20:8765，不带 http:// 或路径。");
        return uri;
    }
}
