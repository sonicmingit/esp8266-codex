using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AIClockBridge.Linux;

public sealed class QuotaService : BackgroundService
{
    readonly SettingsStore settings;
    readonly HttpClient http;
    readonly CodexRpc rpc;
    readonly TimeProvider clock;
    BridgeSettings refreshSettings;
    readonly SemaphoreSlim refreshGate = new(1);
    readonly object gate = new();
    readonly Dictionary<string, UsageState> states = new() { ["codex"] = new(), ["claude"] = new() };
    readonly Dictionary<string, string> identities = new();
    readonly Dictionary<string, DateTimeOffset> cooldowns = new();
    readonly string cachePath;
    public QuotaService(SettingsStore settings, HttpClient http, TimeProvider? clock = null)
    {
        this.settings = settings; this.http = http;
        this.clock = clock ?? TimeProvider.System;
        refreshSettings = settings.Current;
        rpc = new(settings.CodexExecutable);
        cachePath = Path.Combine(settings.DataDirectory, "usage-cache.json");
        try
        {
            var cache = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(cachePath), JsonValues.Options);
            if (cache != null) foreach (var provider in states.Keys.ToArray())
            {
                if (cache.TryGetValue(provider, out var e) && e.Identity == Credentials.Identity(settings.Current, provider)
                    && e.Usage.FetchedAt is { } at && this.clock.GetUtcNow() - at < TimeSpan.FromHours(24))
                {
                    // Probe after the minimum cooldown on startup; old schedules
                    // and activity counters may belong to a previous policy.
                    states[provider] = e.Usage with { Stale = true, NextAttemptAt = e.Usage.Error == null ? null : e.Usage.NextAttemptAt,
                        CurrentRefreshSeconds = InitialInterval(refreshSettings), UnchangedRefreshCount = 0 };
                    identities[provider] = e.Identity;
                    if (e.Usage.Error != null && e.Usage.NextAttemptAt is { } due && due > this.clock.GetUtcNow())
                        cooldowns[provider] = due;
                    else cooldowns[provider] = (e.Usage.AttemptedAt ?? at).AddSeconds(60);
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }
    sealed record CacheEntry(string Identity, UsageState Usage);
    public IReadOnlyDictionary<string, UsageState> Snapshot()
    {
        lock (gate)
        {
            var config = settings.Current;
            return states.ToDictionary(k => k.Key, k =>
            {
                var enabled = k.Key == "codex" ? config.CodexEnabled : config.ClaudeEnabled;
                if (!enabled) return new UsageState { Enabled = false };
                var s = k.Value;
                var interval = CurrentInterval(config, s);
                return s with { Enabled = true, CurrentRefreshSeconds = interval,
                    Stale = s.Stale || s.FetchedAt == null || clock.GetUtcNow() - s.FetchedAt > TimeSpan.FromSeconds(Math.Max(300, interval * 2)) };
            });
        }
    }

    public void SettingsChanged()
    {
        lock (gate)
        {
            var config = settings.Current;
            var policyChanged = config.RefreshSeconds != refreshSettings.RefreshSeconds
                || config.AdaptiveRefreshEnabled != refreshSettings.AdaptiveRefreshEnabled
                || config.MinRefreshSeconds != refreshSettings.MinRefreshSeconds
                || config.MaxRefreshSeconds != refreshSettings.MaxRefreshSeconds
                || config.RefreshUnchangedThreshold != refreshSettings.RefreshUnchangedThreshold;
            refreshSettings = config;
            foreach (var key in states.Keys.ToArray())
            {
                var enabled = key == "codex" ? config.CodexEnabled : config.ClaudeEnabled;
                if (!enabled || !identities.TryGetValue(key, out var identity) || identity != Credentials.Identity(config, key))
                { states[key] = new(); identities.Remove(key); continue; }
                // Device edits retain the learned cadence. Policy edits restart
                // learning without erasing quota or bypassing error cooldowns.
                var interval = policyChanged ? InitialInterval(config) : CurrentInterval(config, states[key]);
                states[key] = states[key] with { CurrentRefreshSeconds = interval,
                    UnchangedRefreshCount = policyChanged ? 0 : states[key].UnchangedRefreshCount };
                if (states[key].AttemptedAt is { } attempted)
                {
                    var due = attempted.AddSeconds(interval);
                    if (cooldowns.TryGetValue(key, out var cooldown) && cooldown > due) due = cooldown;
                    states[key] = states[key] with { NextAttemptAt = due };
                }
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Refresh(false, stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    public async Task Refresh(bool manual, CancellationToken ct)
    {
        if (!await refreshGate.WaitAsync(0, ct)) return;
        try
        {
            // Queries are independent; a Claude outage must not prevent Codex from updating.
            await Task.WhenAll(RefreshProvider("codex", manual, ct), RefreshProvider("claude", manual, ct));
            lock (gate)
            {
                var cache = states.Where(k => k.Value.FetchedAt != null && identities.ContainsKey(k.Key))
                    .ToDictionary(k => k.Key, k => new CacheEntry(identities[k.Key], k.Value));
                try { SettingsStore.AtomicWrite(cachePath, cache); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* quota stays available in memory */ }
            }
        }
        finally { refreshGate.Release(); }
    }

    public async Task RefreshAfterLogin(CancellationToken ct)
    {
        lock (gate)
        {
            cooldowns.Remove("codex");
            states["codex"] = states["codex"] with { NextAttemptAt = null };
        }
        await Refresh(true, ct);
    }

    async Task RefreshProvider(string provider, bool manual, CancellationToken ct)
    {
        var config = settings.Current;
        if (!(provider == "codex" ? config.CodexEnabled : config.ClaudeEnabled)) return;
        var identity = Credentials.Identity(config, provider);
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (!identities.TryGetValue(provider, out var old) || old != identity)
            { states[provider] = new(); identities[provider] = identity; }
            var s = states[provider];
            if (cooldowns.TryGetValue(provider, out var until) && now < until)
            { states[provider] = s with { NextAttemptAt = s.NextAttemptAt is { } scheduled && scheduled > until ? scheduled : until }; return; }
            if (!manual && s.NextAttemptAt is { } due && now < due) return;
        }
        (QuotaWindow? Primary, QuotaWindow? Weekly) windows = default;
        string? error = null;
        var backoff = 60;
        try
        {
            windows = provider == "codex" && config.CodexMode == "managed"
                ? await rpc.Fetch(Credentials.CodexHome(config), ct) : await FetchReadonly(config, provider, ct);
        }
        catch (BridgeException e) { error = e.Message; if (e.StatusCode == 429) backoff = 300; }
        catch (HttpRequestException) { error = "额度服务连接失败，请检查网络或代理设置。"; }
        catch (JsonException) { error = "额度响应格式错误，请稍后重试。"; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { error = "额度查询超时，请稍后重试。"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error = "无法访问凭据或服务目录，请检查权限。"; }
        if (ct.IsCancellationRequested) return;
        now = clock.GetUtcNow();
        lock (gate)
        {
            // Discard old-path requests when settings were changed during a network call.
            if (settings.Current != config) return;
            var after = Credentials.Identity(config, provider);
            if (config.CodexMode != "managed" || provider != "codex")
            {
                if (after != identity) { states[provider] = new(); identities[provider] = after; cooldowns[provider] = now.AddSeconds(60); return; }
            }
            identities[provider] = after;
            cooldowns[provider] = now.AddSeconds(backoff);
            var previous = states[provider];
            var interval = CurrentInterval(config, previous);
            var unchanged = 0;
            if (error == null && config.AdaptiveRefreshEnabled && previous.FetchedAt != null)
            {
                // Compare server windows, never the locally ticking countdown.
                if (previous.Primary != windows.Primary || previous.Weekly != windows.Weekly)
                    interval = Math.Max(config.MinRefreshSeconds, interval / 2);
                else
                {
                    unchanged = previous.UnchangedRefreshCount + 1;
                    if (unchanged >= config.RefreshUnchangedThreshold)
                    { interval = Math.Min(config.MaxRefreshSeconds, interval * 2); unchanged = 0; }
                }
            }
            var next = now.AddSeconds(Math.Max(interval, backoff));
            states[provider] = error == null
                ? new UsageState { Primary = windows.Primary, Weekly = windows.Weekly, FetchedAt = now, AttemptedAt = now, NextAttemptAt = next,
                    CurrentRefreshSeconds = interval, UnchangedRefreshCount = unchanged, Enabled = true, Stale = false }
                : previous with { Error = error, AttemptedAt = now, NextAttemptAt = next, CurrentRefreshSeconds = interval,
                    UnchangedRefreshCount = 0, Enabled = true, Stale = true };
        }
    }

    static int InitialInterval(BridgeSettings config) => config.AdaptiveRefreshEnabled
        ? Math.Clamp(config.RefreshSeconds, config.MinRefreshSeconds, config.MaxRefreshSeconds) : config.RefreshSeconds;

    static int CurrentInterval(BridgeSettings config, UsageState state) => !config.AdaptiveRefreshEnabled
        ? config.RefreshSeconds : state.CurrentRefreshSeconds > 0
            ? Math.Clamp(state.CurrentRefreshSeconds, config.MinRefreshSeconds, config.MaxRefreshSeconds) : InitialInterval(config);

    async Task<(QuotaWindow? Primary, QuotaWindow? Weekly)> FetchReadonly(BridgeSettings config, string provider, CancellationToken ct)
    {
        using var credentials = await Credentials.Read(Credentials.FilePath(config, provider), ct);
        var tokens = JsonValues.Object(credentials.RootElement, provider == "codex" ? "tokens" : "claudeAiOauth");
        var token = tokens.HasValue ? JsonValues.String(tokens.Value, provider == "codex" ? "access_token" : "accessToken") : null;
        if (string.IsNullOrEmpty(token)) throw new BridgeException("未找到订阅登录凭据。请使用 ChatGPT / Claude 账号登录，API Key 不适用于此额度查询。");
        using var request = new HttpRequestMessage(HttpMethod.Get, provider == "codex"
            ? "https://chatgpt.com/backend-api/wham/usage" : "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd(provider == "codex" ? "AIClockBridge/0.1.0" : "claude-code/2.1.0");
        if (provider == "claude") request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        if (provider == "codex" && Credentials.AccountId(tokens!.Value) is { } account)
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            throw new BridgeException(code == 401 ? "登录凭据已失效，请在宿主机重新登录；Codex 也可使用独立目录的自动续期模式。"
                : code == 403 ? "账号访问被拒绝（HTTP 403），请检查账号权限或网络访问限制。"
                : code == 429 ? "额度接口限流，至少 5 分钟后重试。" : $"额度接口返回 HTTP {code}，请稍后重试。", code);
        }
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return provider == "codex" ? QuotaParser.Codex(body.RootElement) : QuotaParser.Claude(body.RootElement);
    }

    public object FirmwareStatus()
    {
        var data = Snapshot(); var c = data["claude"]; var x = data["codex"];
        int? Remaining(QuotaWindow? w) => w?.ResetsAt is { } at ? (int)Math.Max(0, (at - clock.GetUtcNow().ToUnixTimeSeconds()) / 60) : null;
        return new
        {
            ts = clock.GetUtcNow().ToUnixTimeSeconds(), music_playing = false,
            utc_offset_min = (int)TimeZoneInfo.Local.GetUtcOffset(clock.GetUtcNow()).TotalMinutes,
            claude = new { status = c.Enabled && c.FetchedAt != null && !c.Stale ? "idle" : "offline", tokens_today = 0,
                session_min = 0, session_window_min = 300, five_hour_pct = c.Primary?.UsedPercent,
                five_hour_reset_min = Remaining(c.Primary), seven_day_pct = c.Weekly?.UsedPercent,
                five_hour_reset_at = c.Primary?.ResetsAt, seven_day_reset_at = c.Weekly?.ResetsAt,
                seven_day_reset_min = Remaining(c.Weekly), needs_input = false },
            codex = new { status = x.Enabled && x.FetchedAt != null && !x.Stale ? "idle" : "offline", tokens_today = 0,
                primary_pct = x.Primary?.UsedPercent, primary_window_min = x.Primary?.WindowMinutes,
                primary_reset_min = Remaining(x.Primary), weekly_pct = x.Weekly?.UsedPercent,
                primary_reset_at = x.Primary?.ResetsAt, weekly_reset_at = x.Weekly?.ResetsAt,
                weekly_window_min = x.Weekly?.WindowMinutes, weekly_reset_min = Remaining(x.Weekly), needs_input = false }
        };
    }

    public void RecordCodexUsage(BridgeSettings config, QuotaWindow? primary, QuotaWindow? weekly)
    {
        lock (gate)
        {
            if (settings.Current != config) return;
            var now = clock.GetUtcNow();
            var interval = config.AdaptiveRefreshEnabled ? config.MinRefreshSeconds : config.RefreshSeconds;
            // Sending a message is an explicit activity signal.
            var due = now.AddSeconds(interval);
            if (cooldowns.TryGetValue("codex", out var cooldown) && cooldown > due) due = cooldown;
            identities["codex"] = Credentials.Identity(config, "codex");
            states["codex"] = new UsageState { Primary = primary, Weekly = weekly, FetchedAt = now,
                AttemptedAt = now, NextAttemptAt = due, CurrentRefreshSeconds = interval, Enabled = true, Stale = false };
            cooldowns["codex"] = cooldown > now.AddSeconds(60) ? cooldown : now.AddSeconds(60);
        }
    }
}
