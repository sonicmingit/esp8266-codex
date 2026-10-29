using Microsoft.Win32;
using System.IO.Ports;
using System.Text;
using System.Text.Json;

namespace AIClockBridge;

// USB serial transport for the SD2 clock. The PC remains the source of truth:
// it reads Claude/Codex locally, then pushes the same JSON served over HTTP to
// the ESP8266. A #DEVICE handshake prevents an arbitrary COM port from being
// treated as the clock.
sealed class SerialLink : IDisposable
{
    readonly StatusService _service;
    readonly NetSpeedMonitor _netMonitor;
    readonly StockMonitor _stockMonitor;
    readonly object _gate = new();
    readonly StringBuilder _rx = new();
    readonly Dictionary<string, DateTime> _retryAfter =
        new(StringComparer.OrdinalIgnoreCase);

    CancellationTokenSource _cts;
    Task _loop;
    SerialPort _port;
    bool _linked;
    string _portName = "";
    string _firmwareVersion = "";
    string _displayMode = "auto";
    string _resetTimeFormat = "countdown";
    int _brightness = 100;
    bool _usbWifiOff;
    DateTime _openedAt = DateTime.MinValue;
    DateTime _lastHelloAt = DateTime.MinValue;
    DateTime _lastStatusAt = DateTime.MinValue;
    DateTime _lastNetAt = DateTime.MinValue;
    DateTime _nextScanAt = DateTime.MinValue;

    public event Action StateChanged;

    public SerialLink(StatusService service, NetSpeedMonitor netMonitor,
                      StockMonitor stockMonitor)
    {
        _service = service;
        _netMonitor = netMonitor;
        _stockMonitor = stockMonitor;
    }

    public bool IsLinked
    {
        get { lock (_gate) return _linked; }
    }

    public string PortName
    {
        get { lock (_gate) return _portName; }
    }

    public string FirmwareVersion
    {
        get { lock (_gate) return _firmwareVersion; }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop != null) return;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => Run(_cts.Token));
        }
    }

    async Task Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[serial] loop error: {e.Message}");
                ClosePort();
            }

            try { await Task.Delay(250, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    void Tick()
    {
        if (!HasOpenPort())
        {
            if (DateTime.UtcNow >= _nextScanAt) ScanAndOpen();
            return;
        }

        ReadPending();
        var now = DateTime.UtcNow;
        if (!IsLinked)
        {
            if (now - _openedAt > TimeSpan.FromSeconds(12))
            {
                var failed = PortName;
                if (failed.Length > 0) _retryAfter[failed] = now.AddSeconds(30);
                ClosePort();
                return;
            }
            if (now - _lastHelloAt > TimeSpan.FromSeconds(2))
            {
                _lastHelloAt = now;
                SendRaw(Encoding.ASCII.GetBytes("#HELLO\n"));
            }
            return;
        }

        if (now - _lastStatusAt > TimeSpan.FromSeconds(5))
        {
            _lastStatusAt = now;
            if (!SendFrame("#STATUS ", _service.Snapshot().ToJson())) return;
            if (!SendFrame("#STOCK ", _stockMonitor.ToJson())) return;
        }
        if (now - _lastNetAt > TimeSpan.FromSeconds(2))
        {
            _lastNetAt = now;
            SendFrame("#NET ", _netMonitor.ToJson(SystemStatsMonitor.Snapshot()));
        }
    }

    bool HasOpenPort()
    {
        lock (_gate) return _port?.IsOpen == true;
    }

    void ScanAndOpen()
    {
        _nextScanAt = DateTime.UtcNow.AddSeconds(2);
        foreach (var name in CandidatePorts())
        {
            if (_retryAfter.TryGetValue(name, out var retry) && retry > DateTime.UtcNow)
                continue;
            if (OpenPort(name)) return;
            _retryAfter[name] = DateTime.UtcNow.AddSeconds(15);
        }
    }

    static IReadOnlyList<string> CandidatePorts()
    {
        var forced = (Environment.GetEnvironmentVariable("AICLOCK_SERIAL_PORT") ?? "").Trim();
        if (forced.Length > 0) return new[] { forced.ToUpperInvariant() };

        var available = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // CH340/CH343 use WCH's USB vendor ID 1A86. Reading Enum is allowed
            // for normal users and avoids sending #HELLO to unrelated modems.
            using var usb = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\USB");
            foreach (var deviceId in usb?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                if (!deviceId.Contains("VID_1A86", StringComparison.OrdinalIgnoreCase)) continue;
                using var device = usb.OpenSubKey(deviceId);
                foreach (var instanceId in device?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var parameters = device.OpenSubKey(instanceId + @"\Device Parameters");
                    if (parameters?.GetValue("PortName") is string port && available.Contains(port))
                        result.Add(port);
                }
            }
        }
        catch
        {
            // Registry enumeration may be restricted. The handshake below is
            // still authoritative, so fall back to the OS port list.
        }

        if (result.Count == 0 || Environment.GetEnvironmentVariable("AICLOCK_SERIAL_SCAN_ALL") == "1")
            result.UnionWith(available);
        return result.OrderBy(PortNumber).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static int PortNumber(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : int.MaxValue;
    }

    bool OpenPort(string name)
    {
        try
        {
            var port = new SerialPort(name, 115200, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                DtrEnable = false,
                RtsEnable = false,
                ReadTimeout = 100,
                WriteTimeout = 750,
                Encoding = Encoding.UTF8,
                NewLine = "\n",
            };
            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
            lock (_gate)
            {
                _port = port;
                _portName = name;
                _linked = false;
                _firmwareVersion = "";
                _rx.Clear();
            }
            _openedAt = DateTime.UtcNow;
            _lastHelloAt = DateTime.MinValue;
            Console.Error.WriteLine($"[serial] trying {name}");
            RaiseStateChanged();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                  or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"[serial] cannot open {name}: {e.Message}");
            return false;
        }
    }

    void ReadPending()
    {
        string chunk;
        try
        {
            lock (_gate)
            {
                if (_port?.IsOpen != true || _port.BytesToRead <= 0) return;
                chunk = _port.ReadExisting();
            }
        }
        catch
        {
            ClosePort();
            return;
        }
        if (chunk.Length == 0) return;
        _rx.Append(chunk);
        if (_rx.Length > 16_384) _rx.Clear();
        while (true)
        {
            var text = _rx.ToString();
            var newline = text.IndexOf('\n');
            if (newline < 0) break;
            var line = text[..newline].Trim();
            _rx.Remove(0, newline + 1);
            if (line.StartsWith("#DEVICE", StringComparison.Ordinal)) HandleDeviceLine(line);
        }
    }

    void HandleDeviceLine(string line)
    {
        var version = "";
        var usbWifiOff = false;
        var resetTimeFormat = "countdown";
        var jsonStart = line.IndexOf('{');
        if (jsonStart >= 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(line[jsonStart..]);
                if (doc.RootElement.TryGetProperty("fw", out var fw)) version = fw.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("reset_time_format", out var format))
                    resetTimeFormat = format.GetString() == "datetime" ? "datetime" : "countdown";
                if (doc.RootElement.TryGetProperty("usb_wifi_off", out var wifi)
                    && (wifi.ValueKind == JsonValueKind.True || wifi.ValueKind == JsonValueKind.False))
                    usbWifiOff = wifi.GetBoolean();
            }
            catch { }
        }

        var changed = false;
        lock (_gate)
        {
            if (!_linked)
            {
                _linked = true;
                changed = true;
            }
            _firmwareVersion = version;
            _usbWifiOff = usbWifiOff;
            _resetTimeFormat = resetTimeFormat;
        }
        if (!changed) return;
        _lastStatusAt = DateTime.MinValue;
        _lastNetAt = DateTime.MinValue;
        Console.Error.WriteLine($"[serial] linked {PortName}: {line}");
        RaiseStateChanged();
    }

    bool SendFrame(string prefix, byte[] json)
    {
        var prefixBytes = Encoding.ASCII.GetBytes(prefix);
        var frame = new byte[prefixBytes.Length + json.Length + 1];
        Buffer.BlockCopy(prefixBytes, 0, frame, 0, prefixBytes.Length);
        Buffer.BlockCopy(json, 0, frame, prefixBytes.Length, json.Length);
        frame[^1] = (byte)'\n';
        return SendRaw(frame);
    }

    bool SendRaw(byte[] data)
    {
        try
        {
            lock (_gate)
            {
                if (_port?.IsOpen != true) return false;
                _port.Write(data, 0, data.Length);
                return true;
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException
                                  or TimeoutException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[serial] write failed: {e.Message}");
            ClosePort();
            return false;
        }
    }

    /// Commands are one-shot, so a failed write must return false and let the
    /// caller fall back to the device's HTTP API.
    public bool SendCommand(IReadOnlyDictionary<string, object> command)
    {
        if (!IsLinked) return false;
        var ok = SendFrame("#CMD ", JsonSerializer.SerializeToUtf8Bytes(command));
        if (!ok) return false;
        lock (_gate)
        {
            if (command.TryGetValue("display", out var display) && display is string mode)
                _displayMode = mode;
            if (command.TryGetValue("reset_time_format", out var format) && format is string resetFormat)
                _resetTimeFormat = resetFormat;
            if (command.TryGetValue("brightness", out var brightness))
                _brightness = Convert.ToInt32(brightness);
            if (command.TryGetValue("usb_wifi_off", out var usbWifiOff))
                _usbWifiOff = Convert.ToBoolean(usbWifiOff);
        }
        return true;
    }

    /// Synthetic /api/info equivalent used by the mirror and tray when the
    /// clock intentionally has no Wi-Fi. The serial protocol does not mirror
    /// custom-sprite metadata, so those fields remain at their defaults.
    public DeviceInfo WiredDeviceInfo()
    {
        string port, mode, firmware, resetTimeFormat;
        int brightness;
        bool usbWifiOff;
        lock (_gate)
        {
            if (!_linked) return null;
            port = _portName;
            mode = _displayMode;
            resetTimeFormat = _resetTimeFormat;
            firmware = _firmwareVersion;
            brightness = _brightness;
            usbWifiOff = _usbWifiOff;
        }
        var snap = _service.Snapshot();
        var showing = mode == "codex" ? "codex" : "claude";
        if (mode == "auto" && snap.Codex.Status == "working" && snap.Claude.Status != "working")
            showing = "codex";
        return new DeviceInfo
        {
            Ip = $"USB {port}",
            Ssid = "USB",
            Bridge = "USB 有线",
            Mode = mode,
            ResetTimeFormat = resetTimeFormat,
            Effective = mode,
            Showing = showing,
            LastUpdateS = 0,
            Brightness = brightness,
            Wired = true,
            UsbWifiOff = usbWifiOff,
            FirmwareVersion = firmware,
        };
    }

    void ClosePort()
    {
        string closed;
        bool notify;
        lock (_gate)
        {
            closed = _portName;
            notify = _linked || _port != null;
            try { _port?.Close(); } catch { }
            try { _port?.Dispose(); } catch { }
            _port = null;
            _portName = "";
            _firmwareVersion = "";
            _usbWifiOff = false;
            _linked = false;
            _rx.Clear();
        }
        if (notify)
        {
            Console.Error.WriteLine($"[serial] closed {closed}");
            RaiseStateChanged();
        }
    }

    void RaiseStateChanged()
    {
        var handlers = StateChanged?.GetInvocationList() ?? Array.Empty<Delegate>();
        foreach (var handler in handlers)
        {
            try { ((Action)handler)(); } catch { }
        }
    }

    public void Dispose()
    {
        CancellationTokenSource cts;
        Task loop;
        lock (_gate) { cts = _cts; loop = _loop; }
        cts?.Cancel();
        try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        ClosePort();
        cts?.Dispose();
    }
}
