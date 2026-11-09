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
        public int GpuMaxMhz; // 0 = no cap; reapplied because the driver may reset it
        public int CpuMaxMhz; // old frequency cap; only read so it can be cleared once
        public int CpuState = 100; // maximum processor state in percent; reapplied in case the plan changes
        public bool FanQuiet; // quiet fan mode: fans stop while cool, the EC curve otherwise
        public int GameOverlay = 1; // 0 off, 1 listed games only, 2 any fullscreen app (and listed games)
        public string Games = ""; // extra game process names without .exe, separated by |, beyond the ones Windows recognises

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var kv = line.Split('=');
                    if (kv.Length != 2) continue;
                    if (kv[0] == "mode") int.TryParse(kv[1], out s.Mode);
                    else if (kv[0] == "overlay") s.Overlay = kv[1] != "0";
                    else if (kv[0] == "avoidweather") s.AvoidWeather = kv[1] != "0";
                    else if (kv[0] == "fanquiet") s.FanQuiet = kv[1] == "1";
                    else if (kv[0] == "gameoverlay") { int.TryParse(kv[1], out s.GameOverlay); if (s.GameOverlay < 0 || s.GameOverlay > 2) s.GameOverlay = 1; }
                    else if (kv[0] == "games") s.Games = kv[1];
                    else if (kv[0] == "gpumax") int.TryParse(kv[1], out s.GpuMaxMhz);
                    else if (kv[0] == "cpumax") int.TryParse(kv[1], out s.CpuMaxMhz);
                    else if (kv[0] == "cpustate") { int.TryParse(kv[1], out s.CpuState); if (s.CpuState < 50 || s.CpuState > 100) s.CpuState = 100; }
                    else if (kv[0] == "layout") { int.TryParse(kv[1], out s.Layout); if (s.Layout < 0 || s.Layout > 2) s.Layout = 0; }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (s.Mode < 1 || s.Mode > 3) s.Mode = 0;
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Log.Dir);
                File.WriteAllText(FilePath, "mode=" + Mode + "\noverlay=" + (Overlay ? "1" : "0") + "\navoidweather=" + (AvoidWeather ? "1" : "0") + "\nlayout=" + Layout + "\ngameoverlay=" + GameOverlay + "\ngames=" + Games + "\ngpumax=" + GpuMaxMhz + "\ncpumax=" + CpuMaxMhz + "\ncpustate=" + CpuState + "\nfanquiet=" + (FanQuiet ? "1" : "0") + "\n");
            }
            catch (Exception e) { Log.Write("save settings: " + e.Message); }
        }
    }

    // Samples sensors on a background thread so slow WMI or EC calls never block the UI.
    internal sealed class Monitor : IDisposable
    {
        // One sample a second, plus the time the sample takes. Other checks count samples, so they are in seconds.
        const int IntervalMs = 1000, ModeEveryTicks = 6, RestoreWindowSeconds = 60, SleepGapSeconds = 30;

        public readonly Firmware Firmware = new Firmware();
        readonly Settings settings;
        readonly Thread thread;
        volatile bool running = true;
        volatile Snapshot latest = new Snapshot();
        readonly object modeGate = new object();
        DateTime restoreUntil;

        CounterSet counters; FanReader fans; GpuFreq gpu; PackageTemp temp;
        int currentMode, fanFailures, fanPauseTicks, tempPauseTicks;
        public static bool SkipGpu; // --no-gpu: the frequency library is the largest memory user.

        public event Action<int> ModeChanged;
        public event Action<string> FanReleased; // manual fan speed was handed back to the EC, with the reason

        // Manual fan percent, 0 for the EC curve. Not saved: every start begins on the EC curve.
        const double FanSafeTempC = 85;
        volatile int fanPercent;
        int fanUnknownTemp;
        // The menu shows -1 for the quiet mode.
        public const int FanQuietLevel = -1;
        public int FanPercent { get { return fanQuiet ? FanQuietLevel : fanPercent; } }

        // Quiet mode: the fans stop once the package has been cool and lightly loaded for a while, and go back
        // to the EC curve the moment it warms up. The package sensor jumps by 20 C from one second to the next
        // at idle, so stopping uses 30-second averages and going back a 5-second average, with a single-reading
        // limit and a power limit as backstops.
        const double QuietOffC = 50, QuietOffW = 10, QuietBackC = 55, QuietBackSpikeC = 75, QuietBackW = 15;
        const int QuietWindow = 30, QuietBackWindow = 5, StallSeconds = 5;
        volatile bool fanQuiet, fanStopped;
        int quietCool, quietPower; // quietCool: samples since the quiet logic last changed state
        readonly double[] quietTemps = new double[QuietWindow], quietPowers = new double[QuietWindow];
        int quietNext;
        DateTime lastSampleAt = DateTime.UtcNow;
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
            if (fans != null) try { fans.SetFanAuto(); } catch (Exception e) { Log.Write("fan auto at start: " + e.Message); }
            fanQuiet = fans != null && settings.FanQuiet; // the quiet mode is kept across restarts; it starts on the EC curve
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
                else if (fanQuiet) QuietFan(latest);
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
                    else { settings.Mode = mode; settings.Save(); } // Changed elsewhere, such as the Fn key.
                }
                else if (settings.Mode == 0) { settings.Mode = mode; settings.Save(); }
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

        // Called from a worker thread when the user picks a mode. Throws if the write is not confirmed.
        public void SetMode(int mode)
        {
            lock (modeGate)
            {
                Firmware.SetMode(mode);
                settings.Mode = mode;
                settings.Save();
            }
            Publish(mode);
        }

        // 0 hands the fans back to the EC curve, FanQuietLevel picks the quiet mode. Called from a worker thread.
        public void SetFan(int percent)
        {
            var f = fans;
            if (f == null) throw new InvalidOperationException("EC 端口不可用");
            bool quiet = percent == FanQuietLevel;
            if (settings.FanQuiet != quiet) { settings.FanQuiet = quiet; settings.Save(); }
            fanQuiet = false;
            quietCool = 0;
            if (percent <= 0)
            {
                f.SetFanAuto(); fanPercent = 0; fanStopped = false;
                fanQuiet = quiet;
                Log.Write(quiet ? "fan quiet mode" : "fan auto");
                return;
            }
            f.SetFanPercent(percent);
            fanStopped = false;
            fanPercent = percent;
            fanUnknownTemp = 0;
            Log.Write("fan manual " + percent + "%");
        }

        void ReleaseFan(string why)
        {
            try { SetFan(0); } catch (Exception e) { Log.Write("fan release failed: " + e.Message); }
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

        // Mean of the newest n samples in a ring buffer; NaN if any is missing.
        double Recent(double[] ring, int n)
        {
            double sum = 0;
            for (int k = 1; k <= n; k++)
            {
                double x = ring[(quietNext - k + ring.Length) % ring.Length];
                if (double.IsNaN(x)) return double.NaN;
                sum += x;
            }
            return sum / n;
        }

        void QuietFan(Snapshot s)
        {
            var f = fans;
            if (f == null) return;
            double t = s.TempC, w = s.PkgW;
            quietTemps[quietNext] = t; quietPowers[quietNext] = w;
            quietNext = (quietNext + 1) % QuietWindow;
            quietCool++;

            if (fanStopped)
            {
                string why = null;
                double recent = Recent(quietTemps, QuietBackWindow);
                if (double.IsNaN(t)) why = "读不到温度";
                else if (t >= QuietBackSpikeC) why = "温度 " + t.ToString("0") + " °C";
                else if (recent >= QuietBackC) why = "5 秒平均温度 " + recent.ToString("0") + " °C";
                else if (!double.IsNaN(w) && w >= QuietBackW) { if (++quietPower >= 2) why = "功耗 " + w.ToString("0") + " W"; }
                else quietPower = 0;
                if (why != null) QuietBack(why);
                return;
            }
            if (quietCool < QuietWindow) return; // a full window since start or since the fans last came back
            double avgT = Recent(quietTemps, QuietWindow), avgW = Recent(quietPowers, QuietWindow);
            if (double.IsNaN(avgT) || double.IsNaN(avgW) || avgT > QuietOffC || avgW > QuietOffW) return;
            try { f.SetFanOff(); fanStopped = true; quietPower = 0; quietCool = 0; Log.Write("quiet: fans stopped, 30 s average " + avgT.ToString("0") + " C, " + avgW.ToString("0.0") + " W"); }
            catch (Exception e) { Log.Write("quiet: stop failed: " + e.Message); }
        }

        // Back to the EC curve while staying in quiet mode; the cool period starts over.
        void QuietBack(string why)
        {
            var f = fans;
            try { if (f != null) f.SetFanAuto(); } catch (Exception e) { Log.Write("quiet: back to EC failed: " + e.Message); return; }
            fanStopped = false; quietCool = 0; quietPower = 0;
            Log.Write("quiet: fans back to EC (" + why + ")");
        }

        // Called on the UI timer. If sampling stalls while the fans are stopped, give them back to the EC from here.
        public void Watchdog()
        {
            if (fanStopped && (DateTime.UtcNow - lastSampleAt).TotalSeconds > StallSeconds) QuietBack("采样停止");
        }

        // Before sleep the EC must own the fans again. Quiet mode stays on and starts over after waking.
        public void OnSuspend()
        {
            if (fanPercent > 0) ReleaseFan("睡眠");
            if (fanStopped) QuietBack("睡眠");
        }

        // GPU frequency cap. The driver can drop it after a restart or driver update, so it is checked a few seconds after start and after waking.
        public bool GpuCapAvailable { get { var g = gpu; return g != null && g.CanControl; } }
        public double GpuHwMaxMhz { get { var g = gpu; return g == null ? 0 : g.HwMaxMhz; } }

        public void SetGpuMax(int mhz)
        {
            var g = gpu;
            if (g == null) throw new InvalidOperationException("GPU 频率接口不可用");
            g.SetMaxMhz(mhz);
            settings.GpuMaxMhz = mhz;
            settings.Save();
            Log.Write("gpu max set to " + mhz);
        }

        public void SetCpuState(int percent)
        {
            CpuCap.SetPercent(percent);
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
                if (settings.CpuState == 100) return; // the default is left to Windows and other software
                int now = CpuCap.ReadPercent();
                if (now != settings.CpuState) { CpuCap.SetPercent(settings.CpuState); Log.Write("cpu max state reapplied " + settings.CpuState + " (was " + now + ")"); }
            }
            catch (Exception e) { Log.Write("cpu cap: " + e.Message); }
        }

        void KeepGpuCap()
        {
            var g = gpu;
            if (g == null || !g.CanControl || settings.GpuMaxMhz <= 0) return;
            try
            {
                double min, max;
                g.GetRange(out min, out max);
                if (Math.Abs(max - Math.Max(GpuFreq.LowestCapMhz, Math.Min(settings.GpuMaxMhz, g.HwMaxMhz))) > 1) { g.SetMaxMhz(settings.GpuMaxMhz); Log.Write("gpu max reapplied " + settings.GpuMaxMhz); }
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
            if (fanPercent > 0 || fanStopped) try { fans.SetFanAuto(); fanPercent = 0; fanStopped = false; Log.Write("fan auto at exit"); } catch (Exception e) { Log.Write("fan auto at exit: " + e.Message); }
            if (thread.IsAlive) thread.Join(5000);
            if (counters != null) counters.Dispose();
            if (fans != null) fans.Dispose();
            if (gpu != null) gpu.Dispose();
        }
    }
}
