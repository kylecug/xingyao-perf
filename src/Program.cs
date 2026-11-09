using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace XingyaoPerf
{
    internal static class Startup
    {
        const string TaskName = "xingyao-perf";

        static int Schtasks(string args)
        {
            var i = new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false };
            using (var p = Process.Start(i)) { p.WaitForExit(); return p.ExitCode; }
        }

        public static bool Enabled { get { return Schtasks("/query /tn " + TaskName) == 0; } }
        public static bool RunTask() { return Schtasks("/run /tn " + TaskName) == 0; }
        public static void Disable() { Schtasks("/delete /tn " + TaskName + " /f"); }

        // Elevated logon task: no UAC prompt, and it must also start and keep running on battery.
        public static void Enable()
        {
            string user = WindowsIdentity.GetCurrent().Name;
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
                + "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + Esc(user) + "</UserId></LogonTrigger></Triggers>"
                + "<Principals><Principal id=\"Author\"><UserId>" + Esc(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>"
                + "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>"
                + "<StartWhenAvailable>true</StartWhenAvailable><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>"
                + "<Actions Context=\"Author\"><Exec><Command>" + Esc(exe) + "</Command></Exec></Actions></Task>";
            string tmp = Path.Combine(Path.GetTempPath(), "xingyao-perf-task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            try
            {
                int r = Schtasks("/create /tn " + TaskName + " /xml \"" + tmp + "\" /f");
                if (r != 0) throw new InvalidOperationException("schtasks exit code " + r);
            }
            finally { File.Delete(tmp); }
        }

        static string Esc(string s) { return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"); }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        static readonly string[] Names = { "未知", "办公模式", "平衡模式", "性能模式" };

        readonly Settings settings = Settings.Load();
        readonly Monitor monitor;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Overlay overlay = new Overlay();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem[] modeItems = new ToolStripMenuItem[4];
        readonly ToolStripMenuItem overlayItem, weatherItem, startupItem;
        readonly ToolStripMenuItem[] layoutItems = new ToolStripMenuItem[3];
        TaskbarProbe probe; // Runs only for the auto layout. Manual layouts never load UI Automation.
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 500 };
        readonly SynchronizationContext ui;
        IntPtr iconHandle;
        Snapshot shown;

        public TrayApp()
        {
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            monitor = new Monitor(settings);
            for (int m = 1; m <= 3; m++)
            {
                int mode = m;
                modeItems[m] = new ToolStripMenuItem(Names[m], ModeIcon.Draw(m, menu.ImageScalingSize.Height), (s, e) => Pick(mode)) { Enabled = Platform.ModeWriteAllowed };
                menu.Items.Add(modeItems[m]);
            }
            menu.Items.Add(new ToolStripSeparator());
            overlayItem = new ToolStripMenuItem("在任务栏显示监控", null, (s, e) => ToggleOverlay()) { Checked = settings.Overlay };
            weatherItem = new ToolStripMenuItem("避让天气", null, (s, e) => ToggleWeather()) { Checked = settings.AvoidWeather };
            overlay.AvoidWeather = settings.AvoidWeather;
            overlay.Layout = settings.Layout;
            UpdateProbe();
            var layoutMenu = new ToolStripMenuItem("监控条样式");
            string[] layoutNames = { "自动（空间不够时用精简版）", "完整版", "精简版" };
            for (int i = 0; i < 3; i++)
            {
                int layout = i;
                layoutItems[i] = new ToolStripMenuItem(layoutNames[i], null, (s, e) => PickLayout(layout)) { Checked = settings.Layout == i };
                layoutMenu.DropDownItems.Add(layoutItems[i]);
            }
            startupItem = new ToolStripMenuItem("开机自动启动", null, (s, e) => ToggleStartup());
            menu.Items.Add(overlayItem);
            menu.Items.Add(layoutMenu);
            menu.Items.Add(weatherItem);
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => Quit());
            menu.Opening += (s, e) => startupItem.Checked = Startup.Enabled;

            tray.ContextMenuStrip = menu;
            overlay.RightClick += () => menu.Show(Cursor.Position);
            ShowMode(0);
            tray.Visible = true;

            if (!Platform.ModeWriteAllowed)
                tray.ShowBalloonTip(8000, "xingyao-perf", "当前 BIOS（" + Platform.BiosFound + "）或主板未经验证，模式切换已禁用，只显示监控。", ToolTipIcon.Warning);

            monitor.ModeChanged += m => ui.Post(_ => ShowMode(m), null);
            monitor.Start();
            timer.Tick += (s, e) => Tick();
            timer.Start();
        }

        void Tick()
        {
            if (!settings.Overlay) { if (overlay.Visible) overlay.Hide(); return; }
            var s = monitor.Latest;
            if (s != shown) { shown = s; overlay.SetValues(s); }
            overlay.Track_();
        }

        void Pick(int mode)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { monitor.SetMode(mode); }
                catch (Exception e)
                {
                    Log.Write("set mode " + mode + ": " + e.Message);
                    ui.Post(__ => tray.ShowBalloonTip(5000, "切换失败", e.Message, ToolTipIcon.Error), null);
                }
            });
        }

        void ShowMode(int mode)
        {
            for (int m = 1; m <= 3; m++) modeItems[m].Checked = m == mode;
            using (var bmp = ModeIcon.Draw(mode, SystemInformation.SmallIconSize.Height))
            {
                IntPtr h = bmp.GetHicon();
                using (var icon = Icon.FromHandle(h)) tray.Icon = icon;
                if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
                iconHandle = h;
            }
            tray.Text = "xingyao-perf: " + Names[mode];
        }

        void ToggleOverlay()
        {
            settings.Overlay = !settings.Overlay;
            overlayItem.Checked = settings.Overlay;
            settings.Save();
        }

        void PickLayout(int layout)
        {
            settings.Layout = layout;
            overlay.Layout = layout;
            for (int i = 0; i < 3; i++) layoutItems[i].Checked = i == layout;
            UpdateProbe();
            settings.Save();
        }

        void UpdateProbe()
        {
            bool want = settings.Layout == Overlay.LayoutAuto;
            if (want && probe == null) probe = new TaskbarProbe();
            else if (!want && probe != null) { probe.Dispose(); probe = null; }
            overlay.Probe = probe;
        }

        void ToggleWeather()
        {
            settings.AvoidWeather = !settings.AvoidWeather;
            weatherItem.Checked = settings.AvoidWeather;
            overlay.AvoidWeather = settings.AvoidWeather;
            settings.Save();
        }

        void ToggleStartup()
        {
            try { if (Startup.Enabled) Startup.Disable(); else Startup.Enable(); }
            catch (Exception e)
            {
                Log.Write("startup: " + e.Message);
                tray.ShowBalloonTip(5000, "设置开机启动失败", e.Message, ToolTipIcon.Error);
            }
        }

        void Quit()
        {
            timer.Stop();
            tray.Visible = false;
            monitor.Dispose();
            if (probe != null) probe.Dispose();
            overlay.Dispose();
            ExitThread();
        }
    }

    internal static class Program
    {
        static bool IsAdmin() { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }

        [STAThread]
        static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("fatal: " + e.ExceptionObject);
            Application.ThreadException += (s, e) => Log.Write("ui: " + e.Exception);
            bool noElevate = false;
            string selftest = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--no-elevate") noElevate = true;
                else if (args[i] == "--no-gpu") Monitor.SkipGpu = true;
                else if (args[i] == "--selftest" && i + 1 < args.Length) selftest = args[++i];
            }
            if (selftest != null) return SelfTest(selftest);

            if (!IsAdmin() && !noElevate)
            {
                // Prefer the elevated logon task, which needs no UAC prompt. Otherwise ask once.
                if (Startup.RunTask()) return 0;
                try
                {
                    Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName) { UseShellExecute = true, Verb = "runas" });
                    return 0;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return 1; // UAC declined.
                }
            }

            bool created;
            // A --no-elevate test run may sit beside the installed instance.
            using (var single = new Mutex(true, noElevate ? @"Local\xingyao-perf-test" : @"Local\xingyao-perf-single", out created))
            {
                if (!created) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
            return 0;
        }

        // Live values, then a hot example, on dark and light taskbars, followed by the three mode icons.
        static void SavePreview(string file, Snapshot live, bool compact)
        {
            var hot = new Snapshot { CpuPct = 100, GpuPct = 96, PGhz = 4.2, EGhz = 3.8, LGhz = 2.1, PkgW = 52.3, GpuW = 22.4, TempC = 91, GpuMhz = 2100, Fan0 = 6900, Fan1 = 4800 };
            var warm = new Snapshot { CpuPct = 76, GpuPct = 41, PGhz = 3.1, EGhz = 2.7, LGhz = 1.9, PkgW = 42.1, GpuW = 12.4, TempC = 83, GpuMhz = 1450, Fan0 = 5100, Fan1 = 3000 };
            const int H = 60;
            int w = Overlay.WidthFor(H, compact);
            var rows = new[] { live, warm, hot };
            using (var sheet = new Bitmap(w + 170, (H + 6) * rows.Length * 2))
            using (var g = Graphics.FromImage(sheet))
            using (var o = new Overlay())
            using (var f = Overlay.MakeFont(H))
            {
                for (int t = 0; t < 2; t++)
                {
                    bool light = t == 1;
                    g.FillRectangle(light ? Brushes.WhiteSmoke : new SolidBrush(Color.FromArgb(32, 28, 28)), 0, t * (H + 6) * rows.Length, sheet.Width, (H + 6) * rows.Length);
                    for (int i = 0; i < rows.Length; i++)
                    {
                        o.SetValues(rows[i]);
                        using (var bmp = o.Render(w, H, f, light, compact)) g.DrawImage(bmp, 6, (t * rows.Length + i) * (H + 6) + 3, w, H);
                        for (int m = 1; m <= 3; m++)
                            using (var ic = ModeIcon.Draw(m, 40)) g.DrawImage(ic, w + 14 + (m - 1) * 50, (t * rows.Length + i) * (H + 6) + 13, 40, 40);
                    }
                }
                sheet.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        // Reads everything without the UI, reports cost per sample, and renders a preview. Sends no writes.
        static int SelfTest(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Board: " + Platform.BoardFound + "  BIOS: " + Platform.BiosFound + "  Admin: " + IsAdmin());
            sb.AppendLine("ModeWriteAllowed: " + Platform.ModeWriteAllowed + "  FanReadAllowed: " + Platform.FanReadAllowed);
            var s = new Settings();
            using (var m = new Monitor(s))
            {
                try { sb.AppendLine("Firmware mode: " + m.Firmware.ReadMode()); }
                catch (Exception e) { sb.AppendLine("Firmware mode: failed: " + e.Message); }
                m.InitSensors();
                Thread.Sleep(1500);
                var me = Process.GetCurrentProcess();
                me.Refresh();
                TimeSpan cpu0 = me.TotalProcessorTime;
                var sw = Stopwatch.StartNew();
                const int N = 5;
                Snapshot last = null;
                for (int i = 0; i < N; i++)
                {
                    long t0 = sw.ElapsedMilliseconds;
                    last = m.SampleOnce();
                    sb.AppendLine(string.Format("sample {0} ({1} ms): CPU {11}% GPU {12}%, P {2} E {3} L {4} GHz, pkg {5} W, {6} C, GPU {7} MHz {8} W, fans {9}/{10}",
                        i, sw.ElapsedMilliseconds - t0, Overlay.Num(last.PGhz, "0.00"), Overlay.Num(last.EGhz, "0.00"), Overlay.Num(last.LGhz, "0.00"), Overlay.Num(last.PkgW, "0.0"),
                        Overlay.Num(last.TempC, "0"), Overlay.Num(last.GpuMhz, "0"), Overlay.Num(last.GpuW, "0.0"), last.Fan0, last.Fan1, Overlay.Num(last.CpuPct, "0"), Overlay.Num(last.GpuPct, "0")));
                    Thread.Sleep(1000);
                }
                me.Refresh();
                sb.AppendLine("CPU per sample: " + ((me.TotalProcessorTime - cpu0).TotalMilliseconds / N).ToString("0.0") + " ms");
                sb.AppendLine("Private memory: " + (me.PrivateMemorySize64 / 1024 / 1024) + " MB, working set " + (me.WorkingSet64 / 1024 / 1024) + " MB");
                try
                {
                    SavePreview(path + ".png", last, false);
                    SavePreview(path + ".compact.png", last, true);
                }
                catch (Exception e) { sb.AppendLine("preview: " + e.Message); }
                try
                {
                    var sw2 = Stopwatch.StartNew();
                    var t = TaskbarProbe.Probe();
                    long first = sw2.ElapsedMilliseconds;
                    sw2.Restart();
                    t = TaskbarProbe.Probe();
                    sb.AppendLine("Taskbar probe: buttons end at " + t.TasksRight + ", weather " + t.WidgetsLeft + ".." + t.WidgetsRight + " (" + first + " ms first, " + sw2.ElapsedMilliseconds + " ms again)");
                    sb.AppendLine("Widths at 72 px: full " + Overlay.WidthFor(72, false) + ", compact " + Overlay.WidthFor(72, true));
                    me.Refresh();
                    sb.AppendLine("After probe: private " + (me.PrivateMemorySize64 / 1024 / 1024) + " MB, working set " + (me.WorkingSet64 / 1024 / 1024) + " MB");
                }
                catch (Exception e) { sb.AppendLine("taskbar probe: " + e.Message); }
            }
            File.WriteAllText(path, sb.ToString());
            return 0;
        }
    }
}
