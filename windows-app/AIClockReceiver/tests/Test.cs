using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using AIClockReceiver;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

class Test
{
    const string Sample = "{\"ts\":1800000000,\"claude\":{\"status\":\"working\",\"five_hour_pct\":28,\"five_hour_reset_at\":1800008400,\"seven_day_pct\":61,\"seven_day_reset_at\":1800226800},\"codex\":{\"status\":\"idle\",\"primary_window_min\":10080,\"primary_pct\":7,\"primary_reset_at\":1800518400}}";
    static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception(text);
        Console.WriteLine("PASS " + text);
    }
    static void Click(Window w, string name)
    {
        ((Button)w.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    [StructLayout(LayoutKind.Sequential)]
    struct RectI { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RectI r);
    static void DragTo(Window w, bool right, string file)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        var screen = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
        RectI r; GetWindowRect(hwnd, out r);
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        r.Left = right ? screen.Right - width - 6 : screen.Left + 6;
        r.Right = r.Left + width; r.Top = screen.Top + 16; r.Bottom = r.Top + height;
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(r));
        try {
            Marshal.StructureToPtr(r, ptr, false);
            SendMessage(hwnd, 0x216, IntPtr.Zero, ptr);
            r = (RectI)Marshal.PtrToStructure(ptr, typeof(RectI));
            Check(right ? r.Right == screen.Right : r.Left == screen.Left, right ? "right edge snap" : "left edge snap");
            SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, 0, 0, 0x0015);
            SendMessage(hwnd, 0x232, IntPtr.Zero, IntPtr.Zero);
            Check(Preferences.Load(file).DockSide == (right ? 1 : -1), "remember docked edge");
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    static async Task Settings(Window w, Action<Window> action)
    {
        var edit = w.Dispatcher.BeginInvoke(new Action(delegate {
            var dialog = w.OwnedWindows.Cast<Window>().Single();
            try { action(dialog); Click(dialog, "Save"); }
            catch { dialog.Close(); throw; }
        }), DispatcherPriority.ApplicationIdle);
        Click(w, "Settings"); await edit.Task;
    }
    static void Capture(Window w, string path)
    {
        w.UpdateLayout();
        var root = (FrameworkElement)w.Content;
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * 2),
            (int)Math.Ceiling(root.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        // The window background lies outside Content; paint it into the capture.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(root.RenderSize);
            dc.DrawRectangle(w.Background, null, rect);
            var brush = new VisualBrush(root) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = rect };
            dc.DrawRectangle(brush, null, rect);
        }
        image.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }

    [STAThread]
    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static int Run(string[] args)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.SystemDefault;
        var data = Snapshot.Parse(Sample);
        Check(data.Codex.Windows.Count == 1 && data.Codex.Windows[0].Label == "周额度已用", "legacy weekly window");
        Check(Snapshot.Parse("{\"codex\":{\"primary_pct\":0}}").Codex.Windows[0].Percent == "0%", "real zero");
        Check(Preferences.Endpoint("localhost").Port == 8765 && Preferences.Endpoint("localhost:80").Port == 80 &&
              Preferences.Endpoint("https://example.test/ai/").AbsolutePath == "/ai/status", "bridge addresses");
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        int port = ((IPEndPoint)server.LocalEndpoint).Port;
        var received = Task.Run(async delegate {
            using (var socket = await server.AcceptTcpClientAsync())
            using (var stream = socket.GetStream())
            {
                var reader = new StreamReader(stream);
                var line = await reader.ReadLineAsync();
                while (!String.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                var bytes = Encoding.UTF8.GetBytes(Sample);
                var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: " + bytes.Length + "\r\n\r\n");
                await stream.WriteAsync(header, 0, header.Length);
                await stream.WriteAsync(bytes, 0, bytes.Length);
                return line;
            }
        });
        var prefs = new Preferences { Address = "127.0.0.1:" + port, Source = 1 };
        prefs.Save(args[0]);
        Directory.CreateDirectory(args[1]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var w = new Receiver(args[0]).Window;
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -2000;
        var failure = (Exception)null;
        w.ContentRendered += async delegate {
            try {
                var single = (Grid)w.FindName("Single");
                for (int i = 0; single.Visibility != Visibility.Visible && i < 100; i++) await Task.Delay(50);
                Check(single.Visibility == Visibility.Visible && ((Provider)single.DataContext).Activity == "工作中", "live single view");
                Check(await received == "GET /status?client=watch HTTP/1.1", "read-only status request");
                server.Stop();
                Capture(w, Path.Combine(args[1], "single.png"));
                foreach (var dual in new[] { true, false, true })
                {
                    var edit = w.Dispatcher.BeginInvoke(new Action(delegate {
                        var dialog = w.OwnedWindows.Cast<Window>().Single();
                        ((ComboBox)dialog.FindName("View")).SelectedIndex = dual ? 1 : 0;
                        Click(dialog, "Save");
                    }), DispatcherPriority.ApplicationIdle);
                    Click(w, "Settings");
                    await edit.Task;
                    Check(Preferences.Load(args[0]).Dual == dual && (((Grid)w.FindName("Dual")).Visibility == Visibility.Visible) == dual, "switch view and persist");
                }
                Capture(w, Path.Combine(args[1], "dual.png"));
                var idle = (Provider)((ContentControl)w.FindName("RightColumn")).Content;
                var firstFrame = idle.Pet;
                if (SystemParameters.ClientAreaAnimation) {
                    for (int i = 0; idle.Pet == firstFrame && i < 15; i++) await Task.Delay(100);
                    Check(idle.Activity == "空闲" && idle.Pet != firstFrame, "idle pet animates");
                }
                await Settings(w, delegate(Window dialog) {
                    Capture(dialog, Path.Combine(args[1], "settings.png"));
                    ((ComboBox)dialog.FindName("View")).SelectedIndex = 2;
                    ((ComboBox)dialog.FindName("Amount")).SelectedIndex = 1;
                });
                await w.Dispatcher.InvokeAsync(delegate {}, DispatcherPriority.ApplicationIdle);
                var strip = (Grid)w.FindName("Strip");
                var usage = ((Provider)strip.DataContext).Windows[0];
                Console.WriteLine("Strip height {0}, value {1}, label {2}", w.ActualHeight, usage.DisplayValue, usage.Label);
                Check(strip.IsVisible && w.ActualHeight < 85 && usage.Label == "5 小时剩余" && usage.DisplayValue == 72 &&
                    usage.Percent == "72%", "compact view with remaining amount");
                Capture(w, Path.Combine(args[1], "strip.png"));
                DragTo(w, false, args[0]); DragTo(w, true, args[0]);
                Click(w, "Expand"); await Task.Delay(150);
                RectI aligned; GetWindowRect(new WindowInteropHelper(w).Handle, out aligned);
                Check(((Grid)w.FindName("Dual")).IsVisible && aligned.Right == System.Windows.Forms.Screen.PrimaryScreen.WorkingArea.Right, "expand into last layout and keep right edge");
                Click(w, "Collapse");
                Check(Preferences.Load(args[0]).Compact && Preferences.Load(args[0]).Remaining, "persist strip and amount");
                Click(w, "Expand");
                Click(w, "Pin");
                Check(w.Topmost && Preferences.Load(args[0]).Pinned, "pin and persist");
                Click(w, "Refresh");
                var footer = (TextBlock)w.FindName("Footer");
                for (int i = 0; !footer.Text.StartsWith("缓存数据") && i < 160; i++) await Task.Delay(50);
                Check(footer.Text.StartsWith("缓存数据") && ((Provider)((ContentControl)w.FindName("LeftColumn")).Content).Windows[0].Used == 28, "cached snapshot after disconnect");
                if (args.Length > 2 && args[2] == "online") await OnlinePet(w, args[0], args[1]);
                var reopened = new Receiver(args[0]).Window;
                Check(Math.Abs(reopened.Width - w.Width) < 2 && reopened.Topmost, "restart with saved layout");
                reopened.Close();
            }
            catch (Exception e) { failure = e; }
            finally { w.Close(); app.Shutdown(); }
        };
        app.Run(w);
        server.Stop();
        if (failure != null) throw failure;
        return 0;
    }
    static async Task OnlinePet(Window w, string file, string output)
    {
        var edit = w.Dispatcher.InvokeAsync(new Action(delegate {
            var settings = w.OwnedWindows.Cast<Window>().Single();
            var pick = w.Dispatcher.InvokeAsync(new Func<Task>(async delegate {
                var dialog = settings.OwnedWindows.Cast<Window>().Single();
                try {
                    var list = (ListBox)dialog.FindName("List");
                    var button = (Button)dialog.FindName("Choose");
                    var info = (TextBlock)dialog.FindName("Info");
                    for (int i = 0; list.Items.Count == 0 && !info.Text.Contains("失败") && i < 260; i++) await Task.Delay(100);
                    ((TextBox)dialog.FindName("Search")).Text = "homelander";
                    Check(list.Items.Count > 0, "live Petdex catalog search: " + ((TextBlock)dialog.FindName("Info")).Text);
                    list.SelectedIndex = 0;
                    for (int i = 0; !button.IsEnabled && i < 260; i++) await Task.Delay(100);
                    Check(button.IsEnabled && ((Image)dialog.FindName("Preview")).Source != null, "live Petdex decoded preview: " + ((TextBlock)dialog.FindName("Info")).Text);
                    var image = (Image)dialog.FindName("Preview");
                    var actions = (ComboBox)dialog.FindName("Action");
                    if (SystemParameters.ClientAreaAnimation) {
                        foreach (int action in new[] { 0, 1, 2 }) {
                            actions.SelectedIndex = action; await Task.Delay(150);
                            var frame = image.Source;
                            for (int i = 0; image.Source == frame && i < 15; i++) await Task.Delay(100);
                            Check(image.Source != frame, "Petdex animation advances in state " + action);
                        }
                    }
                    Capture(dialog, Path.Combine(output, "pet-picker.png"));
                    Click(dialog, "Choose");
                }
                finally { if (dialog.IsVisible) dialog.Close(); }
            }), DispatcherPriority.ApplicationIdle);
            try {
                Click(settings, "ChooseClaude");
                pick.Task.Unwrap().GetAwaiter().GetResult();
                Click(settings, "Save");
            }
            finally { if (settings.IsVisible) settings.Close(); }
        }), DispatcherPriority.ApplicationIdle);
        Click(w, "Settings"); await edit.Task;
        Check(Preferences.Load(file).ClaudePet != null && Preferences.Load(file).CodexPet == null, "pet assignment per source");
    }
}
