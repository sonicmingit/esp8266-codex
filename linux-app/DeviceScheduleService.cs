using System.Globalization;
using System.Collections.Concurrent;

namespace AIClockBridge.Linux;

public sealed class DeviceScheduleService(SettingsStore settings, DeviceService devices, ILogger<DeviceScheduleService> logger) : BackgroundService
{
    sealed record AppliedState(string Host, string? Mode, int Brightness);
    readonly ConcurrentDictionary<string, AppliedState> applied = new(StringComparer.Ordinal);

    public void Forget(string id) => applied.TryRemove(id, out _);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = TimeOnly.FromDateTime(DateTime.Now);
            foreach (var device in settings.Current.Devices)
            {
                var managed = device.PageScheduleEnabled || device.NightModeEnabled;
                var wasManaged = applied.ContainsKey(device.Id);
                if (!managed && !wasManaged) continue;

                var pageActive = !device.PageScheduleEnabled || InWindow(now, device.PageOnTime, device.PageOffTime);
                var nightActive = device.NightModeEnabled && InWindow(now, device.NightStart, device.NightEnd);
                var mode = pageActive && device.PageScheduleEnabled ? device.PageMode : null;
                var brightness = !pageActive ? device.PageOffBrightness : nightActive ? device.NightBrightness : device.DayBrightness;
                var desired = new AppliedState(device.Host, mode, brightness);
                if (applied.TryGetValue(device.Id, out var prior) && prior == desired) continue;

                try
                {
                    await devices.ApplySchedule(device.Id, desired.Mode, desired.Brightness, stoppingToken);
                    if (managed) applied[device.Id] = desired;
                    else applied.TryRemove(device.Id, out _);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception e) when (e is BridgeException or HttpRequestException or OperationCanceledException)
                {
                    logger.LogWarning("Could not apply the saved screen schedule to {DeviceName}: {Error}", device.Name, e.Message);
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    static bool InWindow(TimeOnly now, string startText, string endText)
    {
        var start = TimeOnly.ParseExact(startText, "HH:mm", CultureInfo.InvariantCulture);
        var end = TimeOnly.ParseExact(endText, "HH:mm", CultureInfo.InvariantCulture);
        return start < end ? now >= start && now < end : now >= start || now < end;
    }
}
