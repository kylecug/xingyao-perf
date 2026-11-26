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
        readonly GameOverlay game = new GameOverlay();
        readonly ToolStripMenuItem[] gameItems = new ToolStripMenuItem[3];
        ToolStripMenuItem autoGameMenu;
        int autoPrev, autoSet, scanTick, gameGone; // scene before the game, scene set for it
        bool scanBusy, updating;
        ToolStripMenuItem updateItem;
        // 0 is "auto" in the CPU and GPU menus: no limit in the official modes, the custom scene's setting otherwise.
        static readonly int[] CpuStates = { 0, 99, 80, 70 };
        static readonly string[] CpuStateNames = { "自动（跟随模式）", "关闭睿频（约 1.8 GHz）", "约 1.5 GHz", "约 1.3 GHz" };
        static readonly int[] GpuCaps = { 0, 2000, 1600, 1200, 900 };
        static readonly int[] FanLevels = { Monitor.FanAuto, 0, 100, 70, 50, 30, 20, 10 };
        readonly ToolStripMenuItem officialMenu = new ToolStripMenuItem("官方模式");
        readonly ToolStripMenuItem[] sceneItems = new ToolStripMenuItem[Monitor.SceneLast + 1];
        const string CustomPrefix = "自定义：";
        CustomForm customForm;
        readonly ToolStripMenuItem fanMenu = new ToolStripMenuItem("风扇");
        readonly ToolStripMenuItem cpuMenu = new ToolStripMenuItem("CPU 最高频率"), gpuMenu = new ToolStripMenuItem("GPU 最高频率");
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem overlayItem, weatherItem, startupItem;
        readonly ToolStripMenuItem[] layoutItems = new ToolStripMenuItem[3];
        static readonly int[] ChargeLimits = { 60, 80, 0 };
        readonly ToolStripMenuItem[] chargeItems = new ToolStripMenuItem[3];
        IntPtr trayTaskbar; // the taskbar window the tray icon was last added to
        TaskbarProbe probe; // Runs only for the auto layout. Manual layouts never load UI Automation.
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 500 };
        readonly SynchronizationContext ui;
        IntPtr iconHandle;
        Snapshot shown, gameShown;

        public TrayApp()
        {
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            monitor = new Monitor(settings);
            for (int m = 1; m <= 3; m++)
            {
                int scene = m;
                sceneItems[m] = new ToolStripMenuItem(Names[m], ModeIcon.Draw(m, menu.ImageScalingSize.Height), (s, e) => Pick(scene)) { Enabled = Platform.ModeWriteAllowed };
                officialMenu.DropDownItems.Add(sceneItems[m]);
            }
            menu.Items.Add(officialMenu);            // A custom scene is picked by clicking it; its submenu holds only "settings".
            for (int m = Monitor.SceneCustom; m <= Monitor.SceneLast; m++)
            {
                int scene = m;
                var item = new ToolStripMenuItem(CustomPrefix + settings.Custom[m - Monitor.SceneCustom].Name) { Enabled = Platform.ModeWriteAllowed };
                item.Click += (s, e) => { menu.Close(); Pick(scene); };
                item.DropDownItems.Add(new ToolStripMenuItem("设置…", null, (s, e) => OpenCustom(scene - Monitor.SceneCustom)));
                sceneItems[m] = item;
                menu.Items.Add(item);
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
            var chargeMenu = new ToolStripMenuItem("充电上限") { Enabled = Platform.FanReadAllowed };
            string[] chargeNames = { "60%", "80%", "关闭（充到 100%）" };
            for (int i = 0; i < 3; i++)
            {
                int limit = ChargeLimits[i];
                chargeItems[i] = new ToolStripMenuItem(chargeNames[i], null, (s, e) => PickCharge(limit));
                chargeMenu.DropDownItems.Add(chargeItems[i]);
            }
            startupItem = new ToolStripMenuItem("开机自动启动", null, (s, e) => ToggleStartup());
            for (int i = 0; i < CpuStates.Length; i++)
            {
                int percent = CpuStates[i];
                cpuMenu.DropDownItems.Add(new ToolStripMenuItem(CpuStateNames[i], null, (s, e) => PickCpuCap(percent)) { Tag = percent });
            }
            for (int i = 0; i < GpuCaps.Length; i++)
            {
                int mhz = GpuCaps[i];
                gpuMenu.DropDownItems.Add(new ToolStripMenuItem(mhz == 0 ? "自动（跟随模式）" : (mhz / 1000.0).ToString("0.0") + " GHz", null, (s, e) => PickGpuCap(mhz)) { Tag = mhz });
            }
            cpuMenu.DropDownOpening += (s, e) => RefreshCaps();
            gpuMenu.DropDownOpening += (s, e) => RefreshCaps();
            RefreshCaps();
            for (int i = 0; i < FanLevels.Length; i++)
            {
                int level = FanLevels[i];
                string name = level == Monitor.FanAuto ? "自动（跟随模式）" : level == 0 ? "EC 曲线" : level == 100 ? "全速 100%" : level + "%";
                fanMenu.DropDownItems.Add(new ToolStripMenuItem(name, null, (s, e) => PickFan(level)) { Tag = level });
            }
            fanMenu.Enabled = Platform.FanReadAllowed;
            fanMenu.DropDownOpening += (s, e) => Check(fanMenu, monitor.FanChoice);
            Check(fanMenu, Monitor.FanAuto);
            monitor.FanReleased += why => ui.Post(_ => { Check(fanMenu, Monitor.FanAuto); tray.ShowBalloonTip(5000, "风扇已恢复自动", "原因：" + why, ToolTipIcon.Warning); }, null);
            monitor.SceneChanged += () => ui.Post(_ => { ShowMode(monitor.CurrentMode); Check(fanMenu, monitor.FanChoice); RefreshCaps(); }, null);
            Microsoft.Win32.SystemEvents.PowerModeChanged += (s, e) => { if (e.Mode == Microsoft.Win32.PowerModes.Suspend) monitor.OnSuspend(); };
            Microsoft.Win32.SystemEvents.SessionEnding += (s, e) => monitor.OnSuspend(); // shutdown or sign-out
            Microsoft.Win32.SystemEvents.PowerModeChanged += (s, e) => { if (e.Mode == Microsoft.Win32.PowerModes.StatusChange) monitor.ApplyScenePowerMode(); };
            menu.Items.Add(fanMenu);
            menu.Items.Add(cpuMenu);
            menu.Items.Add(gpuMenu);
            menu.Items.Add(chargeMenu);
            menu.Items.Add(new ToolStripSeparator());
            var gameMenu = new ToolStripMenuItem("游戏和全屏时显示监控");
            string[] gameNames = { "关闭", "仅游戏（自动识别）", "所有全屏程序和游戏" };
            for (int i = 0; i < 3; i++)
            {
                int when = i;
                gameItems[i] = new ToolStripMenuItem(gameNames[i], null, (s, e) => PickGame(when)) { Checked = settings.GameOverlay == i };
                gameMenu.DropDownItems.Add(gameItems[i]);
            }
            game.SetGames(settings.Games.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
            // Switch to a chosen scene while a game runs, and back when it ends.
            autoGameMenu = new ToolStripMenuItem("游戏时自动切换模式") { Enabled = Platform.ModeWriteAllowed };
            for (int i = 0; i <= Monitor.SceneLast; i++)
            {
                int scene = i;
                autoGameMenu.DropDownItems.Add(new ToolStripMenuItem(i == 0 ? "关闭" : SceneName(i), null, (s, e) => PickAutoGame(scene)) { Tag = i, Checked = settings.AutoGame == i });
            }
            menu.Items.Add(overlayItem);
            menu.Items.Add(gameMenu);
            menu.Items.Add(autoGameMenu);
            menu.Items.Add(layoutMenu);
            menu.Items.Add(weatherItem);
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            updateItem = new ToolStripMenuItem("检查更新（版本 " + AppVersion.Text + "）", null, (s, e) => CheckUpdate());
            menu.Items.Add(updateItem);
            menu.Items.Add("退出", null, (s, e) => Quit());
            menu.Opening += (s, e) => { startupItem.Checked = Startup.Enabled; RefreshCharge(); RefreshCaps(); };

            tray.ContextMenuStrip = menu;
            // Left click opens the same menu as right click. NotifyIcon only has a private method that
            // positions the menu and takes the foreground correctly, so it is called by reflection.
            var showMenu = typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            tray.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (showMenu != null) showMenu.Invoke(tray, null); else menu.Show(Cursor.Position);
            };
            // The bar window never activates, so menu.Show alone leaves the menu without focus: it then ignores
            // clicks elsewhere and stays open. The tray's own path takes the foreground first.
            overlay.Click += () =>
            {
                if (menu.Visible) { menu.Close(); return; }
                if (showMenu != null) showMenu.Invoke(tray, null); else menu.Show(Cursor.Position);
            };
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
            // The tray icon is lost when Explorer starts after this app or restarts. An elevated app does not
            // always get the TaskbarCreated message, so re-add the icon whenever the taskbar window changes.
            IntPtr bar = Native.FindWindow("Shell_TrayWnd", null);
            if (bar != IntPtr.Zero && bar != trayTaskbar)
            {
                tray.Visible = false;
                tray.Visible = true;
                trayTaskbar = bar;
            }
            var latest = monitor.Latest;
            if (latest != gameShown) { gameShown = latest; game.SetValues(latest); }
            game.Track(settings.GameOverlay);
            ScanGames();
            monitor.Watchdog();
            if (!settings.Overlay) { if (overlay.Visible) overlay.Hide(); return; }
            var s = monitor.Latest;
            if (s != shown) { shown = s; overlay.SetValues(s); }
            overlay.Track_();
        }

        void Pick(int scene)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { monitor.SetScene(scene); }
                catch (Exception e)
                {
                    Log.Write("set scene " + scene + ": " + e.Message);
                    ui.Post(__ => tray.ShowBalloonTip(5000, "切换失败", e.Message, ToolTipIcon.Error), null);
                }
            });
        }

        void OpenCustom(int index)
        {
            if (customForm != null && !customForm.IsDisposed) { customForm.Activate(); return; }
            var copies = new Profile[Monitor.CustomCount];
            for (int i = 0; i < copies.Length; i++) copies[i] = settings.Custom[i].Clone();
            customForm = new CustomForm(copies, index);
            customForm.Saved += (profiles, switchTo) =>
            {
                monitor.SetCustom(profiles);
                for (int i = 0; i < profiles.Length; i++)
                {
                    sceneItems[Monitor.SceneCustom + i].Text = CustomPrefix + profiles[i].Name;
                    autoGameMenu.DropDownItems[Monitor.SceneCustom + i].Text = CustomPrefix + profiles[i].Name;
                }
                int active = monitor.Scene;
                // Apply the new settings: the chosen tab, or the active custom scene if it was edited.
                if (switchTo >= 0) Pick(Monitor.SceneCustom + switchTo);
                else if (active >= Monitor.SceneCustom) Pick(active);
                ShowMode(monitor.CurrentMode);
            };
            customForm.Show();
            customForm.Activate();
        }

        void ShowMode(int mode)
        {
            int scene = monitor.Scene;
            for (int m = 1; m <= Monitor.SceneLast; m++) sceneItems[m].Checked = m == scene;
            officialMenu.Checked = scene >= 1 && scene <= 3;
            using (var bmp = ModeIcon.Draw(mode, SystemInformation.SmallIconSize.Height))
            {
                IntPtr h = bmp.GetHicon();
                using (var icon = Icon.FromHandle(h)) tray.Icon = icon;
                if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
                iconHandle = h;
            }
            string name = scene >= Monitor.SceneCustom && scene <= Monitor.SceneLast ? CustomPrefix + settings.Custom[scene - Monitor.SceneCustom].Name : Names[scene >= 1 && scene <= 3 ? scene : mode];
            tray.Text = "xingyao-perf: " + name;
        }

        void ToggleOverlay()
        {
            settings.Overlay = !settings.Overlay;
            overlayItem.Checked = settings.Overlay;
            settings.Save();
        }

        static void Check(ToolStripMenuItem parent, int value)
        {
            foreach (ToolStripMenuItem i in parent.DropDownItems) i.Checked = (int)i.Tag == value;
        }

        void RefreshCaps()
        {
            // Show the user's choice. The power plan value itself is rewritten by other software (see docs/protocol.md).
            Check(cpuMenu, settings.CpuState);
            gpuMenu.Enabled = monitor.GpuCapAvailable;
            foreach (ToolStripMenuItem i in gpuMenu.DropDownItems) i.Visible = (int)i.Tag == 0 || (int)i.Tag < monitor.GpuHwMaxMhz;
            Check(gpuMenu, settings.GpuMaxMhz);
        }

        void PickCpuCap(int mhz)
        {
            try { monitor.SetCpuState(mhz); Check(cpuMenu, mhz); }
            catch (Exception e)
            {
                Log.Write("set cpu cap " + mhz + ": " + e.Message);
                tray.ShowBalloonTip(5000, "设置 CPU 最高频率失败", e.Message, ToolTipIcon.Error);
            }
        }

        void PickFan(int level)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { monitor.SetFan(level); ui.Post(__ => Check(fanMenu, level), null); }
                catch (Exception e)
                {
                    Log.Write("set fan " + level + ": " + e.Message);
                    ui.Post(__ => tray.ShowBalloonTip(5000, "设置风扇失败", e.Message, ToolTipIcon.Error), null);
                }
            });
        }

        void PickGpuCap(int mhz)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { monitor.SetGpuMax(mhz); ui.Post(__ => Check(gpuMenu, mhz), null); }
                catch (Exception e)
                {
                    Log.Write("set gpu cap " + mhz + ": " + e.Message);
                    ui.Post(__ => tray.ShowBalloonTip(5000, "设置 GPU 最高频率失败", e.Message, ToolTipIcon.Error), null);
                }
            });
        }

        void ShowCharge(int limit)
        {
            for (int i = 0; i < 3; i++) chargeItems[i].Checked = ChargeLimits[i] == limit;
        }

        void RefreshCharge()
        {
            if (!Platform.FanReadAllowed) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { int limit = monitor.ReadChargeLimit(); ui.Post(__ => ShowCharge(limit), null); }
                catch (Exception e) { Log.Write("read charge limit: " + e.Message); ui.Post(__ => ShowCharge(-1), null); }
            });
        }

        void PickCharge(int limit)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    monitor.SetChargeLimit(limit);
                    ui.Post(__ => { ShowCharge(limit); tray.ShowBalloonTip(3000, "充电上限", limit == 0 ? "已关闭，会充到 100%" : "已设为 " + limit + "%，关机后也保持", ToolTipIcon.Info); }, null);
                }
                catch (Exception e)
                {
                    Log.Write("set charge limit " + limit + ": " + e.Message);
                    ui.Post(__ => { RefreshCharge(); tray.ShowBalloonTip(5000, "设置充电上限失败", e.Message, ToolTipIcon.Error); }, null);
                }
            });
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

        string SceneName(int scene)
        {
            return scene >= Monitor.SceneCustom ? CustomPrefix + settings.Custom[scene - Monitor.SceneCustom].Name : Names[scene];
        }

        void PickAutoGame(int scene)
        {
            settings.AutoGame = scene;
            Check(autoGameMenu, scene);
            settings.Save();
            if (scene == 0 && autoPrev != 0) { RestoreAfterGame(); }
        }

        void RestoreAfterGame()
        {
            if (monitor.Scene == autoSet) Pick(autoPrev); // not if the user chose another scene meanwhile
            autoPrev = 0;
        }

        // Every 3 s on a worker thread: is a game running? Switch when one starts; switch back 6 s after the last one ends.
        void ScanGames()
        {
            if (settings.AutoGame == 0 && autoPrev == 0) return;
            if (++scanTick < 6 || scanBusy) return;
            scanTick = 0;
            scanBusy = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool running = game.AnyGameRunning();
                ui.Post(__ =>
                {
                    scanBusy = false;
                    if (running)
                    {
                        gameGone = 0;
                        int want = settings.AutoGame;
                        if (want != 0 && autoPrev == 0 && monitor.Scene != want)
                        {
                            autoPrev = monitor.Scene; autoSet = want;
                            Log.Write("game started: scene " + autoPrev + " -> " + want);
                            Pick(want);
                        }
                    }
                    else if (autoPrev != 0 && ++gameGone >= 2)
                    {
                        Log.Write("game ended: back to scene " + autoPrev);
                        RestoreAfterGame();
                    }
                }, null);
            });
        }

        // Only runs when the user picks the menu item. Asks before downloading.
        void CheckUpdate()
        {
            if (updating) return;
            updating = true;
            updateItem.Enabled = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Updater.Release r = null;
                string err = null;
                try { r = Updater.Latest(); }
                catch (Exception e) { err = e.Message; Log.Write("update check: " + e.Message); }
                ui.Post(__ =>
                {
                    if (err != null) { EndUpdate(); tray.ShowBalloonTip(5000, "检查更新失败", err, ToolTipIcon.Error); return; }
                    if (r == null || !Updater.IsNewer(r)) { EndUpdate(); tray.ShowBalloonTip(4000, "检查更新", "已经是最新版本 " + AppVersion.Text, ToolTipIcon.Info); return; }
                    string notes = string.IsNullOrEmpty(r.Notes) ? "（没有更新说明）" : r.Notes.Length > 1500 ? r.Notes.Substring(0, 1500) + "…" : r.Notes;
                    var ask = MessageBox.Show("发现新版本 " + r.Tag + "（当前 " + AppVersion.Text + "）。\n\n更新内容：\n" + notes + "\n\n现在下载并更新吗？更新后程序会自动重启。", "xingyao-perf",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1, (MessageBoxOptions)0x40000);
                    if (ask != DialogResult.Yes) { EndUpdate(); return; }
                    tray.ShowBalloonTip(3000, "正在更新", "下载 " + r.Tag + " …", ToolTipIcon.Info);
                    ThreadPool.QueueUserWorkItem(___ =>
                    {
                        try
                        {
                            string exe = Updater.Install(r);
                            Log.Write("updated to " + r.Tag);
                            ui.Post(____ => { Updater.RestartLater(exe); Quit(); }, null);
                        }
                        catch (Exception e)
                        {
                            Log.Write("update install: " + e.Message);
                            ui.Post(____ => { EndUpdate(); tray.ShowBalloonTip(6000, "更新失败", e.Message + "\n没有改动当前版本。", ToolTipIcon.Error); }, null);
                        }
                    });
                }, null);
            });
        }

        void EndUpdate() { updating = false; updateItem.Enabled = true; }

        void PickGame(int when)
        {
            settings.GameOverlay = when;
            for (int i = 0; i < 3; i++) gameItems[i].Checked = i == when;
            settings.Save();
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
            game.Dispose();
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
                // Let Explorer's TaskbarCreated broadcast reach this elevated process, so the tray icon comes back.
                Native.ChangeWindowMessageFilter(Native.RegisterWindowMessage("TaskbarCreated"), 1);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
            return 0;
        }

        // Live values, then a hot example, on dark and light taskbars, followed by the three mode icons.
        static void SavePreview(string file, Snapshot live, bool compact)
        {
            var hot = new Snapshot { CpuPct = 100, CpuGhz = 3.9, GpuPct = 96, PGhz = 4.2, EGhz = 3.8, LGhz = 2.1, PkgW = 52.3, GpuW = 22.4, TempC = 91, GpuMhz = 2100, SysW = 68.4, MemPct = 71, NetIn = 2.5e6, NetOut = 2e5, Fan0 = 6900, Fan1 = 4800 };
            var warm = new Snapshot { CpuPct = 76, CpuGhz = 3.0, GpuPct = 41, PGhz = 3.1, EGhz = 2.7, LGhz = 1.9, PkgW = 42.1, GpuW = 12.4, TempC = 83, GpuMhz = 1450, MemPct = 58, NetIn = 1e6, NetOut = 1e5, Fan0 = 5100, Fan1 = 3000 };
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
                        // A minute of made-up history, so the graphs show a curve.
                        for (int j = 0; j < 59; j++)
                        {
                            double wave = 0.5 + 0.5 * Math.Sin(j / 6.0 + i);
                            o.SetValues(new Snapshot { CpuPct = (rows[i].CpuPct + 5) * (0.3 + 0.7 * wave), GpuPct = (rows[i].GpuPct + 5) * wave, MemPct = 55 + 10 * wave, PGhz = rows[i].PGhz * (0.4 + 0.6 * wave), EGhz = rows[i].EGhz * (0.4 + 0.6 * wave), LGhz = rows[i].LGhz * (0.4 + 0.6 * wave), GpuMhz = rows[i].GpuMhz * (0.4 + 0.6 * wave), TempC = rows[i].TempC - 15 * (1 - wave), Fan0 = (int)(rows[i].Fan0 * (0.5 + 0.5 * wave)), Fan1 = (int)(rows[i].Fan1 * (0.5 + 0.5 * wave)), NetIn = 3e6 * wave * wave, NetOut = 4e5 * (1 - wave) });
                        }
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
                    sb.AppendLine(string.Format("sample {0} ({1} ms): CPU {11}% GPU {12}%, P {2} E {3} L {4} GHz, pkg {5} W, {6} C, GPU {7} MHz {8} W, fans {9}/{10}, speed {13} GHz, memory {14}%, net in {15} out {16} B/s",
                        i, sw.ElapsedMilliseconds - t0, Overlay.Num(last.PGhz, "0.00"), Overlay.Num(last.EGhz, "0.00"), Overlay.Num(last.LGhz, "0.00"), Overlay.Num(last.PkgW, "0.0"),
                        Overlay.Num(last.TempC, "0"), Overlay.Num(last.GpuMhz, "0"), Overlay.Num(last.GpuW, "0.0"), last.Fan0, last.Fan1, Overlay.Num(last.CpuPct, "0"), Overlay.Num(last.GpuPct, "0"), Overlay.Num(last.CpuGhz, "0.00"), Overlay.Num(last.MemPct, "0"), Overlay.Num(last.NetIn, "0"), Overlay.Num(last.NetOut, "0")));
                    Thread.Sleep(1000);
                }
                me.Refresh();
                sb.AppendLine("CPU per sample: " + ((me.TotalProcessorTime - cpu0).TotalMilliseconds / N).ToString("0.0") + " ms");
                sb.AppendLine("Private memory: " + (me.PrivateMemorySize64 / 1024 / 1024) + " MB, working set " + (me.WorkingSet64 / 1024 / 1024) + " MB");
                try
                {
                    SavePreview(path + ".png", last, false);
                    SavePreview(path + ".compact.png", last, true);
                    using (var form = new CustomForm(new[] { Profile.Default(1), Profile.Default(2), Profile.Default(3), Profile.Default(4), Profile.Default(5) }, 0))
                    {
                        // Shown off screen for a moment, because a hidden form does not paint its controls.
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-5000, -5000);
                        form.ShowInTaskbar = false;
                        form.Show();
                        Application.DoEvents();
                        using (var bmp = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                            bmp.Save(path + ".custom.png", System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
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
                    sb.AppendLine("CPU max state (power plan): " + CpuCap.ReadPercent() + "%");
                    sb.AppendLine("GPU frequency control: " + m.GpuCapAvailable + ", hardware max " + m.GpuHwMaxMhz + " MHz");
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
