using System;
using System.IO;
using System.Web.Script.Serialization;

namespace AIClockReceiver
{
    public sealed class Preferences
    {
        public string Address { get; set; }
        public bool Dual { get; set; }
        // Dual also remembers which full view the strip expands into.
        public bool Compact { get; set; }
        public bool Remaining { get; set; }
        public string ClaudePet { get; set; }
        public string CodexPet { get; set; }
        public int DockSide { get; set; }
        public string DockMonitor { get; set; }
        public double DockFraction { get; set; }
        public Preferences Copy() { return (Preferences)MemberwiseClone(); }
        public bool Pinned { get; set; }
        public int Source { get; set; }

        public static Uri Endpoint(string input)
        {
            if (String.IsNullOrWhiteSpace(input)) return null;
            input = input.Trim();
            bool bare = !input.Contains("://");
            Uri uri;
            if (!Uri.TryCreate(bare ? "http://" + input : input, UriKind.Absolute, out uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || uri.Host.Length == 0 ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Port == 0)
                throw new FormatException("请输入主机:端口或完整的 HTTP(S) 地址");
            var target = new UriBuilder(uri);
            string authority = input.Split('/')[0];
            if (bare && authority.Substring(authority.LastIndexOf(']') + 1).IndexOf(':') < 0) target.Port = 8765;
            string path = uri.AbsolutePath.TrimEnd('/');
            target.Path = path.EndsWith("/status", StringComparison.Ordinal) ? path : path + "/status";
            // Windows Bridge 0.4.6 excludes only this exact viewer marker from passive device pairing.
            target.Query = "client=watch";
            return target.Uri;
        }

        public static Preferences Load(string file)
        {
            if (!File.Exists(file)) return new Preferences();
            var result = new JavaScriptSerializer().Deserialize<Preferences>(File.ReadAllText(file));
            if (result == null) throw new InvalidDataException("设置文件为空");
            Endpoint(result.Address);
            if (result.Source < 0 || result.Source > 2) result.Source = 0;
            return result;
        }

        public void Save(string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(this));
                if (File.Exists(file)) File.Replace(temp, file, null);
                else File.Move(temp, file);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
