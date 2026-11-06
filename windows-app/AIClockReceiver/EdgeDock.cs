using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Screen = System.Windows.Forms.Screen;

namespace AIClockReceiver
{
    internal sealed class EdgeDock
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Bounds { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Bounds rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);
        private readonly Window window;
        private readonly Func<Preferences> settings;
        private readonly Action save;
        private IntPtr handle;
        private bool moving, queued;
        public EdgeDock(Window window, Func<Preferences> settings, Action save)
        {
            this.window = window; this.settings = settings; this.save = save;
            window.SourceInitialized += delegate {
                handle = new WindowInteropHelper(window).Handle;
                HwndSource.FromHwnd(handle).AddHook(Message);
            };
            window.ContentRendered += delegate { Align(); };
            window.SizeChanged += delegate {
                if (!moving && !queued && settings().DockSide != 0) {
                    queued = true;
                    window.Dispatcher.BeginInvoke(new Action(delegate { queued = false; Align(); }), DispatcherPriority.Loaded);
                }
            };
        }
        private IntPtr Message(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
        {
            if (msg == 0x0216) // WM_MOVING provides the proposed rectangle in desktop coordinates.
            {
                moving = true;
                var rect = (Bounds)Marshal.PtrToStructure(lp, typeof(Bounds));
                var screen = Screen.FromRectangle(System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
                var area = screen.WorkingArea;
                int threshold = (int)Math.Ceiling(16 * HwndSource.FromHwnd(hwnd).CompositionTarget.TransformToDevice.M11);
                int delta = 0;
                if (Math.Abs(rect.Left - area.Left) <= threshold) delta = area.Left - rect.Left;
                else if (Math.Abs(rect.Right - area.Right) <= threshold) delta = area.Right - rect.Right;
                else return IntPtr.Zero;
                rect.Left += delta; rect.Right += delta;
                Marshal.StructureToPtr(rect, lp, false);
                handled = true;
                return new IntPtr(1);
            }
            if (msg == 0x0232 && moving) // WM_EXITSIZEMOVE
            {
                moving = false;
                Bounds rect;
                if (!GetWindowRect(hwnd, out rect)) return IntPtr.Zero;
                var screen = Screen.FromHandle(hwnd); var area = screen.WorkingArea;
                var p = settings();
                p.DockSide = Math.Abs(rect.Left - area.Left) <= 1 ? -1 : Math.Abs(rect.Right - area.Right) <= 1 ? 1 : 0;
                p.DockMonitor = screen.DeviceName;
                p.DockFraction = Math.Max(0, Math.Min(1, (double)(rect.Top - area.Top) / Math.Max(1, area.Height - (rect.Bottom - rect.Top))));
                save();
            }
            return IntPtr.Zero;
        }
        private void Align()
        {
            var p = settings();
            Bounds rect;
            if (moving || (p.DockSide != -1 && p.DockSide != 1) || !GetWindowRect(handle, out rect)) return;
            var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == p.DockMonitor) ?? Screen.FromHandle(handle);
            var area = screen.WorkingArea;
            int x = p.DockSide < 0 ? area.Left : area.Right - (rect.Right - rect.Left);
            int y = area.Top + (int)(Math.Max(0, Math.Min(1, p.DockFraction)) * Math.Max(0, area.Height - (rect.Bottom - rect.Top)));
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0015); // no size, z-order or activation change
        }
    }
}
