using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIClockReceiver
{
    internal sealed class PetAnimation
    {
        private readonly BitmapSource[] idle, working, waiting;
        public PetAnimation(BitmapSource[] idle, BitmapSource[] working, BitmapSource[] waiting)
        {
            this.idle = idle; this.working = working; this.waiting = waiting;
        }
        public BitmapSource Frame(bool input, bool busy, double seconds)
        {
            var row = input ? waiting : busy ? working : idle;
            double cycle = input ? 1.01 : busy ? 0.82 : 1.65;
            return row[(int)((seconds % cycle) * row.Length / cycle)];
        }
        public static PetAnimation Decode(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes, false))
            {
                BitmapSource sheet;
                try { sheet = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand).Frames[0]; }
                catch (NotSupportedException ex) { throw new InvalidDataException("此电脑无法解码 WebP，请安装 Windows 的 WebP 图像扩展。", ex); }
                if (sheet.PixelWidth != 1536 || sheet.PixelHeight != 1872)
                    throw new InvalidDataException("Petdex 图集尺寸应为 1536 × 1872");
                var pixels = new FormatConvertedBitmap(sheet, PixelFormats.Pbgra32, null, 0);
                return new PetAnimation(Row(pixels, 0), Row(pixels, 7), Row(pixels, 6));
            }
        }
        private static BitmapSource[] Row(BitmapSource sheet, int row)
        {
            const int w = 192, h = 208, stride = w * 4;
            var frames = new BitmapSource[6];
            var data = new byte[h * stride];
            for (int i = 0; i < frames.Length; i++)
            {
                sheet.CopyPixels(new Int32Rect(i * w, row * h, w, h), data, stride, 0);
                frames[i] = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, data, stride);
                frames[i].Freeze();
            }
            return frames;
        }
    }

    internal sealed class CatalogPet
    {
        public string Slug { get; set; }
        public string Name { get; set; }
        public string Url { get; set; }
        public override string ToString() { return Name; }
    }

    internal sealed class PetLibrary : IDisposable
    {
        private const string Manifest = "https://assets.petdex.dev/manifests/petdex-v1.json";
        private const int Limit = 12 * 1024 * 1024;
        private readonly string folder;
        private readonly HttpClient client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = Limit };
        private List<CatalogPet> catalog;
        public bool Offline { get; private set; }
        public PetLibrary(string directory) { folder = directory; }
        private static bool ValidId(string slug)
        {
            return slug != null && Regex.IsMatch(slug, @"\A[A-Za-z0-9_-]{1,100}\z");
        }
        internal static List<CatalogPet> Parse(byte[] data)
        {
            if (data.Length > 8 * 1024 * 1024) throw new InvalidDataException("宠物目录过大");
            var json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            var root = json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(data));
            object entries;
            if (root == null || !root.TryGetValue("pets", out entries) || !(entries is IList))
                throw new InvalidDataException("无法识别宠物目录");
            var pets = new List<CatalogPet>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in (IList)entries)
            {
                var o = entry as Dictionary<string, object>;
                object slug, sheet, name; Uri uri;
                if (o == null || !o.TryGetValue("slug", out slug) || !ValidId(slug as string) ||
                    !o.TryGetValue("spritesheetUrl", out sheet) || !Uri.TryCreate(sheet as string, UriKind.Absolute, out uri) ||
                    uri.Scheme != "https" || uri.Host != "assets.petdex.dev" || uri.Port != 443 || uri.UserInfo.Length != 0 ||
                    !uri.AbsolutePath.StartsWith("/pets/", StringComparison.Ordinal) || !seen.Add((string)slug)) continue;
                pets.Add(new CatalogPet { Slug = (string)slug, Url = uri.AbsoluteUri,
                    Name = o.TryGetValue("displayName", out name) && !String.IsNullOrWhiteSpace(name as string) ? (string)name : (string)slug });
            }
            if (pets.Count == 0) throw new InvalidDataException("宠物目录为空");
            return pets;
        }
        private async Task<byte[]> Fetch(string url, CancellationToken token)
        {
            using (var response = await client.GetAsync(url, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
        }
        private void Cache(string name, byte[] bytes)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public async Task<List<CatalogPet>> Catalog(CancellationToken token)
        {
            if (catalog != null && !Offline) return catalog;
            var saved = Path.Combine(folder, "catalog.json");
            try
            {
                var data = await Fetch(Manifest, token).ConfigureAwait(false);
                var list = Parse(data);
                token.ThrowIfCancellationRequested();
                Cache("catalog.json", data);
                catalog = list; Offline = false;
            }
            catch (Exception)
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(saved)) throw;
                catalog = Parse(File.ReadAllBytes(saved)); Offline = true;
            }
            return catalog;
        }
        public PetAnimation Saved(string slug)
        {
            if (!ValidId(slug)) return null;
            var path = Path.Combine(folder, "pet-" + slug + ".sheet");
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > Limit) return null;
            try { return PetAnimation.Decode(File.ReadAllBytes(path)); }
            catch (Exception) { return null; }
        }
        public async Task<PetAnimation> Get(CatalogPet pet, CancellationToken token)
        {
            var cached = await Task.Run(() => Saved(pet.Slug), token).ConfigureAwait(false);
            if (cached != null) return cached;
            var data = await Fetch(pet.Url, token).ConfigureAwait(false);
            var anim = PetAnimation.Decode(data);
            token.ThrowIfCancellationRequested();
            Cache("pet-" + pet.Slug + ".sheet", data);
            return anim;
        }
        public void Dispose() { client.Dispose(); }
    }
}
