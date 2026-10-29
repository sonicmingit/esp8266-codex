using System.Security.Cryptography;
using System.Text.Json;

namespace AIClockBridge.Linux;

public static class Credentials
{
    public static string CodexHome(BridgeSettings s) => s.CodexMode == "managed" ? s.CodexManagedHome : s.CodexHome;
    public static string FilePath(BridgeSettings s, string provider) => Path.Combine(provider == "codex" ? s.CodexHome : s.ClaudeHome,
        provider == "codex" ? "auth.json" : ".credentials.json");

    // Invalidate cached quota if a mapped file is replaced or an account changes.
    // Only a one-way fingerprint is persisted, never the credential contents.
    public static string Identity(BridgeSettings s, string provider)
    {
        var path = provider == "codex" ? Path.Combine(CodexHome(s), "auth.json") : FilePath(s, provider);
        var prefix = path + (provider == "codex" ? s.CodexMode : "readonly");
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) return prefix + ":too-large";
            return prefix + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }
        catch (IOException) { return prefix + ":missing"; }
        catch (UnauthorizedAccessException) { return prefix + ":unreadable"; }
    }

    public static async Task<JsonDocument> Read(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (new FileInfo(path).Length > 1024 * 1024) throw new BridgeException("凭据文件过大，请检查目录设置。");
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                if (attempt < 2) { await Task.Delay(80, ct); continue; }
                throw new BridgeException(e is JsonException ? "凭据文件格式错误，请检查文件或重新登录。"
                    : "无法读取登录凭据，请检查目录映射、文件权限和 CLI 登录状态。");
            }
        }
    }

    public static string? AccountId(JsonElement tokens)
    {
        var id = JsonValues.String(tokens, "account_id");
        if (id != null) return id;
        try
        {
            var part = JsonValues.String(tokens, "id_token")?.Split('.')[1].Replace('-', '+').Replace('_', '/');
            if (part == null) return null;
            using var doc = JsonDocument.Parse(Convert.FromBase64String(part.PadRight((part.Length + 3) / 4 * 4, '=')));
            var auth = JsonValues.Object(doc.RootElement, "https://api.openai.com/auth");
            return auth.HasValue ? JsonValues.String(auth.Value, "chatgpt_account_id") : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or IndexOutOfRangeException) { return null; }
    }
}
