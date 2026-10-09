using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AIClockBridge.Linux;

// Short-lived stdio sessions reload credentials on every poll. No thread/start
// or turn/start calls are made, so this helper never submits a model task.
public sealed class CodexRpc(string executable)
{
    public async Task<(QuotaWindow? Primary, QuotaWindow? Weekly)> Fetch(string codexHome, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        ct = timeout.Token;
        Directory.CreateDirectory(codexHome);
        // Fail early if this directory cannot persist refreshed credentials.
        var probe = Path.Combine(codexHome, ".aiclock-write-" + Guid.NewGuid().ToString("N"));
        try { await File.WriteAllTextAsync(probe, "", ct); File.Delete(probe); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new BridgeException("自动续期模式需要独立且可写的 Codex 目录。"); }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = codexHome
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
        start.Environment["CODEX_HOME"] = codexHome;
        // Do not let an inherited API key or external-token mode override managed login.
        start.Environment.Remove("OPENAI_API_KEY");
        start.Environment.Remove("CODEX_ACCESS_TOKEN");
        using var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            try { started = process.Start(); if (!started) throw new InvalidOperationException(); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            { throw new BridgeException("无法启动 Codex CLI。请安装 codex 或配置 AICLOCK_CODEX_EXECUTABLE。", 502); }
            // Drain stderr without returning or logging potentially sensitive CLI output.
            var stderr = Drain(process.StandardError, ct);
            var id = 0;
            async Task<JsonElement> Call(string method, object parameters)
            {
                var requestId = ++id;
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id = requestId, method, @params = parameters }).AsMemory(), ct);
                await process.StandardInput.FlushAsync(ct);
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(ct);
                    if (line == null) throw new BridgeException("Codex App Server 提前退出，请检查 CLI 版本与登录状态。", 502);
                    if (line.Length > 2_000_000) throw new BridgeException("Codex App Server 响应过大。", 502);
                    JsonDocument message;
                    try { message = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                    using (message)
                    {
                        var root = message.RootElement;
                        if (!root.TryGetProperty("id", out var replyId) || !replyId.TryGetInt32(out var rid) || rid != requestId) continue;
                        if (root.TryGetProperty("error", out var error))
                        {
                            var text = JsonValues.String(error, "message") ?? "";
                            // Classify but never echo arbitrary server messages containing tokens.
                            var auth = new[] { "401", "unauthorized", "authentication", "access token", "refresh token", "not logged", "not authenticated", "sign in" }
                                .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
                            var limited = text.Contains("429", StringComparison.OrdinalIgnoreCase) || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
                            throw new BridgeException(auth ? "Codex 登录凭据失效，请重新登录。" : limited ? "Codex 请求限流，稍后重试。" : "Codex 账户接口请求失败，请检查网络、账号与 CLI 版本。", auth ? 401 : limited ? 429 : 502);
                        }
                        if (root.TryGetProperty("result", out var result)) return result.Clone();
                        throw new BridgeException("Codex App Server 响应格式错误。", 502);
                    }
                }
            }
            await Call("initialize", new { clientInfo = new { name = "aiclock_linux", title = "AIClock Linux Bridge", version = "0.1.0" } });
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}".AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            var account = await Call("account/read", new { refreshToken = false });
            var details = JsonValues.Object(account, "account");
            if (!details.HasValue || JsonValues.String(details.Value, "type") != "chatgpt")
                throw new BridgeException("自动续期模式需要 ChatGPT 登录，请在此服务的 Codex 目录中完成登录。");
            JsonElement usage;
            try { usage = await Call("account/rateLimits/read", new { }); }
            catch (BridgeException e) when (e.StatusCode == 401)
            {
                await Call("account/read", new { refreshToken = true });
                usage = await Call("account/rateLimits/read", new { });
            }
            return QuotaParser.Codex(usage, rpc: true);
        }
        finally
        {
            if (started)
            {
                try { process.StandardInput.Close(); if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
    }

    static async Task Drain(StreamReader reader, CancellationToken ct)
    {
        try { var buffer = new char[4096]; while (await reader.ReadAsync(buffer.AsMemory(), ct) > 0) { } }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
}
