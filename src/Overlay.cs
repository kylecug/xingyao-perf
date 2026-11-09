using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace XingyaoPerf
{
    // A small per-pixel-alpha window on the taskbar, just left of the tray.
    // Windows 11 has no taskbar widget API. A topmost window gets covered when Start opens, because the shell
    // raises the taskbar above the topmost band, so this becomes a child of the taskbar window.
    // If that fails it stays a floating topmost window.
    //
    //   CPU | 45% | P 4.2 | E 3.8 | L 2.1 | 39.9 W |       | 6506 rpm
    //   GPU | 80% |     2.10 GHz          | 22.4 W | 69 °C | 6282 rpm
    //
    // Usage, frequency and power line up in columns. CPU and GPU share one package, so the
    // temperature spans both rows. The two fans are stacked but not tied to a row,
    // because which fan cools which part is not known.
    // This uses NativeWindow rather than Form, because Form resets the parent of its window after creating it.
    internal sealed class Overlay : NativeWindow, IDisposable
    {
        const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, WM_RBUTTONUP = 0x205;

        // Layout in pixels for a 60 px tall taskbar, scaled to the real height.
        const float RowH = 26, RowGap = 3, Radius = 3, LabelW = 40, PctW = 60, FreqW = 68, InnerGap = 3, PowerW = 76, TempW = 66, FanW = 92, ColGap = 8;

        // Bars are full at these values. Frequency scales are for display only.
        const double CpuGhzMax = 5.0, GpuGhzMax = 2.5, PkgWMax = 60, GpuWMax = 40, TempMin = 30, TempMax = 100, FanMax = 7500;

        static readonly Color Normal = Color.FromArgb(46, 125, 50), Warn = Color.FromArgb(190, 120, 0), Hot = Color.FromArgb(198, 40, 40), Freq = Color.FromArgb(30, 100, 180);
        static readonly Color TrackDark = Color.FromArgb(48, 255, 255, 255), TrackLight = Color.FromArgb(170, 30, 30, 30);

        Snapshot values = new Snapshot();
        int lastH, lastX, lastY, width;
        bool lastLight;
        Font font;
        string drawn;
        IntPtr embeddedIn;
        bool embedFailed, shown;

        public event Action RightClick;
        public bool Visible { get { return shown; } }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            if (m.Msg == WM_RBUTTONUP) { var h = RightClick; if (h != null) h(); return; }
            base.WndProc(ref m);
        }

        public void Hide()
        {
            if (Handle != IntPtr.Zero) Native.ShowWindow(Handle, Native.SW_HIDE);
            shown = false;
        }

        static CreateParams ParamsFor(IntPtr parent)
        {
            var c = new CreateParams { Caption = "xingyao-perf", Width = 1, Height = 1 };
            c.ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (parent != IntPtr.Zero) { c.Parent = parent; c.Style = Native.WS_CHILD; }
            else { c.Style = Native.WS_POPUP; c.ExStyle |= WS_EX_TOPMOST; }
            return c;
        }

        // Runs on first use and after an Explorer restart, which destroys this child window along with the taskbar.
        void Create(IntPtr bar)
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
            shown = false;
            drawn = null;
            embeddedIn = IntPtr.Zero;
            if (!embedFailed)
            {
                string problem;
                try
                {
                    CreateHandle(ParamsFor(bar));
                    if (Native.GetParent(Handle) == bar) { embeddedIn = bar; return; }
                    problem = "parent not set";
                }
                catch (Exception e) { problem = e.Message; }
                Log.Write("taskbar embed failed (" + problem + "), using a floating window");
                embedFailed = true;
                if (Handle != IntPtr.Zero) DestroyHandle();
            }
            CreateHandle(ParamsFor(IntPtr.Zero));
        }

        public static string Num(double v, string fmt) { return double.IsNaN(v) ? "--" : v.ToString(fmt, CultureInfo.InvariantCulture); }

        public void SetValues(Snapshot s) { values = s; Redraw(); }

        static string Key(Snapshot s)
        {
            return Num(s.CpuPct, "0") + "|" + Num(s.GpuPct, "0") + "|" + Num(s.PGhz, "0.0") + Num(s.EGhz, "0.0") + Num(s.LGhz, "0.0") + "|" + Num(s.PkgW, "0.0") + "|" + Num(s.TempC, "0") + "|" + Num(s.GpuMhz, "0") + "|" + Num(s.GpuW, "0.0") + "|" + s.Fan0 + "|" + s.Fan1;
        }

        public static int WidthFor(int h)
        {
            float k = h / 60f;
            return (int)Math.Ceiling(k * (LabelW + PctW + ColGap + FreqW * 3 + InnerGap * 2 + ColGap + PowerW + ColGap + TempW + ColGap + FanW)) + 2;
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
        public void Track_()
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
                font = MakeFont(h);
                width = WidthFor(h);
                lastH = h; lastLight = light; drawn = null;
            }
            if (Handle == IntPtr.Zero || (!embedFailed && embeddedIn != bar)) Create(bar);
            if (!shown) { Native.ShowWindow(Handle, Native.SW_SHOWNA); shown = true; drawn = null; }

            int x = right - width - 8, y = r.Top;
            if (embeddedIn != IntPtr.Zero)
            {
                x -= r.Left; y -= r.Top; // Child positions are relative to the taskbar.
                if (x != lastX || y != lastY || drawn == null)
                    Native.SetWindowPos(Handle, IntPtr.Zero, x, y, width, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            }
            if (x != lastX || y != lastY) { lastX = x; lastY = y; drawn = null; }
            Redraw();
            if (embeddedIn == IntPtr.Zero)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public static Font MakeFont(int h) { return new Font("Segoe UI", Math.Max(9f, h * 0.27f), FontStyle.Regular, GraphicsUnit.Pixel); }

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

        static Color Level(double v, double warn, double hot) { return v >= hot ? Hot : v >= warn ? Warn : Normal; }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [ThreadStatic] static Color currentTrack;

        static void Bar(Graphics g, Font f, StringFormat center, RectangleF r, float rad, double fraction, Color color, string text)
        {
            using (var path = Rounded(r, rad))
            using (var track = new SolidBrush(currentTrack))
            {
                g.FillPath(track, path);
                if (!double.IsNaN(fraction) && fraction > 0)
                {
                    float w = r.Width * (float)Math.Min(1.0, fraction);
                    var state = g.Save();
                    g.SetClip(path);
                    using (var fill = new SolidBrush(color)) g.FillRectangle(fill, r.Left, r.Top, w, r.Height);
                    g.Restore(state);
                }
                g.DrawString(text, f, Brushes.White, r, center);
            }
        }

        public Bitmap Render(int w, int h, Font f, bool light)
        {
            var s = values;
            currentTrack = light ? TrackLight : TrackDark;
            float k = h / 60f;
            float rowH = RowH * k, gap = RowGap * k, rad = Radius * k, inner = InnerGap * k, col = ColGap * k;
            float y1 = (h - rowH * 2 - gap) / 2, y2 = y1 + rowH + gap;
            float freqW = FreqW * k, freqAll = freqW * 3 + inner * 2;
            float xPct = LabelW * k, xFreq = xPct + (PctW + ColGap) * k, xPower = xFreq + freqAll + col, xTemp = xPower + PowerW * k + col, xFan = xTemp + TempW * k + col;

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var label = new SolidBrush(light ? Color.FromArgb(40, 40, 40) : Color.FromArgb(225, 225, 225)))
            using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            using (var left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            {
                // Alpha 1 is invisible but keeps the whole area clickable for the right-click menu.
                g.Clear(Color.FromArgb(1, 0, 0, 0));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                g.DrawString("CPU", f, label, new RectangleF(0, y1, xPct, rowH), left);
                g.DrawString("GPU", f, label, new RectangleF(0, y2, xPct, rowH), left);

                Bar(g, f, center, new RectangleF(xPct, y1, PctW * k, rowH), rad, s.CpuPct / 100, Level(s.CpuPct, 70, 90), Num(s.CpuPct, "0") + "%");
                Bar(g, f, center, new RectangleF(xPct, y2, PctW * k, rowH), rad, s.GpuPct / 100, Level(s.GpuPct, 70, 90), Num(s.GpuPct, "0") + "%");

                Bar(g, f, center, new RectangleF(xFreq, y1, freqW, rowH), rad, s.PGhz / CpuGhzMax, Freq, "P " + Num(s.PGhz, "0.0"));
                Bar(g, f, center, new RectangleF(xFreq + freqW + inner, y1, freqW, rowH), rad, s.EGhz / CpuGhzMax, Freq, "E " + Num(s.EGhz, "0.0"));
                Bar(g, f, center, new RectangleF(xFreq + (freqW + inner) * 2, y1, freqW, rowH), rad, s.LGhz / CpuGhzMax, Freq, "L " + Num(s.LGhz, "0.0"));
                Bar(g, f, center, new RectangleF(xFreq, y2, freqAll, rowH), rad, s.GpuMhz / 1000 / GpuGhzMax, Freq, Num(s.GpuMhz / 1000, "0.00") + " GHz");

                Bar(g, f, center, new RectangleF(xPower, y1, PowerW * k, rowH), rad, s.PkgW / PkgWMax, Level(s.PkgW, 40, 50), Num(s.PkgW, "0.0") + " W");
                Bar(g, f, center, new RectangleF(xPower, y2, PowerW * k, rowH), rad, s.GpuW / GpuWMax, Level(s.GpuW, 30, 38), Num(s.GpuW, "0.0") + " W");

                Bar(g, f, center, new RectangleF(xTemp, y1, TempW * k, rowH * 2 + gap), rad, (s.TempC - TempMin) / (TempMax - TempMin), Level(s.TempC, 80, 90), Num(s.TempC, "0") + " °C");

                Bar(g, f, center, new RectangleF(xFan, y1, FanW * k, rowH), rad, s.Fan0 < 0 ? double.NaN : s.Fan0 / FanMax, Level(s.Fan0, 5000, 6500), (s.Fan0 < 0 ? "--" : s.Fan0.ToString()) + " rpm");
                Bar(g, f, center, new RectangleF(xFan, y2, FanW * k, rowH), rad, s.Fan1 < 0 ? double.NaN : s.Fan1 / FanMax, Level(s.Fan1, 5000, 6500), (s.Fan1 < 0 ? "--" : s.Fan1.ToString()) + " rpm");
            }
            return bmp;
        }

        void Redraw()
        {
            if (font == null || Handle == IntPtr.Zero || !shown) return;
            string key = Key(values);
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
                    if (embeddedIn != IntPtr.Zero) Native.UpdateLayeredChild(Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, 2);
                    else Native.UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2);
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

        public void Dispose()
        {
            if (font != null) font.Dispose();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    // Three rising bars, like a signal meter. The number lit and the color show the mode.
    internal static class ModeIcon
    {
        static readonly Color[] Colors = { Color.Gray, Color.FromArgb(76, 175, 80), Color.FromArgb(66, 165, 245), Color.FromArgb(239, 83, 80) };

        public static Bitmap Draw(int mode, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var on = new SolidBrush(Colors[mode]))
            using (var off = new SolidBrush(Color.FromArgb(110, 150, 150, 150)))
            {
                g.Clear(Color.Transparent);
                int barW = Math.Max(2, (int)Math.Round(size * 0.24)), barGap = Math.Max(1, (int)Math.Round(size * 0.11));
                int x = (size - barW * 3 - barGap * 2) / 2;
                double[] heights = { 0.42, 0.70, 1.0 };
                int bottom = size - Math.Max(1, size / 16), maxH = bottom - Math.Max(1, size / 16);
                for (int i = 0; i < 3; i++)
                {
                    int bh = (int)Math.Round(maxH * heights[i]);
                    g.FillRectangle(i < mode ? on : off, x + i * (barW + barGap), bottom - bh, barW, bh);
                }
            }
            return bmp;
        }
    }
}
