using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AIClockBridge.Linux;

public sealed class CodexLoginService(SettingsStore settings) : IAsyncDisposable
{
    readonly object gate = new();
    readonly StringBuilder output = new();
    Process? process;
    bool starting;
    DateTimeOffset? startedAt;
    string? error;

    public object Snapshot()
    {
        lock (gate)
        {
            var running = process is { } p && !HasExited(p);
            return new { running, startedAt, exitCode = process is { } done && HasExited(done) ? SafeExitCode(done) : (int?)null,
                output = output.ToString(), error };
        }
    }

    public async Task<object> Start(BridgeSettings config, CancellationToken ct)
    {
        if (!config.CodexEnabled || config.CodexMode != "managed")
            throw new BridgeException("请先启用 Codex 并切换到自动续期模式。", 409);
        var home = Credentials.CodexHome(config);
        var homeLock = CodexRpc.HomeLock(home);
        lock (gate)
        {
            if (starting || process is { } existing && !HasExited(existing))
                throw new BridgeException("登录流程正在运行，请在控制台查看设备码。", 409);
            starting = true;
            output.Clear(); error = null; startedAt = DateTimeOffset.UtcNow;
        }
        try { await homeLock.WaitAsync(ct); }
        catch
        {
            lock (gate) starting = false;
            throw;
        }
        lock (gate)
        {
            try { Directory.CreateDirectory(home); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                starting = false; homeLock.Release(); error = "无法访问自动续期凭据目录，请检查路径和权限。";
                throw new BridgeException(error);
            }
            var info = new ProcessStartInfo(settings.CodexExecutable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = home
            };
            info.ArgumentList.Add("-c"); info.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
            info.ArgumentList.Add("login"); info.ArgumentList.Add("--device-auth");
            info.Environment["CODEX_HOME"] = home;
            info.Environment.Remove("OPENAI_API_KEY"); info.Environment.Remove("CODEX_ACCESS_TOKEN");
            var next = new Process { StartInfo = info, EnableRaisingEvents = true };
            try
            {
                if (!next.Start()) throw new InvalidOperationException("Process.Start returned false");
                process = next;
                starting = false;
                _ = Drain(next.StandardOutput);
                _ = Drain(next.StandardError);
                _ = Monitor(next, homeLock);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                starting = false; homeLock.Release(); next.Dispose(); error = "无法启动 Codex CLI，请检查镜像和 CLI 安装。";
                throw new BridgeException(error, 502);
            }
            return Snapshot();
        }
    }

    public object Cancel()
    {
        lock (gate)
        {
            if (process is { } p && !HasExited(p))
            {
                try { p.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return Snapshot();
    }

    async Task Drain(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                line = Regex.Replace(line, "(?i)(access_token|refresh_token|id_token|authorization)\\s*[:=]\\s*[^\\s,\\\"]+", "$1=[已隐藏]");
                lock (gate)
                {
                    output.AppendLine(line);
                    if (output.Length > 16000) output.Remove(0, output.Length - 16000);
                }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    async Task Monitor(Process watched, SemaphoreSlim homeLock)
    {
        try { await watched.WaitForExitAsync(); }
        catch (InvalidOperationException) { }
        finally { homeLock.Release(); }
    }

    static bool HasExited(Process p) { try { return p.HasExited; } catch (InvalidOperationException) { return true; } }
    static int? SafeExitCode(Process p) { try { return p.ExitCode; } catch (InvalidOperationException) { return null; } }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        Process? current;
        lock (gate) current = process;
        if (current is { } p)
        {
            try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or TimeoutException) { }
        }
        lock (gate) { process?.Dispose(); process = null; }
    }
}
