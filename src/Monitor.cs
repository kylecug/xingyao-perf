using System;
using System.IO;
using System.Threading;

namespace XingyaoPerf
{
    internal static class Log
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xingyao-perf");
        public static void Write(string msg)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string f = Path.Combine(Dir, "log.txt");
                if (File.Exists(f) && new FileInfo(f).Length > 256 * 1024) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + Environment.NewLine);
            }
            catch { }
        }
    }

    internal sealed class Settings
    {
        static readonly string FilePath = Path.Combine(Log.Dir, "settings.ini");
        public int Mode;
        public bool Overlay = true;
        public bool AvoidWeather = true;
        public int Layout; // 0 auto, 1 full, 2 compact
        public int Scene; // 1-3 official modes, 4-6 the custom scenes; 0 until known
        public Profile[] Custom = { Profile.Default(1), Profile.Default(2), Profile.Default(3), Profile.Default(4), Profile.Default(5) };
        public int GpuMaxMhz; // GPU menu choice in MHz, 0 = auto (follows the scene); reapplied because the driver may reset it
        public int CpuMaxMhz; // old frequency cap; only read so it can be cleared once
        public int CpuState; // CPU menu choice: maximum processor state in percent, 0 = auto (no limit); reapplied in case the plan changes
        public int GameOverlay = 1; // 0 off, 1 listed games only, 2 any fullscreen app (and listed games)
        public string Games = ""; // extra game process names without .exe, separated by |, beyond the ones Windows recognises

        public static Settings Load()
        {
            var s = new Settings();
            bool fanQuiet = false;
            try
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var kv = line.Split('=');
                    if (kv.Length != 2) continue;
                    if (kv[0] == "mode") int.TryParse(kv[1], out s.Mode);
                    else if (kv[0] == "overlay") s.Overlay = kv[1] != "0";
                    else if (kv[0] == "avoidweather") s.AvoidWeather = kv[1] != "0";
                    else if (kv[0] == "scene") int.TryParse(kv[1], out s.Scene);
                    else if (kv[0].Length == 7 && kv[0].StartsWith("custom") && kv[0][6] >= '1' && kv[0][6] <= '5') s.Custom[kv[0][6] - '1'] = Profile.Parse(kv[1], kv[0][6] - '0');
                    else if (kv[0] == "fanquiet") fanQuiet = kv[1] == "1"; // before 0.9.16 the quiet mode was a fan setting
                    else if (kv[0] == "gameoverlay") { int.TryParse(kv[1], out s.GameOverlay); if (s.GameOverlay < 0 || s.GameOverlay > 2) s.GameOverlay = 1; }
                    else if (kv[0] == "games") s.Games = kv[1];
                    else if (kv[0] == "gpumax") int.TryParse(kv[1], out s.GpuMaxMhz);
                    else if (kv[0] == "cpumax") int.TryParse(kv[1], out s.CpuMaxMhz);
                    else if (kv[0] == "cpustate") { int.TryParse(kv[1], out s.CpuState); if (s.CpuState < 50 || s.CpuState >= 100) s.CpuState = 0; }
                    else if (kv[0] == "layout") { int.TryParse(kv[1], out s.Layout); if (s.Layout < 0 || s.Layout > 2) s.Layout = 0; }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (s.Mode < 1 || s.Mode > 3) s.Mode = 0;
            if (s.Scene < 1 || s.Scene > Monitor.SceneLast) s.Scene = fanQuiet ? Monitor.SceneCustom : s.Mode;
            if (s.GpuMaxMhz < 0) s.GpuMaxMhz = 0;
            return s;
        }

        string CustomLines()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Custom.Length; i++) sb.Append("\ncustom" + (i + 1) + "=" + Custom[i].Serialize());
            return sb.ToString();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Log.Dir);
                File.WriteAllText(FilePath, "mode=" + Mode + "\noverlay=" + (Overlay ? "1" : "0") + "\navoidweather=" + (AvoidWeather ? "1" : "0") + "\nlayout=" + Layout + "\ngameoverlay=" + GameOverlay + "\ngames=" + Games + "\ngpumax=" + GpuMaxMhz + "\ncpumax=" + CpuMaxMhz + "\ncpustate=" + CpuState + "\nscene=" + Scene + CustomLines() + "\n");
            }
            catch (Exception e) { Log.Write("save settings: " + e.Message); }
        }
    }

    // Samples sensors on a background thread so slow WMI or EC calls never block the UI.
    internal sealed class Monitor : IDisposable
    {
        // A 700 ms wait plus the 200-300 ms a sample takes gives about one sample a second.
        // Other checks count samples, so their counts are roughly seconds.
        const int IntervalMs = 700, ModeEveryTicks = 6, RestoreWindowSeconds = 60, SleepGapSeconds = 30;

        public readonly Firmware Firmware = new Firmware();
        readonly Settings settings;
        readonly Thread thread;
        volatile bool running = true;
        volatile Snapshot latest = new Snapshot();
        readonly object modeGate = new object();
        DateTime restoreUntil;

        CounterSet counters; FanReader fans; GpuFreq gpu; PackageTemp temp; BatteryPower battery;
        int currentMode, fanFailures, fanPauseTicks, tempPauseTicks;
        public static bool SkipGpu; // --no-gpu: the frequency library is the largest memory user.

        public event Action<int> ModeChanged;
        public event Action<string> FanReleased; // manual fan speed was handed back to the EC, with the reason

        // Scenes, the first group of the menu: the three official modes as they are (Windows on Balanced, no limits,
        // EC fans), and five custom scenes (scene 4-8), which set the official mode, Windows power mode, CPU and GPU
        // limits and a fan curve. They start as everyday quiet, full power and plain examples (see Profile).
        // Picking a scene puts the fan, CPU and GPU menus back on "auto", which follows it.
        public const int SceneCustom = 4, SceneLast = 8, CustomCount = SceneLast - SceneCustom + 1;
        public event Action SceneChanged;
        Profile CustomProfile { get { int s = settings.Scene; return s >= SceneCustom && s <= SceneLast ? settings.Custom[s - SceneCustom] : null; } }

        // Fan menu choice: FanAuto follows the scene, 0 is the EC curve, otherwise a manual percent.
        // Not saved: every start begins on auto. Manual percent is handed back (to auto) when hot.
        public const int FanAuto = -1;
        const double FanSafeTempC = 85;
        volatile int fanPercent, fanChoice = FanAuto;
        int fanUnknownTemp;
        public int FanChoice { get { return fanChoice; } }

        // Custom fan curve (see Profile): level i runs Pcts[i]; level 4 is the EC. The package sensor jumps by 20 C
        // from one second to the next at idle, so levels follow a 5-sample average: up as soon as it reaches a point,
        // down once it has been CurveDownC below the point for CurveDownSamples samples.
        const int CurveEcLevel = 4, CurveDownSamples = 20, CurveWindow = 5, StallSeconds = 5;
        const double CurveDownC = 3, CurveSpikeC = 5; // a single reading this far above the last point goes to the EC at once
        volatile Profile fanCurve; // set while the curve is running
        volatile bool curveHeld;   // the curve holds the fans (stopped or a manual speed)
        bool FanHeld { get { return curveHeld; } }
        int curveLevel = CurveEcLevel, curveCool;
        readonly double[] curveTemps = new double[CurveWindow];
        int curveNext;
        DateTime lastSampleAt = DateTime.UtcNow;
        int batteryPauseTicks;
        public Snapshot Latest { get { return latest; } }
        public int CurrentMode { get { return currentMode; } }

        public Monitor(Settings settings)
        {
            this.settings = settings;
            thread = new Thread(Run) { IsBackground = true, Name = "sensors", Priority = ThreadPriority.BelowNormal };
        }

        public void Start() { restoreUntil = DateTime.UtcNow.AddSeconds(RestoreWindowSeconds); thread.Start(); }

        // Used by --selftest to read everything once without the UI.
        public void InitSensors()
        {
            Try("counters", () => counters = new CounterSet());
            Try("package temperature", () => temp = new PackageTemp());
            if (!SkipGpu) Try("gpu frequency", () => gpu = new GpuFreq());
            Try("fan reader", () => fans = new FanReader());
            Try("battery power", () => battery = new BatteryPower());
        }

        static void Try(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Log.Write(what + ": " + e.GetType().Name + ": " + e.Message); }
        }

        public Snapshot SampleOnce()
        {
            var s = new Snapshot { Mode = currentMode };
            if (counters != null) try { counters.Sample(s); } catch (Exception e) { Log.Write("counters: " + e.Message); }
            if (temp != null)
            {
                if (tempPauseTicks > 0) tempPauseTicks--;
                else
                    try { s.TempC = temp.Read(); }
                    catch (Exception e) { Log.Write("temperature: " + e.Message); tempPauseTicks = 60; }
            }
            if (gpu != null) try { s.GpuMhz = gpu.ReadMhz(); } catch (Exception e) { Log.Write("gpu: " + e.Message); }
            if (battery != null)
            {
                if (batteryPauseTicks > 0) batteryPauseTicks--;
                else try { s.SysW = battery.Read(); } catch (Exception e) { Log.Write("battery power: " + e.Message); batteryPauseTicks = 60; }
            }
            if (fans != null)
            {
                if (fanPauseTicks > 0) fanPauseTicks--;
                else
                {
                    // Other software may use the same EC channel, so failures back off instead of retrying hard.
                    try { fans.Read(out s.Fan0, out s.Fan1); fanFailures = 0; }
                    catch (Exception e)
                    {
                        if (++fanFailures == 5) { Log.Write("fan read pausing: " + e.Message); fanPauseTicks = 30; fanFailures = 0; }
                    }
                }
            }
            return s;
        }

        void Run()
        {
            InitSensors();
            // A crash or power loss may have left a manual speed in the EC, so always start on the EC curve.
            // This is here, not in InitSensors, because --selftest must not write anything.
            // SetFan(FanAuto) sends D5 10 first, then follows the saved scene (a custom curve starts from the EC curve).
            if (fans != null) try { SetFan(FanAuto); } catch (Exception e) { Log.Write("fan auto at start: " + e.Message); }
            ApplyScenePowerMode();
            int tick = 0;
            var last = DateTime.UtcNow;
            bool checkCaps = true;
            while (running)
            {
                var now = DateTime.UtcNow;
                if ((now - last).TotalSeconds > SleepGapSeconds) { restoreUntil = now.AddSeconds(RestoreWindowSeconds); checkCaps = true; } // woke from sleep
                last = now;
                // The caps are only lost on a restart, driver reset or power plan change, so they are checked
                // a few seconds after start and after waking, not continuously.
                if (checkCaps && tick >= 3) { KeepGpuCap(); KeepCpuCap(); checkCaps = false; }
                if (tick++ % ModeEveryTicks == 0) CheckMode();
                latest = SampleOnce();
                lastSampleAt = DateTime.UtcNow;
                if (fanPercent > 0) KeepFan(latest);
                else if (fanCurve != null) CurveFan(latest);
                Thread.Sleep(IntervalMs);
            }
        }

        void CheckMode()
        {
            int mode;
            try { mode = Firmware.ReadMode(); }
            catch (Exception e)
            {
                if (currentMode != 0) Log.Write("mode read: " + e.Message);
                Publish(0);
                return;
            }
            lock (modeGate)
            {
                // Right after startup or wake, firmware or the vendor service may reset the mode, so put the user's choice back.
                if (settings.Mode != 0 && mode != settings.Mode)
                {
                    if (DateTime.UtcNow < restoreUntil && Platform.ModeWriteAllowed)
                    {
                        try { Firmware.SetMode(settings.Mode); mode = settings.Mode; Log.Write("restored mode " + mode); }
                        catch (Exception e) { Log.Write("restore failed: " + e.Message); }
                    }
                    else
                    {
                        // Changed elsewhere, such as the Fn key: that official mode becomes the scene.
                        settings.Mode = mode;
                        settings.Save();
                        Log.Write("mode changed elsewhere to " + mode);
                        if (settings.Scene != mode) ApplyScene(mode, false);
                    }
                }
                else if (settings.Mode == 0) { settings.Mode = mode; if (settings.Scene == 0) settings.Scene = mode; settings.Save(); }
            }
            Publish(mode);
        }

        void Publish(int mode)
        {
            if (mode == currentMode) return;
            currentMode = mode;
            var h = ModeChanged;
            if (h != null) h(mode);
        }

        int FirmwareModeFor(int scene) { return scene >= SceneCustom ? settings.Custom[scene - SceneCustom].Mode : scene; }

        // Called from a worker thread when the user picks a scene. Throws if the mode write is not confirmed.
        public void SetScene(int scene)
        {
            if (scene < 1 || scene > SceneLast) throw new ArgumentOutOfRangeException("scene");
            int mode = FirmwareModeFor(scene);
            lock (modeGate)
            {
                Firmware.SetMode(mode);
                settings.Mode = mode;
                ApplyScene(scene, true);
            }
            Publish(mode);
        }

        // Sets the Windows power mode, and puts the fan, CPU and GPU choices back on auto and applies them.
        // Each step logs its own failure so one broken part does not stop the rest.
        void ApplyScene(int scene, bool picked)
        {
            settings.Scene = scene;
            settings.CpuState = 0;
            settings.GpuMaxMhz = 0;
            settings.Save();
            Log.Write("scene " + scene + (picked ? "" : " (follows the mode)"));
            ApplyScenePowerMode();
            if (fans != null) try { SetFan(FanAuto); } catch (Exception e) { Log.Write("scene fan: " + e.Message); }
            try { CpuCap.SetPercent(CpuPercent); } catch (Exception e) { Log.Write("scene cpu: " + e.Message); }
            var g = gpu;
            if (g != null && g.CanControl) try { g.SetMaxMhz(GpuMhz); } catch (Exception e) { Log.Write("scene gpu: " + e.Message); }
            var h = SceneChanged;
            if (h != null) h();
        }

        public int Scene { get { return settings.Scene; } }
        int CpuPercent
        {
            get
            {
                if (settings.CpuState != 0) return settings.CpuState;
                var p = CustomProfile;
                return p == null || p.Cpu == 0 ? 100 : p.Cpu;
            }
        }
        int GpuMhz { get { var p = CustomProfile; return settings.GpuMaxMhz != 0 ? settings.GpuMaxMhz : p != null ? p.Gpu : 0; } }

        // Replaces the custom scenes from the settings window. If one of them is active it is applied again.
        public void SetCustom(Profile[] profiles)
        {
            lock (modeGate)
            {
                settings.Custom = profiles;
                settings.Save();
            }
            Log.Write("custom scenes saved");
        }

        // FanAuto follows the scene (a custom scene's fan curve, otherwise the EC curve), 0 is the EC curve,
        // otherwise a manual percent. Called from a worker thread.
        public void SetFan(int percent)
        {
            var f = fans;
            if (f == null) throw new InvalidOperationException("EC 端口不可用");
            var p = percent == FanAuto ? CustomProfile : null;
            fanChoice = percent;
            fanCurve = null; curveHeld = false; curveLevel = CurveEcLevel; curveCool = 0;
            if (percent <= 0)
            {
                f.SetFanAuto(); fanPercent = 0;
                fanCurve = p == null ? null : p.Clone();
                Log.Write(p != null ? "fan custom curve" : percent == FanAuto ? "fan auto (EC)" : "fan EC curve");
                return;
            }
            f.SetFanPercent(percent);
            fanPercent = percent;
            fanUnknownTemp = 0;
            Log.Write("fan manual " + percent + "%");
        }

        void ReleaseFan(string why)
        {
            try { SetFan(FanAuto); } catch (Exception e) { Log.Write("fan release failed: " + e.Message); }
            Log.Write("fan released: " + why);
            var h = FanReleased;
            if (h != null) h(why);
        }

        // While manual: hand back to the EC when hot or when the temperature cannot be read.
        // The EC keeps a manual speed on its own (held 60 s after one D6 in a test), so it is not re-sent.
        void KeepFan(Snapshot s)
        {
            if (double.IsNaN(s.TempC)) { if (++fanUnknownTemp >= 6) { ReleaseFan("读不到温度"); return; } }
            else fanUnknownTemp = 0;
            if (s.TempC >= FanSafeTempC) ReleaseFan("温度 " + s.TempC.ToString("0") + " °C");
        }

        // Mean of the last CurveWindow temperatures; NaN if any is missing.
        double RecentTemp()
        {
            double sum = 0;
            foreach (double x in curveTemps) { if (double.IsNaN(x)) return double.NaN; sum += x; }
            return sum / CurveWindow;
        }

        void CurveFan(Snapshot s)
        {
            var p = fanCurve;
            if (p == null || fans == null) return;
            curveTemps[curveNext] = s.TempC;
            curveNext = (curveNext + 1) % CurveWindow;
            double t = s.TempC, t5 = RecentTemp();
            if (double.IsNaN(t))
            {
                if (++fanUnknownTemp >= 6 && curveLevel != CurveEcLevel) CurveTo(CurveEcLevel, "读不到温度");
                return;
            }
            fanUnknownTemp = 0;
            if (t >= p.Temps[3] + CurveSpikeC) { if (curveLevel != CurveEcLevel) CurveTo(CurveEcLevel, "温度 " + t.ToString("0") + " °C"); curveCool = 0; return; }
            if (double.IsNaN(t5)) return; // the first few samples after a change
            int up = 0, down = 0;
            for (int i = 0; i < 4; i++)
            {
                if (t5 >= p.Temps[i]) up = i + 1;
                if (t5 >= p.Temps[i] - CurveDownC) down = i + 1;
            }
            if (up > curveLevel) { curveCool = 0; CurveTo(up, "5 秒平均温度 " + t5.ToString("0") + " °C"); }
            else if (down < curveLevel)
            {
                if (++curveCool >= CurveDownSamples) { curveCool = 0; CurveTo(down, "5 秒平均温度 " + t5.ToString("0") + " °C"); }
            }
            else curveCool = 0;
        }

        static int PctAt(Profile p, int level) { return level >= CurveEcLevel ? Profile.Ec : p.Pcts[level]; }

        void CurveTo(int level, string why)
        {
            var p = fanCurve;
            if (p == null) return;
            int pct = PctAt(p, level);
            // Neighbouring levels may share a speed; then only the level changes and nothing is sent.
            if (pct == PctAt(p, curveLevel)) { curveLevel = level; return; }
            try
            {
                if (pct == Profile.Ec) fans.SetFanAuto();
                else if (pct == 0) fans.SetFanOff();
                else fans.SetFanPercent(pct);
                curveHeld = pct != Profile.Ec;
                curveLevel = level;
                Log.Write("curve: " + (pct == Profile.Ec ? "EC" : pct == 0 ? "fans stopped" : pct + "%") + " (" + why + ")");
            }
            catch (Exception e) { Log.Write("curve: write failed: " + e.Message); }
        }

        // Back to the EC curve while keeping the scene; the curve takes over again once it has cooled.
        void CurveBack(string why)
        {
            var f = fans;
            try { if (f != null) f.SetFanAuto(); } catch (Exception e) { Log.Write("curve: back to EC failed: " + e.Message); return; }
            Log.Write("curve: fans back to EC (" + why + ")");
            curveHeld = false; curveLevel = CurveEcLevel; curveCool = 0;
        }

        // Windows keeps the power mode per power source, so it is applied again at start and when AC changes.
        public void ApplyScenePowerMode()
        {
            int scene = settings.Scene;
            if (scene == 0) return;
            var p = CustomProfile;
            int power = p == null ? Profile.PowerBalanced : p.Power;
            Guid want = power == Profile.PowerEfficiency ? PowerMode.BestEfficiency : power == Profile.PowerPerformance ? PowerMode.BestPerformance : PowerMode.Balanced;
            try
            {
                Guid now = PowerMode.Get();
                if (now == want) return;
                PowerMode.Set(want);
                Log.Write("power mode set to " + want + " (was " + now + ")");
            }
            catch (Exception e) { Log.Write("power mode: " + e.Message); }
        }

        // Called on the UI timer. If sampling stalls while the fans are stopped, give them back to the EC from here.
        public void Watchdog()
        {
            if (FanHeld && (DateTime.UtcNow - lastSampleAt).TotalSeconds > StallSeconds) CurveBack("采样停止");
        }

        // Before sleep the EC must own the fans again. The curve stays on and starts over after waking.
        public void OnSuspend()
        {
            if (fanPercent > 0) ReleaseFan("睡眠");
            if (FanHeld) CurveBack("睡眠");
        }

        // GPU frequency cap. The driver can drop it after a restart or driver update, so it is checked a few seconds after start and after waking.
        public bool GpuCapAvailable { get { var g = gpu; return g != null && g.CanControl; } }
        public double GpuHwMaxMhz { get { var g = gpu; return g == null ? 0 : g.HwMaxMhz; } }

        // GPU menu choice in MHz, 0 for auto.
        public void SetGpuMax(int mhz)
        {
            var g = gpu;
            if (g == null) throw new InvalidOperationException("GPU 频率接口不可用");
            int old = settings.GpuMaxMhz;
            settings.GpuMaxMhz = mhz;
            try { g.SetMaxMhz(GpuMhz); }
            catch { settings.GpuMaxMhz = old; throw; }
            settings.Save();
            Log.Write("gpu max set to " + mhz + " (applied " + GpuMhz + ")");
        }

        // CPU menu choice in percent, 0 for auto (no limit).
        public void SetCpuState(int percent)
        {
            CpuCap.SetPercent(percent == 0 ? 100 : percent);
            settings.CpuState = percent;
            settings.Save();
            Log.Write("cpu max state set to " + percent);
        }

        void KeepCpuCap()
        {
            try
            {
                if (settings.CpuMaxMhz != 0)
                {
                    CpuCap.ClearFrequencyCap();
                    Log.Write("cleared old cpu frequency cap " + settings.CpuMaxMhz);
                    settings.CpuMaxMhz = 0;
                    settings.Save();
                }
                int want = CpuPercent;
                if (want == 100) return; // the default is left to Windows and other software
                int now = CpuCap.ReadPercent();
                if (now != want) { CpuCap.SetPercent(want); Log.Write("cpu max state reapplied " + want + " (was " + now + ")"); }
            }
            catch (Exception e) { Log.Write("cpu cap: " + e.Message); }
        }

        void KeepGpuCap()
        {
            var g = gpu;
            int want = GpuMhz;
            if (g == null || !g.CanControl || want <= 0) return;
            try
            {
                double min, max;
                g.GetRange(out min, out max);
                if (Math.Abs(max - Math.Max(GpuFreq.LowestCapMhz, Math.Min(want, g.HwMaxMhz))) > 1) { g.SetMaxMhz(want); Log.Write("gpu max reapplied " + want); }
            }
            catch (Exception e) { Log.Write("gpu cap: " + e.Message); }
        }

        // Charge limit in percent, 0 when off. Called from a worker thread; the EC keeps the setting itself.
        public int ReadChargeLimit()
        {
            var f = fans;
            if (f == null) throw new InvalidOperationException("EC 端口不可用");
            var v = f.ReadCharge();
            return v[0] == 1 ? v[1] : 0;
        }

        // 0 writes back the factory state (off, 0, 0). 60 and 80 stop charging there and never force a discharge.
        public void SetChargeLimit(int limit)
        {
            if (limit != 0 && limit != 60 && limit != 80) throw new ArgumentOutOfRangeException("limit");
            var f = fans;
            if (f == null) throw new InvalidOperationException("EC 端口不可用");
            if (limit == 0) f.WriteCharge(0, 0, 0); else f.WriteCharge(1, limit, 100);
            Log.Write("charge limit set to " + limit);
        }

        public void Dispose()
        {
            running = false;
            if (fanPercent > 0 || FanHeld) try { fanCurve = null; fans.SetFanAuto(); fanPercent = 0; curveHeld = false; Log.Write("fan auto at exit"); } catch (Exception e) { Log.Write("fan auto at exit: " + e.Message); }
            if (thread.IsAlive) thread.Join(5000);
            if (counters != null) counters.Dispose();
            if (fans != null) fans.Dispose();
            if (gpu != null) gpu.Dispose();
        }
    }
}
