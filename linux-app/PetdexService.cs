using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AIClockBridge.Linux;

public sealed record Pet(string Slug, string DisplayName, string Kind, string SpritesheetUrl);
public sealed record AnimationState(string Id, string Label, int Row, int Frames, int DurationMs);

// Image conversion is adapted from windows-app/AIClockBridge/PetdexService.cs.
public sealed class PetdexService(HttpClient http)
{
    public static readonly AnimationState[] Animations =
    [
        new("idle", "待机", 0, 6, 1100), new("running-right", "向右跑", 1, 8, 1060),
        new("running-left", "向左跑", 2, 8, 1060), new("waving", "挥手", 3, 4, 700),
        new("jumping", "跳跃", 4, 5, 840), new("failed", "失败", 5, 8, 1220),
        new("waiting", "等待", 6, 6, 1010), new("running", "原地跑", 7, 6, 820), new("review", "思考", 8, 6, 1030)
    ];
    readonly SemaphoreSlim gate = new(1);
    List<Pet>? pets;
    DateTimeOffset loadedAt;
    readonly Dictionary<string, byte[]> gifs = new();

    public async Task<IReadOnlyList<Pet>> Manifest(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (pets != null && DateTimeOffset.UtcNow - loadedAt < TimeSpan.FromHours(6)) return pets;
            try
            {
                var body = await Download(new Uri("https://assets.petdex.dev/manifests/petdex-v1.json"), 8 * 1024 * 1024, ct);
                using var doc = JsonDocument.Parse(body);
                var list = new List<Pet>();
                foreach (var entry in doc.RootElement.GetProperty("pets").EnumerateArray())
                {
                    var slug = JsonValues.String(entry, "slug"); var sheet = JsonValues.String(entry, "spritesheetUrl");
                    if (string.IsNullOrEmpty(slug) || !Uri.TryCreate(sheet, UriKind.Absolute, out var url)
                        || url.Scheme != "https" || url.Host != "assets.petdex.dev") continue;
                    list.Add(new(slug, JsonValues.String(entry, "displayName") ?? slug, JsonValues.String(entry, "kind") ?? "", url.AbsoluteUri));
                }
                if (list.Count == 0) throw new BridgeException("petdex 图库为空或格式已变化。", 502);
                pets = list.DistinctBy(p => p.Slug).ToList(); loadedAt = DateTimeOffset.UtcNow;
                return pets;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException)
            {
                if (pets != null) return pets;
                throw new BridgeException("无法加载 petdex 图库，请检查网络后重试。", 502);
            }
        }
        finally { gate.Release(); }
    }

    public async Task<byte[]> Gif(string slug, string animation, string slot, CancellationToken ct)
    {
        DeviceService.ValidateSlot(slot);
        var state = Animations.FirstOrDefault(a => a.Id == animation) ?? throw new BridgeException("动画类型无效。");
        var pet = (await Manifest(ct)).FirstOrDefault(p => p.Slug == slug) ?? throw new BridgeException("没有找到该宠物。", 404);
        var key = $"{slug}:{animation}:{slot}";
        await gate.WaitAsync(ct);
        try
        {
            if (gifs.TryGetValue(key, out var existing)) return existing;
            var bytes = await Download(new Uri(pet.SpritesheetUrl), 16 * 1024 * 1024, ct);
            try
            {
                var info = Image.Identify(bytes);
                if (info.Width != 1536 || info.Height != 1872) throw new BridgeException("petdex 精灵图尺寸不兼容。", 502);
                using var sheet = Image.Load<Rgba32>(bytes);
                var gif = BuildGif(sheet, state, slot == "claude" ? 111 : 120, 120);
                if (gifs.Count >= 32) gifs.Remove(gifs.Keys.First());
                gifs[key] = gif;
                return gif;
            }
            catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException)
            { throw new BridgeException("petdex 精灵图解码失败。", 502); }
        }
        finally { gate.Release(); }
    }

    async Task<byte[]> Download(Uri url, int limit, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new BridgeException($"petdex 下载失败（HTTP {(int)response.StatusCode}）。", 502);
        if (response.Content.Headers.ContentLength > limit) throw new BridgeException("petdex 资源过大。", 502);
        using var source = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream();
        var buffer = new byte[16384]; int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        { if (output.Length + read > limit) throw new BridgeException("petdex 资源过大。", 502); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
        return output.ToArray();
    }

    public static byte[] BuildGif(Image<Rgba32> sheet, AnimationState state, int width, int height)
    {
        var scale = Math.Min(width / 192.0, height / 208.0);
        var w = (int)Math.Round(192 * scale); var h = (int)Math.Round(208 * scale);
        using var gif = new Image<Rgba32>(width, height);
        gif.Metadata.GetGifMetadata().RepeatCount = 0;
        for (var frame = 0; frame < Math.Min(8, state.Frames); frame++)
        {
            using var crop = sheet.Clone(c => c.Crop(new Rectangle(frame * 192, state.Row * 208, 192, 208)).Resize(w, h));
            using var image = new Image<Rgba32>(width, height, Color.Black);
            image.Mutate(c => c.DrawImage(crop, new Point((width - w) / 2, (height - h) / 2), 1));
            image.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = Math.Max(5, state.DurationMs / state.Frames / 10);
            gif.Frames.AddFrame(image.Frames.RootFrame);
        }
        gif.Frames.RemoveFrame(0);
        using var output = new MemoryStream(); gif.SaveAsGif(output); return output.ToArray();
    }
}
