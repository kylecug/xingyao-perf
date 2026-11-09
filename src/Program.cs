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
        static readonly string[] Glyphs = { "?", "办", "平", "性" };
        static readonly Color[] Colors = { Color.Gray, Color.FromArgb(46, 125, 50), Color.FromArgb(21, 101, 192), Color.FromArgb(198, 40, 40) };

        readonly Settings settings = Settings.Load();
        readonly Monitor monitor;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Overlay overlay = new Overlay();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem[] modeItems = new ToolStripMenuItem[4];
        readonly ToolStripMenuItem overlayItem, startupItem;
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
                modeItems[m] = new ToolStripMenuItem(Names[m], null, (s, e) => Pick(mode)) { Enabled = Platform.ModeWriteAllowed };
                menu.Items.Add(modeItems[m]);
            }
            menu.Items.Add(new ToolStripSeparator());
            overlayItem = new ToolStripMenuItem("在任务栏显示监控", null, (s, e) => ToggleOverlay()) { Checked = settings.Overlay };
            startupItem = new ToolStripMenuItem("开机自动启动", null, (s, e) => ToggleStartup());
            menu.Items.Add(overlayItem);
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => Quit());
            menu.Opening += (s, e) => startupItem.Checked = Startup.Enabled;

            tray.ContextMenuStrip = menu;
            overlay.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) menu.Show(Cursor.Position); };
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
            overlay.Track();
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
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            using (var bg = new SolidBrush(Colors[mode]))
            using (var f = new Font("Microsoft YaHei", 21f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.FillRectangle(bg, 0, 0, 32, 32);
                g.DrawString(Glyphs[mode], f, Brushes.White, new RectangleF(0, 1, 32, 32), sf);
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
            using (var single = new Mutex(true, @"Local\xingyao-perf-single", out created))
            {
                if (!created) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
            return 0;
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
                    sb.AppendLine(string.Format("sample {0} ({1} ms): P {2} E {3} L {4} GHz, pkg {5} W, {6} C, GPU {7} MHz {8} W, fans {9}/{10}",
                        i, sw.ElapsedMilliseconds - t0, Overlay.Num(last.PGhz, "0.00"), Overlay.Num(last.EGhz, "0.00"), Overlay.Num(last.LGhz, "0.00"), Overlay.Num(last.PkgW, "0.0"),
                        Overlay.Num(last.TempC, "0"), Overlay.Num(last.GpuMhz, "0"), Overlay.Num(last.GpuW, "0.0"), last.Fan0, last.Fan1));
                    Thread.Sleep(1000);
                }
                me.Refresh();
                sb.AppendLine("CPU per sample: " + ((me.TotalProcessorTime - cpu0).TotalMilliseconds / N).ToString("0.0") + " ms");
                sb.AppendLine("Private memory: " + (me.PrivateMemorySize64 / 1024 / 1024) + " MB, working set " + (me.WorkingSet64 / 1024 / 1024) + " MB");
                try
                {
                    using (var o = new Overlay())
                    using (var f = new Font("Segoe UI", 18f, FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        o.SetValues(last);
                        using (var bmp = o.Render(420, 60, f, false)) bmp.Save(path + ".png", System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                catch (Exception e) { sb.AppendLine("preview: " + e.Message); }
            }
            File.WriteAllText(path, sb.ToString());
            return 0;
        }
    }
}
