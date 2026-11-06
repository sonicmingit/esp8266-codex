using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace AIClockReceiver
{
    public sealed class Receiver
    {
        private readonly string file;
        private Preferences prefs;
        private readonly HttpClient http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(6), MaxResponseContentBufferSize = 65536 };
        private readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        private readonly DispatcherTimer animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        private readonly Stopwatch motion = Stopwatch.StartNew();
        private readonly PetAnimation defaultClaude = Builtin("claude"), defaultCodex = Builtin("codex");
        private readonly PetLibrary pets;
        private PetAnimation claude, codex;
        private CancellationTokenSource request;
        private bool closed;
        private Snapshot snapshot;
        private string error;
        private Provider selected;
        public Window Window { get; private set; }

        [STAThread]
        public static void Main()
        {
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.SystemDefault;
            var app = new Application();
            var receiver = new Receiver(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIClockReceiver", "settings.json"));
            app.Run(receiver.Window);
        }

        internal static Window LoadWindow(string name)
        {
            Window window;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                window = (Window)XamlReader.Load(stream);
            if (name != "Receiver.xaml")
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DialogTheme.xaml"))
                    window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
            return window;
        }

        private static PetAnimation Builtin(string name)
        {
            var frames = new BitmapSource[6];
            for (int i = 0; i < frames.Length; i++)
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name + "_" + i + ".png"))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                frames[i] = RemoveMatte(image);
            }
            return new PetAnimation(frames, frames, frames);
        }

        // Firmware frames have an opaque black backdrop. Clear only black pixels
        // reachable from the edges, keeping enclosed details such as the eyes.
        private static BitmapSource RemoveMatte(BitmapSource frame)
        {
            var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
            var pixels = new byte[w * h * 4];
            bitmap.CopyPixels(pixels, w * 4, 0);
            var queue = new Queue<int>();
            Action<int> visit = delegate(int index) {
                int i = 4 * index;
                if (pixels[i + 3] == 0 || (pixels[i] | pixels[i + 1] | pixels[i + 2]) != 0) return;
                pixels[i + 3] = 0;
                queue.Enqueue(index);
            };
            for (int x = 0; x < w; x++) { visit(x); visit((h - 1) * w + x); }
            for (int y = 0; y < h; y++) { visit(y * w); visit(y * w + w - 1); }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                if (i % w > 0) visit(i - 1);
                if (i % w < w - 1) visit(i + 1);
                if (i >= w) visit(i - w);
                if (i < w * (h - 1)) visit(i + w);
            }
            var transparent = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            transparent.Freeze();
            return transparent;
        }

        private T Find<T>(string name) where T : FrameworkElement { return (T)Window.FindName(name); }

        public Receiver(string settingsFile)
        {
            file = settingsFile;
            try { prefs = Preferences.Load(file); }
            catch (Exception e) { prefs = new Preferences(); error = "设置读取失败：" + e.Message; }
            pets = new PetLibrary(Path.Combine(Path.GetDirectoryName(file), "pets"));
            claude = pets.Saved(prefs.ClaudePet) ?? defaultClaude;
            codex = pets.Saved(prefs.CodexPet) ?? defaultCodex;
            Window = LoadWindow("Receiver.xaml");
            new EdgeDock(Window, () => prefs, delegate {
                try { prefs.Save(file); } catch (Exception e) { Find<TextBlock>("Footer").ToolTip = e.Message; }
            });
            Find<Button>("Expand").Click += delegate { ToggleCompact(false); };
            Find<Button>("Collapse").Click += delegate { ToggleCompact(true); };
            var strip = Find<Grid>("Strip");
            var menu = new ContextMenu();
            var settingsItem = new MenuItem { Header = "设置…" };
            settingsItem.Click += delegate { EditSettings(); };
            menu.Items.Add(settingsItem);
            var exitItem = new MenuItem { Header = "关闭接收端" };
            exitItem.Click += delegate { Window.Close(); };
            menu.Items.Add(exitItem);
            strip.ContextMenu = menu;
            Find<Button>("Settings").Click += delegate { EditSettings(); };
            Find<Button>("Refresh").Click += async delegate { await Refresh(); };
            Find<Button>("Close").Click += delegate { Window.Close(); };
            Find<Button>("Pin").Click += delegate {
                prefs.Pinned = !prefs.Pinned;
                try { prefs.Save(file); }
                catch (Exception e) { prefs.Pinned = !prefs.Pinned; MessageBox.Show(Window, e.Message, "保存设置失败"); }
                UpdatePin();
            };
            Find<DockPanel>("TitleBar").MouseLeftButtonDown += Drag;
            strip.MouseLeftButtonDown += Drag;
            poll.Tick += async delegate { await Refresh(); };
            animation.Tick += delegate { Tick(); };
            Window.Loaded += async delegate { poll.Start(); animation.Start(); await Refresh(); };
            Window.Closed += delegate {
                closed = true; poll.Stop(); animation.Stop();
                if (request != null) request.Cancel();
                http.Dispose();
                pets.Dispose();
            };
            ApplyLayout();
        }

        private void Drag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) Window.DragMove();
        }

        private void ToggleCompact(bool compact)
        {
            var next = prefs.Copy();
            next.Compact = compact;
            try { next.Save(file); }
            catch (Exception e) { MessageBox.Show(Window, e.Message, "保存设置失败"); return; }
            prefs = next;
            ApplyLayout();
        }

        private void UpdatePin()
        {
            Window.Topmost = prefs.Pinned;
            var pin = Find<Button>("Pin");
            pin.Content = prefs.Pinned ? "\uE77A" : "\uE718";
            pin.ToolTip = prefs.Pinned ? "取消置顶" : "窗口置顶";
            System.Windows.Automation.AutomationProperties.SetName(pin, pin.ToolTip.ToString());
        }

        private void ApplyLayout()
        {
            bool dual = prefs.Dual && !prefs.Compact;
            Window.Width = prefs.Compact ? 364 : dual ? 422 : 358;
            Window.Tag = dual;
            var full = prefs.Compact ? Visibility.Collapsed : Visibility.Visible;
            Find<DockPanel>("TitleBar").Visibility = full;
            Find<DockPanel>("FooterBar").Visibility = full;
            Find<Border>("FooterRule").Visibility = full;
            Find<Grid>("Strip").Visibility = prefs.Compact ? Visibility.Visible : Visibility.Collapsed;
            Find<Border>("TopRule").Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
            ColorToken("Paper", prefs.Compact ? "#202D37" : dual ? "#FAF7F2" : "#171816");
            ColorToken("Ink", dual ? "#252320" : "#F8F6F2");
            ColorToken("Quiet", dual ? "#605B55" : "#C2BAB3");
            ColorToken("Line", dual ? "#DBD6CF" : "#494440");
            ColorToken("Claude", dual ? "#A64319" : "#F58A61");
            ColorToken("Codex", dual ? "#44449C" : "#A1A8FF");
            ColorToken("ResetInk", dual ? "#605B55" : "#F58A61");
            Find<ContentControl>("LeftColumn").Resources["Meter"] = Window.Resources["Claude"];
            Find<ContentControl>("RightColumn").Resources["Meter"] = Window.Resources["Codex"];
            Find<ContentControl>("LeftColumn").Resources["Number"] = Window.Resources["Claude"];
            Find<ContentControl>("RightColumn").Resources["Number"] = Window.Resources["Codex"];
            UpdatePin();
            Draw();
        }

        private void ColorToken(string key, string hex)
        {
            Window.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        private void EditSettings()
        {
            var dialog = LoadWindow("Settings.xaml");
            dialog.Owner = Window;
            var address = (TextBox)dialog.FindName("Address");
            var view = (ComboBox)dialog.FindName("View");
            var source = (ComboBox)dialog.FindName("Source");
            var amount = (ComboBox)dialog.FindName("Amount");
            var message = (TextBlock)dialog.FindName("Error");
            var next = prefs.Copy();
            var nextClaude = claude; var nextCodex = codex;
            var clabel = (TextBlock)dialog.FindName("ClaudePetLabel");
            var xlabel = (TextBlock)dialog.FindName("CodexPetLabel");
            clabel.Text = prefs.ClaudePet ?? "内置耳机宠物";
            xlabel.Text = prefs.CodexPet ?? "内置黄色宠物";
            ((Button)dialog.FindName("ChooseClaude")).Click += delegate {
                var picker = new PetPicker();
                if (!picker.Show(dialog, pets, "Claude", next.ClaudePet, defaultClaude)) return;
                next.ClaudePet = picker.Slug; nextClaude = picker.Animation; clabel.Text = next.ClaudePet ?? "内置耳机宠物";
            };
            ((Button)dialog.FindName("ChooseCodex")).Click += delegate {
                var picker = new PetPicker();
                if (!picker.Show(dialog, pets, "Codex", next.CodexPet, defaultCodex)) return;
                next.CodexPet = picker.Slug; nextCodex = picker.Animation; xlabel.Text = next.CodexPet ?? "内置黄色宠物";
            };
            address.Text = prefs.Address ?? "";
            view.SelectedIndex = prefs.Compact ? 2 : prefs.Dual ? 1 : 0;
            source.SelectedIndex = prefs.Source;
            amount.SelectedIndex = prefs.Remaining ? 1 : 0;
            source.IsEnabled = view.SelectedIndex != 1;
            view.SelectionChanged += delegate { source.IsEnabled = view.SelectedIndex != 1; };
            ((Button)dialog.FindName("Save")).Click += async delegate {
                next.Address = address.Text.Trim();
                next.Compact = view.SelectedIndex == 2;
                if (!next.Compact) next.Dual = view.SelectedIndex == 1;
                next.Source = source.SelectedIndex;
                next.Remaining = amount.SelectedIndex == 1;
                bool changed;
                try {
                    changed = !Equals(Preferences.Endpoint(next.Address), Preferences.Endpoint(prefs.Address));
                    next.Save(file);
                }
                catch (Exception e) { message.Text = e.Message; return; }
                prefs = next;
                claude = nextClaude; codex = nextCodex;
                if (changed) {
                    if (request != null) { request.Cancel(); request = null; }
                    snapshot = null; selected = null; error = null;
                }
                ApplyLayout();
                dialog.DialogResult = true;
                if (changed) await Refresh();
            };
            dialog.ShowDialog();
        }

        private async Task Refresh()
        {
            var uri = Preferences.Endpoint(prefs.Address);
            if (closed || request != null || uri == null) return;
            using (var current = new CancellationTokenSource())
            {
                request = current;
                Draw();
                try
                {
                    using (var response = await http.GetAsync(uri, current.Token))
                    {
                        response.EnsureSuccessStatusCode();
                        var data = Snapshot.Parse(await response.Content.ReadAsStringAsync());
                        if (closed || request != current) return;
                        snapshot = data;
                        error = null;
                    }
                }
                catch (Exception e)
                {
                    if (closed || request != current) return;
                    error = e is OperationCanceledException ? "连接超时" : e.GetBaseException().Message;
                }
                finally
                {
                    if (request == current) { request = null; if (!closed) Draw(); }
                }
            }
        }

        private Provider Active()
        {
            var a = snapshot.Claude; var b = snapshot.Codex;
            if (prefs.Source == 1) return a;
            if (prefs.Source == 2) return b;
            if (!b.Present || (a.Waiting && !b.Waiting)) return a;
            if (!a.Present || (b.Waiting && !a.Waiting)) return b;
            if ((a.State == "working") != (b.State == "working")) return a.State == "working" ? a : b;
            long interval = a.State == "working" ? 2 : 6;
            return Environment.TickCount / (interval * 1000) % 2 == 0 ? a : b;
        }

        private void Tick()
        {
            if (snapshot == null) return;
            double age = snapshot.Age.Elapsed.TotalSeconds;
            bool stale = error != null || age > 30;
            double phase = SystemParameters.ClientAreaAnimation ? motion.Elapsed.TotalSeconds : 0;
            snapshot.Claude.Tick(stale, age, phase, claude, prefs.Remaining);
            snapshot.Codex.Tick(stale, age, phase, codex, prefs.Remaining);
            if (!prefs.Dual || prefs.Compact)
            {
                var next = Active();
                if (next != selected)
                {
                    selected = next;
                    Find<Grid>("Single").DataContext = next;
                    Find<Grid>("Strip").DataContext = next;
                    Find<TextBlock>("StripHint").Text = next.Windows.Count == 0 ? "暂无额度数据" : "";
                    Find<TextBlock>("Activity").DataContext = next;
                    var heading = Find<TextBlock>("Heading");
                    heading.Text = next.Name;
                    heading.SetResourceReference(TextBlock.ForegroundProperty, next.Name);
                    Find<Grid>("Single").Resources["ResetInk"] = Window.Resources[next.Name];
                }
            }
            if (stale) Find<TextBlock>("Footer").Text = "缓存数据   " + prefs.Address;
        }

        private void Draw()
        {
            bool hasData = snapshot != null;
            Find<Grid>("Single").Visibility = hasData && !prefs.Dual && !prefs.Compact ? Visibility.Visible : Visibility.Collapsed;
            Find<Grid>("Dual").Visibility = hasData && prefs.Dual && !prefs.Compact ? Visibility.Visible : Visibility.Collapsed;
            Find<StackPanel>("Empty").Visibility = hasData || prefs.Compact ? Visibility.Collapsed : Visibility.Visible;
            Find<TextBlock>("Activity").Visibility = hasData && !prefs.Dual ? Visibility.Visible : Visibility.Collapsed;
            if (!hasData || (prefs.Dual && !prefs.Compact))
            {
                Find<TextBlock>("Heading").Text = "AI 额度";
                Find<TextBlock>("Heading").SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                selected = null;
            }
            if (!hasData)
            {
                Find<Grid>("Strip").DataContext = null;
                Find<TextBlock>("StripHint").Text = error != null ? "连接失败" : request != null ? "正在连接…" : "展开以设置桥接";
            }
            Find<ContentControl>("LeftColumn").Content = hasData ? snapshot.Claude : null;
            Find<ContentControl>("RightColumn").Content = hasData ? snapshot.Codex : null;
            string address = prefs.Address;
            var footer = Find<TextBlock>("Footer");
            footer.Text = String.IsNullOrEmpty(address) ? "尚未配置桥接" :
                hasData && prefs.Dual && error == null ? "桥接已连接   " + address : address;
            footer.ToolTip = error ?? footer.Text;
            Find<TextBlock>("EmptyTitle").Text = error != null ? "无法连接桥接" :
                request != null ? "正在连接…" : "连接你的桥接服务";
            Find<TextBlock>("EmptyHint").Text = error ?? (String.IsNullOrEmpty(address) ? "在设置中填写发射端地址" : address);
            Find<Button>("Refresh").IsEnabled = request == null && !String.IsNullOrEmpty(address);
            Tick();
        }
    }
}
