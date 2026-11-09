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
                File.WriteAllText(FilePath, "mode=" + Mode + "\noverlay=" + (Overlay ? "1" : "0") + "\n");
            }
            catch (Exception e) { Log.Write("save settings: " + e.Message); }
        }
    }

    // Samples sensors on a background thread so slow WMI or EC calls never block the UI.
    internal sealed class Monitor : IDisposable
    {
        const int IntervalMs = 2000, ModeEveryTicks = 3, RestoreWindowSeconds = 60, SleepGapSeconds = 30;

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
                    catch (Exception e) { Log.Write("temperature: " + e.Message); tempPauseTicks = 30; }
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
                        if (++fanFailures == 5) { Log.Write("fan read pausing: " + e.Message); fanPauseTicks = 15; fanFailures = 0; }
                    }
                }
            }
            return s;
        }

        void Run()
        {
            InitSensors();
            int tick = 0;
            var last = DateTime.UtcNow;
            while (running)
            {
                var now = DateTime.UtcNow;
                if ((now - last).TotalSeconds > SleepGapSeconds) restoreUntil = now.AddSeconds(RestoreWindowSeconds); // woke from sleep
                last = now;
                if (tick++ % ModeEveryTicks == 0) CheckMode();
                latest = SampleOnce();
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

        public void Dispose()
        {
            running = false;
            if (thread.IsAlive) thread.Join(5000);
            if (counters != null) counters.Dispose();
            if (fans != null) fans.Dispose();
            if (gpu != null) gpu.Dispose();
        }
    }
}
