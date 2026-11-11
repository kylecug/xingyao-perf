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
    // Usage and power boxes are Task Manager style history graphs: CPU and GPU usage behind the percentages,
    // physical memory in use behind the package power, network receive (solid) and send (dashed) behind
    // the GPU power. On battery the GPU power is replaced by the whole laptop's power, marked with a sigma.
    // Temperature and fan speeds are history graphs as well; the temperature line takes the colour of its level.
    //
    // The compact layout has no labels. Like Task Manager, the first two columns are history graphs:
    // CPU and GPU usage behind their frequencies (CPU as Task Manager's "Speed"), physical memory in use
    // behind the package power, and network receive (solid) and send (dashed) behind the GPU power.
    // The last column is the temperature above the mean of the two fans:
    //
    //   [cpu graph  3.1 GHz ] | [memory  39.9 W] | [temperature 69 °C]
    //   [gpu graph  2.10 GHz] | [network 22.4 W] | [fan mean  4650 rpm]
    //
    // Usage, frequency and power line up in columns. CPU and GPU share one package, so the
    // temperature spans both rows. The two fans are stacked but not tied to a row,
    // because which fan cools which part is not known.
    // This uses NativeWindow rather than Form, because Form resets the parent of its window after creating it.
    internal sealed class Overlay : NativeWindow, IDisposable
    {
        const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205;

        // Layout in pixels for a 60 px tall taskbar, scaled to the real height.
        const float RowH = 26, RowGap = 3, Radius = 3, LabelW = 30, PctW = 40, FreqW = 45, InnerGap = 2, PowerW = 51, TempW = 44, FanW = 63, ColGap = 5;

        const float CompactUseW = 72, CompactRightW = 72;

        // Room for the Windows weather button, which sits beside the tray when taskbar icons are left-aligned.
        // Used only when the probe cannot read the button's real position.
        const float WeatherW = 200;
        public bool AvoidWeather;

        public const int LayoutAuto = 0, LayoutFull = 1, LayoutCompact = 2;
        public int Layout;
        public TaskbarProbe Probe;
        bool compact;

        // Bars are full at these values. Frequency scales are for display only.
        const double CpuGhzMax = 5.0, GpuGhzMax = 2.5, PkgWMax = 60, GpuWMax = 40, TempMin = 30, TempMax = 100, FanMax = 7500;

        static readonly Color Normal = Color.FromArgb(46, 125, 50), Warn = Color.FromArgb(190, 120, 0), Hot = Color.FromArgb(198, 40, 40), Freq = Color.FromArgb(30, 100, 180);
        static readonly Color TrackDark = Color.FromArgb(48, 255, 255, 255), TrackLight = Color.FromArgb(170, 30, 30, 30);

        Snapshot values = new Snapshot();

        // Graph history, one entry per sample, newest at histNext - 1.
        const int Hist = 60;
        readonly double[] hCpu = new double[Hist], hGpu = new double[Hist], hMem = new double[Hist], hIn = new double[Hist], hOut = new double[Hist];
        readonly double[] hTemp = new double[Hist], hFan0 = new double[Hist], hFan1 = new double[Hist], hFan = new double[Hist];
        static readonly Color FanLine = Color.FromArgb(70, 180, 90);
        int histNext, histCount;
        static readonly Color CpuLine = Color.FromArgb(60, 160, 230), GpuLine = Color.FromArgb(40, 190, 170), MemLine = Color.FromArgb(190, 110, 230), NetLine = Color.FromArgb(235, 150, 50);
        int lastH, lastX, lastY, width;
        bool lastLight;
        Font font;
        string drawn;
        IntPtr embeddedIn;
        bool embedFailed, shown;

        public event Action Click; // left or right button
        public bool Visible { get { return shown; } }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            if (m.Msg == WM_RBUTTONUP || m.Msg == WM_LBUTTONUP) { var h = Click; if (h != null) h(); return; }
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

        public void SetValues(Snapshot s)
        {
            values = s;
            hCpu[histNext] = s.CpuPct; hGpu[histNext] = s.GpuPct; hMem[histNext] = s.MemPct; hIn[histNext] = s.NetIn; hOut[histNext] = s.NetOut;
            hTemp[histNext] = s.TempC; hFan0[histNext] = s.Fan0 < 0 ? double.NaN : s.Fan0; hFan1[histNext] = s.Fan1 < 0 ? double.NaN : s.Fan1; hFan[histNext] = FanMean(s);
            histNext = (histNext + 1) % Hist;
            if (histCount < Hist) histCount++;
            drawn = null; // the graphs move with every sample
            Redraw();
        }

        static string Key(Snapshot s)
        {
            return Num(s.CpuPct, "0") + "|" + Num(s.GpuPct, "0") + "|" + Num(s.PGhz, "0.0") + Num(s.CpuGhz, "0.0") + Num(s.EGhz, "0.0") + Num(s.LGhz, "0.0") + "|" + Num(s.PkgW, "0.0") + "|" + Num(s.TempC, "0") + "|" + Num(s.GpuMhz, "0") + "|" + Num(s.GpuW, "0.0") + "|" + s.Fan0 + "|" + s.Fan1 + "|" + Num(s.SysW, "0.0");
        }

        public static int WidthFor(int h, bool compact)
        {
            float k = h / 60f;
            float units = compact
                ? CompactUseW + ColGap + PowerW + ColGap + CompactRightW
                : LabelW + PctW + ColGap + FreqW * 3 + InnerGap * 2 + ColGap + PowerW + ColGap + TempW + ColGap + FanW;
            return (int)Math.Ceiling(k * units) + 2;
        }

        // Auto picks the full layout when it fits between the taskbar buttons and the right edge.
        // The margin before switching back to full stops it flipping when the space is borderline.
        bool PickCompact(int h, int edge, TaskbarProbe.Result p)
        {
            if (Layout == LayoutFull) return false;
            if (Layout == LayoutCompact) return true;
            if (p == null || p.TasksRight < 0) return compact;
            int room = edge - 8 - (p.TasksRight + 12);
            int full = WidthFor(h, false);
            return compact ? room < full + 24 : room < full;
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
                lastH = h; lastLight = light; drawn = null;
            }

            // Stay left of the weather button when it sits between the taskbar buttons and the tray.
            var p = Probe == null ? null : Probe.Latest;
            if (p != null && (DateTime.UtcNow - p.At).TotalSeconds > 15) p = null;
            int edge = right;
            if (AvoidWeather)
            {
                if (p == null) edge -= (int)Math.Ceiling(h / 60f * WeatherW);
                else if (p.WidgetsLeft > p.TasksRight && p.WidgetsRight <= right + 2) edge = p.WidgetsLeft;
            }
            bool c = PickCompact(h, edge, p);
            int w = WidthFor(h, c);
            if (c != compact || w != width) { compact = c; width = w; drawn = null; lastX = int.MinValue; }
            if (Handle == IntPtr.Zero || (!embedFailed && embeddedIn != bar)) Create(bar);
            if (!shown) { Native.ShowWindow(Handle, Native.SW_SHOWNA); shown = true; drawn = null; }

            int x = edge - width - 8, y = r.Top;
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

        public static Font MakeFont(int h) { return new Font("Segoe UI", Math.Max(9f, h * 0.24f), FontStyle.Regular, GraphicsUnit.Pixel); }

        static bool TaskbarOnScreen(Native.RECT r)
        {
            var b = Screen.PrimaryScreen.Bounds;
            return r.Top < b.Bottom - 4 && r.Bottom > b.Top + 4;
        }

        static bool ForegroundIsFullscreen() { return IsFullscreen(Native.GetForegroundWindow()); }

        // True for a window that covers its whole monitor, other than the desktop and the shell.
        public static bool IsFullscreen(IntPtr f)
        {
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

        public Bitmap Render(int w, int h, Font f, bool light, bool compact)
        {
            var s = values;
            currentTrack = light ? TrackLight : TrackDark;
            float k = h / 60f;
            float rowH = RowH * k, gap = RowGap * k, rad = Radius * k, inner = InnerGap * k, col = ColGap * k;
            float y1 = (h - rowH * 2 - gap) / 2, y2 = y1 + rowH + gap;
            float freqW = FreqW * k, freqAll = freqW * 3 + inner * 2;
            float xPct = LabelW * k, xFreq = xPct + (PctW + ColGap) * k, xPower = xFreq + freqAll + col, xTemp = xPower + PowerW * k + col, xFan = xTemp + TempW * k + col;

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            if (compact) { RenderCompact(bmp, f, k, rowH, rad, col, y1, y2, s, light); return bmp; }
            using (var g = Graphics.FromImage(bmp))
            using (var label = new SolidBrush(light ? Color.FromArgb(40, 40, 40) : Color.FromArgb(225, 225, 225)))
            using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip, Trimming = StringTrimming.None })
            using (var left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip, Trimming = StringTrimming.None })
            {
                // Alpha 1 is invisible but keeps the whole area clickable for the right-click menu.
                g.Clear(Color.FromArgb(1, 0, 0, 0));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                g.DrawString("CPU", f, label, new RectangleF(0, y1, xPct, rowH), left);
                g.DrawString("GPU", f, label, new RectangleF(0, y2, xPct, rowH), left);

                Graph(g, f, center, new RectangleF(xPct, y1, PctW * k, rowH), rad, CpuLine, hCpu, 0, 100, null, Num(s.CpuPct, "0") + "%");
                Graph(g, f, center, new RectangleF(xPct, y2, PctW * k, rowH), rad, GpuLine, hGpu, 0, 100, null, Num(s.GpuPct, "0") + "%");

                Bar(g, f, center, new RectangleF(xFreq, y1, freqW, rowH), rad, s.PGhz / CpuGhzMax, Freq, "P " + Num(s.PGhz, "0.0"));
                Bar(g, f, center, new RectangleF(xFreq + freqW + inner, y1, freqW, rowH), rad, s.EGhz / CpuGhzMax, Freq, "E " + Num(s.EGhz, "0.0"));
                Bar(g, f, center, new RectangleF(xFreq + (freqW + inner) * 2, y1, freqW, rowH), rad, s.LGhz / CpuGhzMax, Freq, "L " + Num(s.LGhz, "0.0"));
                Bar(g, f, center, new RectangleF(xFreq, y2, freqAll, rowH), rad, s.GpuMhz / 1000 / GpuGhzMax, Freq, Num(s.GpuMhz / 1000, "0.00") + " GHz");

                Graph(g, f, center, new RectangleF(xPower, y1, PowerW * k, rowH), rad, MemLine, hMem, 0, 100, null, Num(s.PkgW, "0.0") + " W");
                Graph(g, f, center, new RectangleF(xPower, y2, PowerW * k, rowH), rad, NetLine, hIn, 0, NetScale(), hOut, SecondPower(s));

                Graph(g, f, center, new RectangleF(xTemp, y1, TempW * k, rowH * 2 + gap), rad, TempColor(s), hTemp, TempMin, TempMax, null, Num(s.TempC, "0") + " °C");

                Graph(g, f, center, new RectangleF(xFan, y1, FanW * k, rowH), rad, FanLine, hFan0, 0, FanMax, null, (s.Fan0 < 0 ? "--" : s.Fan0.ToString()) + " rpm");
                Graph(g, f, center, new RectangleF(xFan, y2, FanW * k, rowH), rad, FanLine, hFan1, 0, FanMax, null, (s.Fan1 < 0 ? "--" : s.Fan1.ToString()) + " rpm");
            }
            return bmp;
        }

        void RenderCompact(Bitmap bmp, Font f, float k, float rowH, float rad, float col, float y1, float y2, Snapshot s, bool light)
        {
            float use = CompactUseW * k, xPower = use + col, xRight = xPower + PowerW * k + col, pw = PowerW * k;
            double fan = FanMean(s);
            double netScale = NetScale();
            using (var g = Graphics.FromImage(bmp))
            using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip, Trimming = StringTrimming.None })
            {
                g.Clear(Color.FromArgb(1, 0, 0, 0)); // clickable, see Render
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                Graph(g, f, center, new RectangleF(0, y1, use, rowH), rad, CpuLine, hCpu, 0, 100, null, Num(s.CpuGhz, "0.0") + " GHz");
                Graph(g, f, center, new RectangleF(0, y2, use, rowH), rad, GpuLine, hGpu, 0, 100, null, Num(s.GpuMhz / 1000, "0.00") + " GHz");
                Graph(g, f, center, new RectangleF(xPower, y1, pw, rowH), rad, MemLine, hMem, 0, 100, null, Num(s.PkgW, "0.0") + " W");
                Graph(g, f, center, new RectangleF(xPower, y2, pw, rowH), rad, NetLine, hIn, 0, netScale, hOut, SecondPower(s));
                Graph(g, f, center, new RectangleF(xRight, y1, CompactRightW * k, rowH), rad, TempColor(s), hTemp, TempMin, TempMax, null, Num(s.TempC, "0") + " °C");
                Graph(g, f, center, new RectangleF(xRight, y2, CompactRightW * k, rowH), rad, FanLine, hFan, 0, FanMax, null, Num(fan, "0") + " rpm");
            }
        }

        static double FanMean(Snapshot s)
        {
            int n = 0, sum = 0;
            if (s.Fan0 >= 0) { sum += s.Fan0; n++; }
            if (s.Fan1 >= 0) { sum += s.Fan1; n++; }
            return n == 0 ? double.NaN : (double)sum / n;
        }

        // Green, orange or red by the current temperature; grey while it cannot be read.
        static Color TempColor(Snapshot s)
        {
            if (double.IsNaN(s.TempC)) return Color.Gray;
            var c = Level(s.TempC, 80, 90);
            return Color.FromArgb(Math.Min(255, c.R + 40), Math.Min(255, c.G + 40), Math.Min(255, c.B + 40));
        }

        // The GPU power, or on battery the whole laptop's power marked with a sigma.
        static string SecondPower(Snapshot s)
        {
            return double.IsNaN(s.SysW) ? Num(s.GpuW, "0.0") + " W" : "\u03a3" + Num(s.SysW, s.SysW >= 10 ? "0" : "0.0") + " W";
        }

        // Like Task Manager, the network graph scales to the busiest recent sample, rounded up to 1, 2 or 5 times a power of ten (at least 100 kbit/s).
        double NetScale()
        {
            double top = 100e3 / 8;
            for (int i = 0; i < histCount; i++)
            {
                if (hIn[i] > top) top = hIn[i];
                if (hOut[i] > top) top = hOut[i];
            }
            double p = Math.Pow(10, Math.Floor(Math.Log10(top)));
            return top <= p ? p : top <= 2 * p ? 2 * p : top <= 5 * p ? 5 * p : 10 * p;
        }

        // A history graph across the box: the oldest sample at the left, the newest at the right edge.
        // The main series is a filled area with a line on top; the optional second one is a dashed line.
        void Graph(Graphics g, Font f, StringFormat center, RectangleF r, float rad, Color line, double[] main, double min, double max, double[] second, string text)
        {
            using (var path = Rounded(r, rad))
            using (var track = new SolidBrush(currentTrack))
            {
                g.FillPath(track, path);
                var state = g.Save();
                g.SetClip(path);
                var pts = Points(r, main, min, max);
                if (pts != null)
                {
                    var area = new PointF[pts.Length + 2];
                    pts.CopyTo(area, 0);
                    area[pts.Length] = new PointF(pts[pts.Length - 1].X, r.Bottom);
                    area[pts.Length + 1] = new PointF(pts[0].X, r.Bottom);
                    using (var fill = new SolidBrush(Color.FromArgb(110, line))) g.FillPolygon(fill, area);
                    using (var pen = new Pen(line, Math.Max(1f, r.Height / 20))) g.DrawLines(pen, pts);
                }
                var pts2 = second == null ? null : Points(r, second, min, max);
                if (pts2 != null)
                    using (var pen = new Pen(line, Math.Max(1f, r.Height / 20)) { DashStyle = DashStyle.Dash }) g.DrawLines(pen, pts2);
                g.Restore(state);
                g.DrawString(text, f, Brushes.White, r, center);
            }
        }

        // Points for the samples so far; a missing reading draws as zero. Null until there are two samples.
        PointF[] Points(RectangleF r, double[] ring, double min, double max)
        {
            if (histCount < 2) return null;
            var pts = new PointF[histCount];
            float step = r.Width / (Hist - 1);
            for (int i = 0; i < histCount; i++)
            {
                double v = ring[(histNext - histCount + i + Hist) % Hist];
                double frac = double.IsNaN(v) ? 0 : Math.Max(0, Math.Min(1, (v - min) / (max - min)));
                float y = r.Bottom - 1 - (float)frac * (r.Height - 2);
                pts[i] = new PointF(r.Right - (histCount - 1 - i) * step, y);
            }
            return pts;
        }

        void Redraw()
        {
            if (font == null || Handle == IntPtr.Zero || !shown) return;
            string key = Key(values);
            if (key == drawn) return;
            drawn = key;
            using (var bmp = Render(width, lastH, font, lastLight, compact))
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
