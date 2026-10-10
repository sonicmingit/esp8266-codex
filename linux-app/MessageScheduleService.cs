using System.Globalization;
using System.Text.Json;

namespace AIClockBridge.Linux;

public sealed class MessageScheduleService : BackgroundService
{
    readonly SettingsStore settings;
    readonly QuotaService quota;
    readonly CodexRpc rpc;
    readonly ILogger<MessageScheduleService> logger;
    readonly TimeProvider clock;
    readonly object gate = new();
    readonly SemaphoreSlim sendGate = new(1);
    readonly string historyPath;
    MessageHistory history;
    public MessageScheduleService(SettingsStore settings, QuotaService quota, ILogger<MessageScheduleService> logger, TimeProvider clock)
    {
        this.settings = settings; this.quota = quota; this.logger = logger; this.clock = clock;
        rpc = new(settings.CodexExecutable);
        historyPath = Path.Combine(settings.DataDirectory, "message-history.json");
        try { history = JsonSerializer.Deserialize<MessageHistory>(File.ReadAllText(historyPath), JsonValues.Options) ?? new(); }
        catch (FileNotFoundException) { history = new(); }
        catch (JsonException) { throw new BridgeException("message-history.json 格式错误，请修复记录文件后重启，避免重复发送。"); }
        if (history.Runs == null) throw new BridgeException("message-history.json 记录格式错误。");
        if (history.Runs.Any(r => r.Status == "running"))
        {
            history = history with { Runs = history.Runs.Select(r => r.Status == "running" ? r with
            { Status = "interrupted", FinishedAt = clock.GetUtcNow(), Error = "服务重启中断了发送，结果未知；不会自动重发。" } : r).ToList() };
            SettingsStore.AtomicWrite(historyPath, history);
        }
    }

    public object Snapshot()
    {
        lock (gate)
        {
            var config = settings.Current;
            var now = clock.GetLocalNow();
            var due = DueTime(now, config.MessageSchedule.Time);
            // Once per local calendar day, even if the configured time changes.
            if (now >= due.AddMinutes(5) || history.LastScheduledDate == DateKey(now)) due = DueTime(now.AddDays(1), config.MessageSchedule.Time);
            return new { schedule = config.MessageSchedule, timeZone = clock.LocalTimeZone.Id,
                serverTime = now, nextRunAt = config.MessageSchedule.Enabled ? (DateTimeOffset?)due : null,
                codexMode = config.CodexMode, codexEnabled = config.CodexEnabled,
                canSend = config.CodexEnabled && config.CodexMode == "managed", running = history.Runs.Any(r => r.Status == "running"),
                runs = history.Runs.ToArray() };
        }
    }

    public Task<List<CodexModel>> Models(CancellationToken ct)
    {
        var config = settings.Current;
        RequireManaged(config);
        return rpc.Models(config.CodexHome, ct);
    }

    public async Task<MessageRun?> Run(bool scheduled, CancellationToken ct)
    {
        if (!await sendGate.WaitAsync(0, ct))
        {
            if (scheduled) return null;
            throw new BridgeException("已有一条消息正在发送，请等待结果。", 409);
        }
        try
        {
            var config = settings.Current;
            var plan = config.MessageSchedule;
            var now = clock.GetLocalNow();
            MessageRun run;
            lock (gate)
            {
                if (scheduled && (!plan.Enabled || !IsDue(now, plan.Time, history.LastScheduledDate))) return null;
                RequireManaged(config);
                SettingsStore.ValidateMessageSchedule(plan, requireModel: true);
                if (history.Runs.FirstOrDefault() is { } last && clock.GetUtcNow() - last.StartedAt < TimeSpan.FromSeconds(60))
                {
                    if (scheduled) return null;
                    throw new BridgeException("消息发送至少间隔 60 秒，请稍后再试。", 429);
                }
                run = new MessageRun { Source = scheduled ? "scheduled" : "manual", Model = plan.Model, Message = plan.Message, StartedAt = clock.GetUtcNow() };
                var next = history with { LastScheduledDate = scheduled ? DateKey(now) : history.LastScheduledDate,
                    Runs = new[] { run }.Concat(history.Runs).Take(20).ToList() };
                // Persist the attempt BEFORE contacting Codex. Ambiguous failures
                // or process restarts must never automatically resend a paid turn.
                SettingsStore.AtomicWrite(historyPath, next);
                history = next;
            }
            try
            {
                var result = await rpc.Send(config.CodexHome, Path.Combine(settings.DataDirectory, "message-workspace"), plan.Model, plan.Message, ct);
                run = run with { Status = "succeeded", FinishedAt = clock.GetUtcNow(), Reply = result.Reply,
                    Primary = result.Primary, Weekly = result.Weekly, QuotaError = result.QuotaError };
                if (result.QuotaError == null) quota.RecordCodexUsage(config, result.Primary, result.Weekly);
            }
            catch (BridgeException e) { run = run with { Status = "failed", FinishedAt = clock.GetUtcNow(), Error = e.Message }; }
            catch (OperationCanceledException) { run = run with { Status = "interrupted", FinishedAt = clock.GetUtcNow(), Error = "发送被中断，结果未知；不会自动重发。" }; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            { run = run with { Status = "failed", FinishedAt = clock.GetUtcNow(), Error = "消息连接或服务目录访问失败；不会自动重发。" }; }
            lock (gate)
            {
                history = history with { Runs = history.Runs.Select(r => r.Id == run.Id ? run : r).ToList() };
                SettingsStore.AtomicWrite(historyPath, history);
            }
            return run;
        }
        finally { sendGate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Run(true, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) when (e is BridgeException or IOException or UnauthorizedAccessException)
            { logger.LogWarning("Could not execute scheduled message: {Error}", e is BridgeException ? e.Message : "Message history directory is not writable."); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    static void RequireManaged(BridgeSettings config)
    {
        if (!config.CodexEnabled || config.CodexMode != "managed")
            throw new BridgeException("发送消息需要启用 Codex，选择自动续期模式，并登录独立的可写服务目录。");
    }
    static string DateKey(DateTimeOffset now) => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    static DateTimeOffset DueTime(DateTimeOffset now, string time)
    {
        var local = now.Date.Add(TimeOnly.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture).ToTimeSpan());
        return new DateTimeOffset(local, now.Offset);
    }
    public static bool IsDue(DateTimeOffset now, string time, string? lastDate)
    {
        var due = DueTime(now, time);
        // Allow polling/network latency, but don't catch up long-missed sends.
        return lastDate != DateKey(now) && now >= due && now < due.AddMinutes(5);
    }
}
