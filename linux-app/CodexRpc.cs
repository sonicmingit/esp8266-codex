using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AIClockBridge.Linux;

// All sessions for one managed home share this lock, including quota polling.
public sealed class CodexRpc(string executable)
{
    static readonly ConcurrentDictionary<string, SemaphoreSlim> homes = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    internal static SemaphoreSlim HomeLock(string home) =>
        homes.GetOrAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(home)), _ => new(1));

    public Task<(QuotaWindow? Primary, QuotaWindow? Weekly)> Fetch(string home, CancellationToken ct) =>
        WithSession(home, null, TimeSpan.FromSeconds(45), async session =>
        {
            JsonElement usage;
            try { usage = await session.Call("account/rateLimits/read", new { }); }
            catch (BridgeException e) when (e.StatusCode == 401)
            {
                await session.Call("account/read", new { refreshToken = true });
                usage = await session.Call("account/rateLimits/read", new { });
            }
            return QuotaParser.Codex(usage, rpc: true);
        }, ct);

    public Task<List<CodexModel>> Models(string home, CancellationToken ct) =>
        WithSession(home, null, TimeSpan.FromSeconds(45), ReadModels, ct);

    static async Task<List<CodexModel>> ReadModels(Session session)
    {
        var models = new List<CodexModel>();
        string? cursor = null;
        var cursors = new HashSet<string>();
        do
        {
            var result = await session.Call("model/list", new { limit = 100, includeHidden = false, cursor });
            if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new BridgeException("Codex 模型列表响应格式错误。", 502);
            foreach (var entry in data.EnumerateArray())
            {
                var id = JsonValues.String(entry, "model") ?? JsonValues.String(entry, "id");
                if (id == null || models.Any(m => m.Id == id)) continue;
                if (entry.TryGetProperty("inputModalities", out var modalities) && modalities.ValueKind == JsonValueKind.Array
                    && !modalities.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == "text")) continue;
                models.Add(new(id, JsonValues.String(entry, "displayName") ?? id,
                    entry.TryGetProperty("isDefault", out var d) && d.ValueKind == JsonValueKind.True));
            }
            cursor = JsonValues.String(result, "nextCursor");
            if (cursor != null && (!cursors.Add(cursor) || cursors.Count > 20))
                throw new BridgeException("Codex 模型列表分页异常。", 502);
        } while (cursor != null);
        return models;
    }

    public Task<CodexMessageResult> Send(string home, string workspace, string model, string message, CancellationToken ct) =>
        WithSession(home, workspace, TimeSpan.FromMinutes(3), async session =>
        {
            if (!(await ReadModels(session)).Any(m => m.Id == model))
                throw new BridgeException("所选模型不在当前 Codex 模型列表中，请重新加载并选择。");
            var thread = await session.Call("thread/start", new
            {
                model, modelProvider = "openai", cwd = workspace, ephemeral = true,
                approvalPolicy = "never", sandbox = "read-only",
                baseInstructions = "You answer a single short message. Reply concisely in plain text. Do not use tools, read files, run commands, browse, or delegate.",
                developerInstructions = "This is a scheduled subscription message. Only answer the user's message, with no external actions."
            });
            var threadId = JsonValues.String(JsonValues.Object(thread, "thread") ?? default, "id")
                ?? throw new BridgeException("Codex 未返回消息会话标识。", 502);
            string? turnId = null;
            JsonElement? completed = null;
            var replies = new Dictionary<string, string>();
            session.Notification = notification =>
            {
                var parameters = JsonValues.Object(notification, "params");
                if (parameters == null || JsonValues.String(parameters.Value, "threadId") != threadId) return;
                var method = JsonValues.String(notification, "method");
                if (method == "item/completed" && JsonValues.Object(parameters.Value, "item") is { } item)
                {
                    if (turnId != null && JsonValues.String(parameters.Value, "turnId") != turnId) return;
                    AddReply(item, replies);
                }
                if (method == "turn/completed" && JsonValues.Object(parameters.Value, "turn") is { } turn
                    && (turnId == null || JsonValues.String(turn, "id") == turnId)) completed = turn.Clone();
            };
            var started = await session.Call("turn/start", new { threadId, model, input = new[] { new { type = "text", text = message } } });
            var initial = JsonValues.Object(started, "turn") ?? throw new BridgeException("Codex 未返回消息任务。", 502);
            turnId = JsonValues.String(initial, "id") ?? throw new BridgeException("Codex 未返回消息任务标识。", 502);
            if (completed != null && JsonValues.String(completed.Value, "id") != turnId) completed = null;
            if (JsonValues.String(initial, "status") is "completed" or "failed" or "interrupted") completed = initial;
            while (completed == null) await session.ReadNotification();
            var final = completed.Value;
            if (JsonValues.String(final, "status") != "completed")
                throw SafeError(JsonValues.Object(final, "error"), "Codex 消息未完成，请检查额度、模型权限与登录状态。已停止本次发送。");
            if (final.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray()) AddReply(item, replies);
            var reply = string.Join("\n\n", replies.Values).Trim();
            if (reply.Length == 0) throw new BridgeException("Codex 任务已结束，但没有返回文本回复。", 502);
            (QuotaWindow? Primary, QuotaWindow? Weekly) windows = default;
            string? quotaError = null;
            try { windows = QuotaParser.Codex(await session.Call("account/rateLimits/read", new { }), rpc: true); }
            catch (Exception e) when (e is BridgeException or OperationCanceledException or IOException or JsonException)
            { quotaError = "消息已完成，但发送后的额度查询失败；请等待后台刷新。"; }
            return new CodexMessageResult(reply, windows.Primary, windows.Weekly, quotaError);
        }, ct);

    static void AddReply(JsonElement item, Dictionary<string, string> replies)
    {
        if (JsonValues.String(item, "type") != "agentMessage" || JsonValues.String(item, "phase") == "commentary") return;
        var text = JsonValues.String(item, "text");
        if (string.IsNullOrEmpty(text)) return;
        replies[JsonValues.String(item, "id") ?? "final"] = text;
        if (replies.Values.Sum(t => t.Length) > 16000)
            throw new BridgeException("模型回复超过 16000 字符，已停止本次消息。", 502);
    }

    async Task<T> WithSession<T>(string home, string? workspace, TimeSpan duration, Func<Session, Task<T>> action, CancellationToken ct)
    {
        var semaphore = HomeLock(home);
        await semaphore.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(duration);
            Directory.CreateDirectory(home);
            var probe = Path.Combine(home, ".aiclock-write-" + Guid.NewGuid().ToString("N"));
            try { await File.WriteAllTextAsync(probe, "", timeout.Token); File.Delete(probe); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { throw new BridgeException("自动续期模式需要独立且可写的 Codex 目录。"); }
            if (workspace != null) Directory.CreateDirectory(workspace);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workspace ?? home
            };
            start.ArgumentList.Add("app-server");
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
            if (workspace != null)
                foreach (var value in new[] { "model_provider=\"openai\"", "features.shell_tool=false", "features.unified_exec=false", "features.apply_patch_freeform=false", "features.multi_agent=false", "web_search=\"disabled\"", "mcp_servers={}", "notify=[]" })
                { start.ArgumentList.Add("-c"); start.ArgumentList.Add(value); }
            start.Environment["CODEX_HOME"] = home;
            start.Environment.Remove("OPENAI_API_KEY"); start.Environment.Remove("CODEX_ACCESS_TOKEN");
            using var process = new Process { StartInfo = start };
            var started = false;
            try
            {
                try { started = process.Start(); if (!started) throw new InvalidOperationException(); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                { throw new BridgeException("无法启动 Codex CLI。请安装 codex 或配置 AICLOCK_CODEX_EXECUTABLE。", 502); }
                var diagnostics = new StartupDiagnostics();
                var stderr = Drain(process.StandardError, diagnostics, timeout.Token);
                var session = new Session(process, timeout.Token, diagnostics, stderr);
                await session.Call("initialize", new { clientInfo = new { name = "aiclock_linux", title = "AIClock Linux Bridge", version = "0.1.0" } });
                await session.Write(new { method = "initialized", @params = new { } });
                var account = await session.Call("account/read", new { refreshToken = false });
                var details = JsonValues.Object(account, "account");
                if (!details.HasValue || JsonValues.String(details.Value, "type") != "chatgpt")
                    throw new BridgeException("自动续期模式需要 ChatGPT 登录，请在此服务的 Codex 目录中完成登录。");
                return await action(session);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new BridgeException(workspace == null ? "Codex 账户查询超时。" : "消息发送超过 3 分钟，已停止。可能已消耗额度，请查看账户窗口；不会自动重发。", 504); }
            finally
            {
                timeout.Cancel();
                if (started)
                {
                    try { process.StandardInput.Close(); if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
        }
        finally { semaphore.Release(); }
    }

    sealed class Session(Process process, CancellationToken ct, StartupDiagnostics diagnostics, Task stderr)
    {
        int id;
        public Action<JsonElement>? Notification;
        public async Task Write(object message)
        {
            try
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct);
                await process.StandardInput.FlushAsync(ct);
            }
            catch (IOException) { ct.ThrowIfCancellationRequested(); throw await EarlyExit(); }
        }
        async Task<BridgeException> EarlyExit()
        {
            // Give the separate stderr reader time to consume the final startup error.
            try { await stderr.WaitAsync(TimeSpan.FromMilliseconds(500), ct); }
            catch (TimeoutException) { }
            return diagnostics.EarlyExit(process);
        }
        async Task<JsonElement> Read()
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct);
                if (line == null) throw await EarlyExit();
                if (line.Length > 2_000_000) throw new BridgeException("Codex App Server 响应过大。", 502);
                try { using var doc = JsonDocument.Parse(line); return doc.RootElement.Clone(); }
                catch (JsonException) { }
            }
        }
        void Handle(JsonElement root)
        {
            if (root.TryGetProperty("id", out _) && root.TryGetProperty("method", out _))
                throw new BridgeException("模型请求了工具或人工操作，已停止。本功能仅支持简单文本回复。", 502);
            Notification?.Invoke(root);
        }
        public async Task ReadNotification() => Handle(await Read());
        public async Task<JsonElement> Call(string method, object parameters)
        {
            var requestId = ++id;
            await Write(new { id = requestId, method, @params = parameters });
            while (true)
            {
                var root = await Read();
                if (root.TryGetProperty("method", out _)) { Handle(root); continue; }
                if (!root.TryGetProperty("id", out var replyId) || !replyId.TryGetInt32(out var rid) || rid != requestId) continue;
                if (JsonValues.Object(root, "error") is { } error) throw SafeError(error, "Codex 账户接口请求失败，请检查网络、账号与 CLI 版本。");
                if (root.TryGetProperty("result", out var result)) return result;
                throw new BridgeException("Codex App Server 响应格式错误。", 502);
            }
        }
    }

    static BridgeException SafeError(JsonElement? error, string fallback)
    {
        var text = error is { } value ? JsonValues.String(value, "message") ?? "" : "";
        var auth = new[] { "401", "unauthorized", "authentication", "access token", "refresh token", "not logged", "not authenticated", "sign in" }
            .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        var limited = text.Contains("429", StringComparison.OrdinalIgnoreCase) || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("usage limit", StringComparison.OrdinalIgnoreCase);
        return new BridgeException(auth ? "Codex 登录凭据失效，请重新登录。" : limited ? "Codex 额度不足或请求限流，请稍后重试。" : fallback, auth ? 401 : limited ? 429 : 502);
    }

    sealed class StartupDiagnostics
    {
        readonly object gate = new();
        string tail = "";
        bool permission, missingBubblewrap, configuration;

        public void Observe(string chunk)
        {
            lock (gate)
            {
                // Retain only a short overlap for matches across reads. Never expose raw stderr,
                // which may contain account details, tokens or configured secrets.
                var text = tail + chunk;
                permission |= text.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase);
                missingBubblewrap |= text.Contains("could not find bubblewrap", StringComparison.OrdinalIgnoreCase);
                configuration |= text.Contains("error loading config", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("failed to load config", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("toml parse error", StringComparison.OrdinalIgnoreCase);
                tail = text.Length > 256 ? text[^256..] : text;
            }
        }

        public BridgeException EarlyExit(Process process)
        {
            lock (gate)
            {
                var code = process.HasExited ? $"（退出码 {process.ExitCode}）" : "";
                var reason = configuration ? "CLI 输出提示配置加载失败，请检查自动续期目录中的 config.toml。"
                    : permission ? "CLI 输出提示权限不足，请检查 Codex 目录内部文件和子目录的属主、权限及容器沙箱限制。"
                    : missingBubblewrap ? "CLI 输出提示缺少 bubblewrap，请更新并重新构建服务镜像。"
                    : "请检查 CLI 版本、配置与登录状态。";
                return new BridgeException($"Codex App Server 提前退出{code}。{reason}", 502);
            }
        }
    }

    static async Task Drain(StreamReader reader, StartupDiagnostics diagnostics, CancellationToken ct)
    {
        try
        {
            var buffer = new char[4096]; int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
                diagnostics.Observe(new string(buffer, 0, count));
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
}
