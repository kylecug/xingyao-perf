using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace XingyaoPerf
{
    // A small per-pixel-alpha window that sits on the taskbar, just left of the tray.
    // Windows 11 has no taskbar widget API, so this floats above the taskbar rather than inside it.
    internal sealed class Overlay : Form
    {
        const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        const string Template1 = "P 8.8  E 8.8  L 8.8 GHz   88.8 W  88 °C";
        const string Template2 = "GPU 8888 MHz  88.8 W   8888/8888 RPM";

        string line1 = "", line2 = "";
        int lastH, lastX, lastY, width;
        bool lastLight;
        Font font;
        string drawn;

        public Overlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "xingyao-perf";
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var c = base.CreateParams;
                c.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                return c;
            }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        public static string Num(double v, string fmt) { return double.IsNaN(v) ? "--" : v.ToString(fmt, CultureInfo.InvariantCulture); }

        public void SetValues(Snapshot s)
        {
            line1 = "P " + Num(s.PGhz, "0.0") + "  E " + Num(s.EGhz, "0.0") + "  L " + Num(s.LGhz, "0.0") + " GHz   " + Num(s.PkgW, "0.0") + " W  " + Num(s.TempC, "0") + " °C";
            line2 = "GPU " + Num(s.GpuMhz, "0") + " MHz  " + Num(s.GpuW, "0.0") + " W   " + (s.Fan0 < 0 ? "--" : s.Fan0.ToString()) + "/" + (s.Fan1 < 0 ? "--" : s.Fan1.ToString()) + " RPM";
            Redraw();
        }

        static bool LightTaskbar()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                var v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                return v is int && (int)v == 1;
            }
        }

        // Called on a timer: follows the taskbar, stays above it, and hides for fullscreen apps.
        public void Track()
        {
            IntPtr bar = Native.FindWindow("Shell_TrayWnd", null);
            Native.RECT r, tray;
            if (bar == IntPtr.Zero || !Native.GetWindowRect(bar, out r) || ForegroundIsFullscreen() || !TaskbarOnScreen(r))
            {
                if (Visible) Hide();
                return;
            }
            IntPtr notify = Native.FindWindowEx(bar, IntPtr.Zero, "TrayNotifyWnd", null);
            int right = notify != IntPtr.Zero && Native.GetWindowRect(notify, out tray) ? tray.Left : r.Right;
            int h = r.Bottom - r.Top;
            bool light = LightTaskbar();
            if (h != lastH || light != lastLight)
            {
                if (font != null) font.Dispose();
                font = new Font("Segoe UI", Math.Max(9f, h * 0.30f), FontStyle.Regular, GraphicsUnit.Pixel);
                using (var bmp = new Bitmap(1, 1))
                using (var g = Graphics.FromImage(bmp))
                    width = (int)Math.Ceiling(Math.Max(g.MeasureString(Template1, font).Width, g.MeasureString(Template2, font).Width)) + 8;
                lastH = h; lastLight = light; drawn = null;
            }
            int x = right - width - 8, y = r.Top;
            if (x != lastX || y != lastY) { lastX = x; lastY = y; drawn = null; }
            if (!Visible) { Show(); drawn = null; }
            Redraw();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        static bool TaskbarOnScreen(Native.RECT r)
        {
            var b = Screen.PrimaryScreen.Bounds;
            return r.Top < b.Bottom - 4 && r.Bottom > b.Top + 4;
        }

        static bool ForegroundIsFullscreen()
        {
            IntPtr f = Native.GetForegroundWindow();
            Native.RECT r;
            if (f == IntPtr.Zero || !Native.GetWindowRect(f, out r)) return false;
            var sb = new StringBuilder(64);
            Native.GetClassName(f, sb, 64);
            string c = sb.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "XamlExplorerHostIslandWindow") return false;
            var b = Screen.FromHandle(f).Bounds;
            return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
        }

        public Bitmap Render(int w, int h, Font f, bool light)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var brush = new SolidBrush(light ? Color.FromArgb(20, 20, 20) : Color.FromArgb(245, 245, 245)))
            {
                // Alpha 1 is invisible but keeps the whole area clickable for the right-click menu.
                g.Clear(Color.FromArgb(1, 0, 0, 0));
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                float rowH = f.GetHeight(g);
                float top = Math.Max(0, (h - rowH * 2) / 2);
                g.DrawString(line1, f, brush, 2, top);
                g.DrawString(line2, f, brush, 2, top + rowH);
            }
            return bmp;
        }

        void Redraw()
        {
            if (font == null || !IsHandleCreated || !Visible) return;
            string key = line1 + "|" + line2;
            if (key == drawn) return;
            drawn = key;
            using (var bmp = Render(width, lastH, font, lastLight))
            {
                IntPtr screen = Native.GetDC(IntPtr.Zero), mem = Native.CreateCompatibleDC(screen);
                IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = Native.SelectObject(mem, hbmp);
                try
                {
                    var pos = new Native.POINT(lastX, lastY);
                    var size = new Native.SIZE(width, lastH);
                    var src = new Native.POINT(0, 0);
                    var blend = new Native.BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
                    Native.UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2);
                }
                finally
                {
                    Native.SelectObject(mem, old);
                    Native.DeleteObject(hbmp);
                    Native.DeleteDC(mem);
                    Native.ReleaseDC(IntPtr.Zero, screen);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && font != null) font.Dispose();
            base.Dispose(disposing);
        }
    }
}
