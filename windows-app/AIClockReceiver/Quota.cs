using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Media.Imaging;

namespace AIClockReceiver
{
    public sealed class UsageWindow : INotifyPropertyChanged
    {
        private readonly string period;
        private bool remaining;
        public string Label { get { return period + (remaining ? "剩余" : "已用"); } }
        public double Used { get; private set; }
        public double DisplayValue { get { return remaining ? 100 - Used : Used; } }
        public string Percent { get { return (remaining ? Math.Ceiling(DisplayValue) : Math.Floor(DisplayValue)) + "%"; } }
        internal void SetRemaining(bool value)
        {
            if (remaining == value) return;
            remaining = value;
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(""));
        }
        public string Reset { get; private set; }
        public event PropertyChangedEventHandler PropertyChanged;
        private readonly double? seconds;

        internal UsageWindow(string label, double used, double? minutes, double? epoch, double? server)
        {
            period = label;
            Used = Math.Min(100, used);
            if (epoch >= 1577836800 && epoch <= 32503680000 && server >= 1577836800 && server <= 32503680000)
                seconds = Math.Max(0, epoch.Value - server.Value);
            else if (minutes >= 0 && minutes <= 525600)
                seconds = minutes * 60 + 30;
            Update(0);
        }

        internal void Update(double elapsed)
        {
            if (!seconds.HasValue) { Reset = "重置时间未知"; return; }
            int m = (int)Math.Ceiling(Math.Max(0, seconds.Value - elapsed) / 60);
            string reset = m == 0 ? "等待额度更新" : m < 60 ? m + " 分后重置" :
                m < 1440 ? m / 60 + " 时" + (m % 60 == 0 ? "" : " " + m % 60 + " 分") + "后重置" :
                m / 1440 + " 天" + (m % 1440 < 60 ? "" : " " + m % 1440 / 60 + " 时") + "后重置";
            if (reset == Reset) return;
            Reset = reset;
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Reset"));
        }
    }

    public sealed class Provider : INotifyPropertyChanged
    {
        public string Name { get; private set; }
        public string State { get; private set; }
        public bool Waiting { get; private set; }
        public bool Present { get; private set; }
        public string Activity { get; private set; }
        public BitmapSource Pet { get; private set; }
        public List<UsageWindow> Windows { get; private set; }
        public event PropertyChangedEventHandler PropertyChanged;

        internal Provider(string name, Dictionary<string, object> data, double? ts)
        {
            Name = name;
            Present = data != null;
            data = data ?? new Dictionary<string, object>();
            object value;
            State = data.TryGetValue("status", out value) ? value as string : null;
            Waiting = data.TryGetValue("needs_input", out value) && true.Equals(value);
            Windows = new List<UsageWindow>();
            bool claude = name == "Claude";
            string primary = claude ? "five_hour" : "primary";
            string week = claude ? "seven_day" : "weekly";
            double duration = claude ? 300 : Snapshot.Number(data, "primary_window_min") ?? 300;
            if (duration > 0 && duration < 1440) Add(data, primary, "5 小时", ts);
            if (!Add(data, week, "周额度", ts) && !claude && duration >= 1440)
                Add(data, primary, "周额度", ts);
        }

        private bool Add(Dictionary<string, object> data, string prefix, string label, double? ts)
        {
            double? used = Snapshot.Number(data, prefix + "_pct");
            if (!used.HasValue || used.Value < 0) return false;
            Windows.Add(new UsageWindow(label, used.Value, Snapshot.Number(data, prefix + "_reset_min"),
                Snapshot.Number(data, prefix + "_reset_at"), ts));
            return true;
        }

        internal void Tick(bool stale, double elapsed, double motion, PetAnimation animation, bool remaining)
        {
            string activity = stale ? "缓存数据" : Waiting ? "待确认" : State == "working" ? "工作中" :
                State == "idle" ? "空闲" : State == "offline" ? "来源离线" : "暂无状态";
            if (activity != Activity) { Activity = activity; Notify("Activity"); }
            var pet = animation.Frame(Waiting, State == "working", stale || !Present ? 0 : motion);
            if (Pet != pet) { Pet = pet; Notify("Pet"); }
            foreach (var window in Windows) { window.SetRemaining(remaining); window.Update(elapsed); }
        }

        private void Notify(string name)
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class Snapshot
    {
        public Provider Claude, Codex;
        public readonly Stopwatch Age = Stopwatch.StartNew();
        public static Snapshot Parse(string json)
        {
            var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("响应不是桥接状态");
            var c = ObjectField(root, "claude");
            var x = ObjectField(root, "codex");
            if (c == null && x == null) throw new InvalidDataException("响应缺少额度来源");
            double? ts = Number(root, "ts");
            return new Snapshot { Claude = new Provider("Claude", c, ts), Codex = new Provider("Codex", x, ts) };
        }

        private static Dictionary<string, object> ObjectField(Dictionary<string, object> data, string key)
        {
            object value;
            return data.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        internal static double? Number(Dictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) ||
                !(value is int || value is long || value is decimal || value is double)) return null;
            double number = Convert.ToDouble(value);
            return double.IsNaN(number) || double.IsInfinity(number) ? (double?)null : number;
        }
    }
}
