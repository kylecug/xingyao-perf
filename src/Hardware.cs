using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace XingyaoPerf
{
    internal static class Platform
    {
        public const string Board = "XINGYAO Series-P916F-PTL";
        public static readonly string BoardFound, BiosFound;

        static Platform()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
            {
                BoardFound = k == null ? "" : ((k.GetValue("BaseBoardProduct") as string) ?? "").Trim();
                BiosFound = k == null ? "" : ((k.GetValue("BIOSVersion") as string) ?? "").Trim();
            }
        }

        // Mode writes are only proven on these firmware versions. A BIOS update turns them off until re-verified.
        public static bool ModeWriteAllowed
        {
            get { return BoardFound == Board && (BiosFound == "1.00" || BiosFound == "1.01"); }
        }
        public static bool FanReadAllowed { get { return BoardFound == Board && BiosFound == "1.01"; } }
    }

    // Performance mode through root\WMI OemWMIMethod.OemWMIfun. Needs administrator rights.
    internal sealed class Firmware
    {
        public const int Office = 1, Balanced = 2, Turbo = 3;
        private readonly object gate = new object();
        private ManagementObject inst;

        private byte[] Call(params byte[] cmd)
        {
            lock (gate)
            {
                if (inst == null)
                {
                    using (var c = new ManagementClass(@"root\WMI", "OemWMIMethod", null))
                    using (var all = c.GetInstances())
                        foreach (ManagementObject m in all) { inst = m; break; }
                    if (inst == null) throw new InvalidOperationException("OemWMIMethod has no instance");
                }
                var buf = new byte[64];
                Array.Copy(cmd, buf, cmd.Length);
                using (var input = inst.GetMethodParameters("OemWMIfun"))
                {
                    input["u8Input"] = buf;
                    using (var output = inst.InvokeMethod("OemWMIfun", input, null))
                        return (byte[])output["u8Output"];
                }
            }
        }

        public int ReadMode()
        {
            var o = Call(3, 9);
            if (o[0] != 0 || o[1] < 1 || o[1] > 3) throw new InvalidOperationException("unexpected mode reply " + o[0] + "," + o[1]);
            return o[1];
        }

        public void SetMode(int mode)
        {
            if (mode < 1 || mode > 3) throw new ArgumentOutOfRangeException("mode");
            if (!Platform.ModeWriteAllowed) throw new InvalidOperationException("this BIOS is not verified for mode writes");
            var o = Call(3, 10, (byte)mode);
            if (o[0] != 0x93 + mode) throw new InvalidOperationException("unexpected ack 0x" + o[0].ToString("X2"));
            var sw = Stopwatch.StartNew();
            while (ReadMode() != mode)
            {
                if (sw.ElapsedMilliseconds > 1000) throw new InvalidOperationException("mode read-back did not match");
                Thread.Sleep(20);
            }
        }
    }

    // Read-only PM2 EC queries for fan RPM, through the inpoutx64 driver already installed on this machine.
    // Port protocol from Kurisu934/FanControl.MechrevoPtl (MIT), validated there on P916F-PTL BIOS 1.01.
    internal sealed class FanReader : IDisposable
    {
        const string DriverHash = "F8965FDCE668692C3785AFA3559159F9A18287BC0D53ABB21902895A8ECF221B";
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint creation, uint flags, IntPtr tpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] i, int il, byte[] o, int ol, out int n, IntPtr ov);

        readonly SafeFileHandle h;
        readonly Mutex mutex = new Mutex(false, @"Local\MechrevoPtlFanDiagnostic");

        public FanReader()
        {
            if (!Platform.FanReadAllowed) throw new NotSupportedException("BIOS not verified for fan reads");
            string driver = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\drivers\inpoutx64.sys");
            using (var f = File.OpenRead(driver))
            using (var sha = SHA256.Create())
                if (BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "") != DriverHash)
                    throw new NotSupportedException("inpoutx64 driver differs from the verified version");
            h = CreateFile(@"\\.\inpoutx64", 0xc0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        byte In(ushort p)
        {
            byte[] i = new byte[16], o = new byte[16]; i[0] = (byte)p; int n;
            if (!DeviceIoControl(h, 0x9c402004, i, 2, o, 16, out n, IntPtr.Zero) || n != 1) throw new IOException("port read failed");
            return o[0];
        }
        void Out(ushort p, byte v)
        {
            byte[] i = new byte[16], o = new byte[16]; i[0] = (byte)p; i[2] = v; int n;
            if (!DeviceIoControl(h, 0x9c402008, i, 3, o, 16, out n, IntPtr.Zero)) throw new IOException("port write failed");
        }
        void Wait(byte mask, byte want)
        {
            var sw = Stopwatch.StartNew();
            while ((In(0x6c) & mask) != want)
            {
                if (sw.ElapsedMilliseconds > 500) throw new TimeoutException("EC handshake timeout");
                Thread.Sleep(1);
            }
        }
        // Only RPM queries can be issued. Pending output belongs to another client, so the sample is skipped.
        byte Query(byte arg)
        {
            if (arg < 0x16 || arg > 0x19) throw new ArgumentException("query not allowed");
            Wait(2, 0);
            if ((In(0x6c) & 1) != 0) throw new IOException("EC output pending");
            Out(0x6c, 0xd5); Wait(2, 0); Out(0x68, arg); Wait(1, 1);
            return In(0x68);
        }
        int Rpm(byte lo, byte hi)
        {
            for (int a = 0; a < 3; a++)
            {
                byte b = Query(hi), l = Query(lo), c = Query(hi);
                if (b == c) { int rpm = l + 256 * c; if (rpm <= 16000) return rpm; }
            }
            throw new IOException("RPM sample unstable");
        }
        public void Read(out int fan0, out int fan1)
        {
            bool got = false;
            try
            {
                try { got = mutex.WaitOne(100); } catch (AbandonedMutexException) { got = true; }
                if (!got) throw new IOException("EC channel busy");
                fan0 = Rpm(0x18, 0x19); fan1 = Rpm(0x16, 0x17);
            }
            finally { if (got) mutex.ReleaseMutex(); }
        }
        // Charge thresholds. Verified on this machine (BIOS 1.01): read F1 12/13/14; write F1 10, F3 100, F2 80, F1 11.
        // Protocol and write order from clmslgs/open-revo ptl_pmc.rs. See docs/protocol.md.
        byte Send(byte cmd, byte arg, bool reply)
        {
            bool ok = (cmd == 0xf1 && arg >= 0x10 && arg <= 0x14) || ((cmd == 0xf2 || cmd == 0xf3) && arg <= 100)
                || cmd == 0xd6 || (cmd == 0xd5 && arg == 0x10);
            if (!ok) throw new ArgumentException("command not allowed");
            Wait(2, 0);
            if ((In(0x6c) & 1) != 0) throw new IOException("EC output pending, nothing sent");
            Out(0x6c, cmd); Wait(2, 0); Out(0x68, arg);
            if (reply) { Wait(1, 1); return In(0x68); }
            // These commands do not reply. Read any stray byte so it is not taken as the next answer.
            Wait(2, 0); Thread.Sleep(30);
            if ((In(0x6c) & 1) != 0) Log.Write("charge: unexpected reply 0x" + In(0x68).ToString("X2") + " to " + cmd.ToString("X2") + " " + arg.ToString("X2"));
            return 0;
        }

        T Locked<T>(Func<T> f)
        {
            bool got = false;
            try
            {
                try { got = mutex.WaitOne(3000); } catch (AbandonedMutexException) { got = true; }
                if (!got) throw new IOException("EC channel busy");
                return f();
            }
            finally { if (got) mutex.ReleaseMutex(); }
        }

        // Fan writes, verified on this machine: D6 <pwm> holds both fans at one PWM, D5 10 returns them to the EC curve.
        // Neither replies. Manual PWM bypasses the EC curve, so the caller must restore auto (see Monitor).
        public void SetFanPercent(int percent)
        {
            if (percent < 30 || percent > 100) throw new ArgumentOutOfRangeException("percent");
            Locked(() => Send(0xd6, (byte)Math.Round(percent * 255.0 / 100), false));
        }

        public void SetFanAuto() { Locked(() => Send(0xd5, 0x10, false)); }

        // D6 00: both fans stop. Used only by the quiet mode, which hands back to the EC as soon as it warms up.
        public void SetFanOff() { Locked(() => Send(0xd6, 0, false)); }

        // Returns enabled (0 or 1), lower (stop charging at) and upper (discharge above) percentages.
        public int[] ReadCharge()
        {
            var v = Locked(() => new int[] { Send(0xf1, 0x12, true), Send(0xf1, 0x13, true), Send(0xf1, 0x14, true) });
            if (v[0] > 1 || v[1] > 100 || v[2] > 100) throw new IOException("unexpected charge reply " + string.Join(",", v));
            return v;
        }

        // Writes, then reads back. On any mismatch it writes the previous values back and throws.
        public void WriteCharge(int enabled, int lower, int upper)
        {
            var before = ReadCharge();
            if (before[0] == enabled && before[1] == lower && before[2] == upper) return;
            try { WriteChargeOnce(enabled, lower, upper); }
            catch (Exception e)
            {
                try { WriteChargeOnce(before[0], before[1], before[2]); Log.Write("charge: rolled back after " + e.Message); }
                catch (Exception r) { Log.Write("charge: rollback failed: " + r.Message); throw new IOException(e.Message + "; rollback failed: " + r.Message); }
                throw;
            }
        }

        void WriteChargeOnce(int enabled, int lower, int upper)
        {
            Locked(() =>
            {
                Send(0xf1, 0x10, false);
                int e = Send(0xf3, (byte)upper, true); if (e != upper) throw new IOException("upper echo " + e + ", wanted " + upper);
                e = Send(0xf2, (byte)lower, true); if (e != lower) throw new IOException("lower echo " + e + ", wanted " + lower);
                if (enabled == 1) Send(0xf1, 0x11, false);
                return 0;
            });
            Thread.Sleep(300);
            var v = ReadCharge();
            if (v[0] != enabled || v[1] != lower || v[2] != upper)
                throw new IOException("charge read-back " + string.Join(",", v) + ", wanted " + enabled + "," + lower + "," + upper);
        }

        public void Dispose() { h.Dispose(); mutex.Dispose(); }
    }

    // GPU frequency through the Intel Graphics Control Library that ships with the driver.
    internal sealed class GpuFreq : IDisposable
    {
        const string D = "ControlLib.dll";
        [StructLayout(LayoutKind.Sequential)] struct InitArgs { public uint Size; public byte Version; public uint AppVersion; public uint Flags; public uint Supported; public uint U1, U2, U3, U4; }
        [StructLayout(LayoutKind.Sequential)] struct FreqProps { public uint Size; public byte Version; public int Type; public byte CanControl; public double Min, Max; }
        [StructLayout(LayoutKind.Sequential)] struct FreqState { public uint Size; public byte Version; public double Voltage, Request, Tdp, Efficient, Actual; public uint Throttle; }
        [DllImport(D, CallingConvention = CallingConvention.Cdecl)] static extern int ctlInit(ref InitArgs a, out IntPtr h);
        [DllImport(D, CallingConvention = CallingConvention.Cdecl)] static extern int ctlClose(IntPtr h);
        [DllImport(D, CallingConvention = CallingConvention.Cdecl)] static extern int ctlEnumerateDevices(IntPtr h, ref uint n, IntPtr[] d);
        [DllImport(D, CallingConvention = CallingConvention.Cdecl)] static extern int ctlEnumFrequencyDomains(IntPtr d, ref uint n, IntPtr[] f);
        [DllImport(D, CallingConvention = CallingConvention.Cdecl)] static extern int ctlFrequencyGetProperties(IntPtr f, ref FreqProps p);
        [DllImport(D, CallingConvention = CallingConvention.Cdecl)] static extern int ctlFrequencyGetState(IntPtr f, ref FreqState s);
        [StructLayout(LayoutKind.Sequential)] struct FreqRange { public uint Size; public byte Version; public double Min, Max; }
        [DllImport(D, CallingConvention = CallingConvention.Cdecl)] static extern int ctlFrequencyGetRange(IntPtr f, ref FreqRange r);
        [DllImport(D, CallingConvention = CallingConvention.Cdecl)] static extern int ctlFrequencySetRange(IntPtr f, ref FreqRange r);

        readonly IntPtr api, domain;
        readonly object gate = new object();
        public readonly bool CanControl;
        public readonly double HwMinMhz, HwMaxMhz;

        public GpuFreq()
        {
            var a = new InitArgs();
            a.Size = (uint)Marshal.SizeOf(typeof(InitArgs));
            a.AppVersion = (1u << 16) | 1u;
            a.Flags = 1; // CTL_INIT_FLAG_USE_LEVEL_ZERO, required for telemetry
            int r = ctlInit(ref a, out api);
            if (r != 0) throw new InvalidOperationException("ctlInit 0x" + r.ToString("X8"));
            uint n = 0;
            ctlEnumerateDevices(api, ref n, null);
            if (n == 0) throw new InvalidOperationException("no IGCL device");
            var devs = new IntPtr[n];
            ctlEnumerateDevices(api, ref n, devs);
            uint m = 0;
            r = ctlEnumFrequencyDomains(devs[0], ref m, null);
            if (r != 0 || m == 0) throw new InvalidOperationException("no frequency domain 0x" + r.ToString("X8"));
            var fs = new IntPtr[m];
            ctlEnumFrequencyDomains(devs[0], ref m, fs);
            foreach (var f in fs)
            {
                var p = new FreqProps();
                p.Size = (uint)Marshal.SizeOf(typeof(FreqProps));
                if (ctlFrequencyGetProperties(f, ref p) == 0 && p.Type == 0) { domain = f; CanControl = p.CanControl != 0; HwMinMhz = p.Min; HwMaxMhz = p.Max; break; }
            }
            if (domain == IntPtr.Zero) throw new InvalidOperationException("no GPU frequency domain");
        }

        public double ReadMhz()
        {
            var s = new FreqState();
            s.Size = (uint)Marshal.SizeOf(typeof(FreqState));
            int r;
            lock (gate) r = ctlFrequencyGetState(domain, ref s);
            if (r != 0) throw new InvalidOperationException("ctlFrequencyGetState 0x" + r.ToString("X8"));
            return s.Actual < 0 ? double.NaN : s.Actual;
        }

        // The driver's allowed frequency range in MHz. Setting the maximum caps the GPU and leaves power to the CPU.
        public void GetRange(out double min, out double max)
        {
            var g = new FreqRange { Size = (uint)Marshal.SizeOf(typeof(FreqRange)) };
            int r;
            lock (gate) r = ctlFrequencyGetRange(domain, ref g);
            if (r != 0) throw new InvalidOperationException("ctlFrequencyGetRange 0x" + r.ToString("X8"));
            min = g.Min; max = g.Max;
        }

        // Caps below this never take effect: under load the GPU still runs at its efficient frequency (900 MHz),
        // even with the minimum lowered too. Measured on this machine.
        public const int LowestCapMhz = 900;

        // 0 removes the cap. In the IGCL range, -1 means the factory value, so the minimum always stays at the
        // factory setting; writing the hardware minimum (100 MHz) lets the GPU idle far lower than normal.
        public void SetMaxMhz(int mhz)
        {
            if (!CanControl) throw new NotSupportedException("驱动不允许控制 GPU 频率");
            double max = mhz <= 0 ? -1 : Math.Max(LowestCapMhz, Math.Min(HwMaxMhz, mhz));
            var g = new FreqRange { Size = (uint)Marshal.SizeOf(typeof(FreqRange)), Min = -1, Max = max };
            int r;
            lock (gate) r = ctlFrequencySetRange(domain, ref g);
            if (r != 0) throw new InvalidOperationException("ctlFrequencySetRange 0x" + r.ToString("X8"));
        }

        public void Dispose() { if (api != IntPtr.Zero) ctlClose(api); }
    }

    // CPU cap through the active Windows power plan's "Maximum processor state" (percent), for both efficiency
    // classes (0: E and LPE cores, 1: P cores), on AC and battery. Below 100% turbo is off and the percent applies
    // to the base frequency: measured 99% about 1.8 GHz on P cores, 80% about 1.5, 70% about 1.3.
    // The "Maximum processor frequency" setting would allow caps above base, but on this machine other software
    // (probably Intel DTT) rewrites it within a minute, so it is only cleared. See docs/protocol.md.
    internal static class CpuCap
    {
        static Guid Sub = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        static readonly Guid[] State = { new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"), new Guid("bc5038f7-23e0-4960-96da-33abaf5935ed") };
        static readonly Guid[] Freq = { new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100"), new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e101") };
        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

        static Guid Active()
        {
            IntPtr p;
            uint r = PowerGetActiveScheme(IntPtr.Zero, out p);
            if (r != 0) throw new InvalidOperationException("PowerGetActiveScheme " + r);
            try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); } finally { LocalFree(p); }
        }

        static void Write(Guid[] settings, uint value)
        {
            Guid scheme = Active();
            foreach (var setting in settings)
            {
                Guid s = setting;
                uint r = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref Sub, ref s, value);
                if (r == 0) r = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref Sub, ref s, value);
                if (r != 0) throw new InvalidOperationException("power plan write " + r);
            }
            uint a = PowerSetActiveScheme(IntPtr.Zero, ref scheme); // applies the change
            if (a != 0) throw new InvalidOperationException("PowerSetActiveScheme " + a);
        }

        // Percent for the P cores on AC power.
        public static int ReadPercent()
        {
            Guid scheme = Active(), s = State[1];
            uint v;
            uint r = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref Sub, ref s, out v);
            if (r != 0) throw new InvalidOperationException("PowerReadACValueIndex " + r);
            return (int)v;
        }

        public static void SetPercent(int percent)
        {
            if (percent < 50 || percent > 100) throw new ArgumentOutOfRangeException("percent");
            Write(State, (uint)percent);
            if (ReadPercent() != percent) throw new InvalidOperationException("power plan read-back mismatch");
        }

        // Removes the frequency cap an earlier version wrote.
        public static void ClearFrequencyCap() { Write(Freq, 0); }
    }

    // Windows 11 power mode (Settings > Power > Power mode), the "overlay" on top of the power plan.
    // Here Intel's power management follows it: "Best power efficiency" kept turbo on one core (P cores
    // about 3.8 GHz instead of 4.6) and cut package power by about 25%, while single settings such as the
    // frequency cap, core parking and EPP are rewritten. The mode is kept per power source (AC and battery).
    internal static class PowerMode
    {
        public static readonly Guid BestEfficiency = new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");
        [DllImport("powrprof.dll")] static extern uint PowerGetActualOverlayScheme(out Guid g);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid g);

        public static Guid Get()
        {
            Guid g;
            uint r = PowerGetActualOverlayScheme(out g);
            if (r != 0) throw new InvalidOperationException("PowerGetActualOverlayScheme " + r);
            return g;
        }

        public static void Set(Guid g)
        {
            uint r = PowerSetActiveOverlayScheme(g);
            if (r != 0) throw new InvalidOperationException("PowerSetActiveOverlayScheme " + r);
        }
    }

    // Package temperature from Intel DTT. Domain 0 is the processor package. Needs administrator rights.
    internal sealed class PackageTemp
    {
        public double Read()
        {
            using (var s = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName, Temperature FROM EsifDeviceInformation"))
            using (var all = s.Get())
                foreach (ManagementObject o in all)
                    using (o)
                    {
                        var name = o["InstanceName"] as string;
                        if (name == null || !name.EndsWith("_0")) continue;
                        double t = Convert.ToDouble(o["Temperature"]);
                        if (t > 0 && t < 125) return t;
                    }
            return double.NaN;
        }
    }

    // Core frequencies and RAPL power from Windows performance counters.
    internal sealed class CounterSet : IDisposable
    {
        readonly IntPtr q, pkg, gpu, cpuPct, cpuPerf, cpuBase, gpuEngines, netIn, netOut;
        readonly List<IntPtr> p = new List<IntPtr>(), e = new List<IntPtr>(), l = new List<IntPtr>();

        public CounterSet()
        {
            Check(Native.PdhOpenQuery(null, IntPtr.Zero, out q));
            Check(Native.PdhAddEnglishCounter(q, @"\Energy Meter(rapl_package0_pkg)\Power", IntPtr.Zero, out pkg));
            Check(Native.PdhAddEnglishCounter(q, @"\Energy Meter(rapl_package0_pp1)\Power", IntPtr.Zero, out gpu));
            Check(Native.PdhAddEnglishCounter(q, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out cpuPct));
            Check(Native.PdhAddEnglishCounter(q, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out cpuPerf));
            Check(Native.PdhAddEnglishCounter(q, @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out cpuBase));
            Check(Native.PdhAddEnglishCounter(q, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out gpuEngines));
            Check(Native.PdhAddEnglishCounter(q, @"\Network Interface(*)\Bytes Received/sec", IntPtr.Zero, out netIn));
            Check(Native.PdhAddEnglishCounter(q, @"\Network Interface(*)\Bytes Sent/sec", IntPtr.Zero, out netOut));
            foreach (var c in ReadTopology())
            {
                IntPtr h;
                Check(Native.PdhAddEnglishCounter(q, @"\Processor Information(0," + c.Key + @")\Actual Frequency", IntPtr.Zero, out h));
                (c.Value == 'P' ? p : c.Value == 'E' ? e : l).Add(h);
            }
            Native.PdhCollectQueryData(q);
        }

        // P cores have the highest efficiency class. LPE cores share neither their class nor their last-level cache with P cores.
        static IEnumerable<KeyValuePair<int, char>> ReadTopology()
        {
            uint len;
            Native.GetSystemCpuSetInformation(IntPtr.Zero, 0, out len, IntPtr.Zero, 0);
            var mem = Marshal.AllocHGlobal((int)len);
            var items = new List<int[]>();
            try
            {
                if (!Native.GetSystemCpuSetInformation(mem, len, out len, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                for (int off = 0; off < len; off += Marshal.ReadInt32(mem, off))
                    items.Add(new[] { (int)Marshal.ReadByte(mem, off + 14), Marshal.ReadByte(mem, off + 16), Marshal.ReadByte(mem, off + 18) });
            }
            finally { Marshal.FreeHGlobal(mem); }
            int top = 0, topLlc = 0;
            foreach (var i in items) if (i[2] > top) { top = i[2]; topLlc = i[1]; }
            var r = new List<KeyValuePair<int, char>>();
            foreach (var i in items)
                r.Add(new KeyValuePair<int, char>(i[0], i[2] == top ? 'P' : i[1] == topLlc ? 'E' : 'L'));
            return r;
        }

        static void Check(int status) { if (status != 0) throw new Win32Exception(status); }

        double One(IntPtr c)
        {
            uint t; Native.PDH_VALUE v;
            return Native.PdhGetFormattedCounterValue(c, Native.PDH_FMT_DOUBLE, out t, out v) == 0 ? v.Value : double.NaN;
        }
        double Avg(List<IntPtr> cs)
        {
            double s = 0; int n = 0;
            foreach (var c in cs) { double x = One(c); if (!double.IsNaN(x)) { s += x; n++; } }
            return n == 0 ? double.NaN : s / n / 1000.0;
        }

        // Calls back with each instance of a wildcard counter. False if the counter could not be read.
        static bool EachInstance(IntPtr c, Action<string, double> each)
        {
            const int PDH_MORE_DATA = unchecked((int)0x800007D2);
            uint size = 0, count;
            int r = Native.PdhGetFormattedCounterArray(c, Native.PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
            if (r != PDH_MORE_DATA || size == 0) return false;
            IntPtr mem = Marshal.AllocHGlobal((int)size);
            try
            {
                r = Native.PdhGetFormattedCounterArray(c, Native.PDH_FMT_DOUBLE, ref size, out count, mem);
                if (r != 0) return false;
                int itemSize = IntPtr.Size + Marshal.SizeOf(typeof(Native.PDH_VALUE));
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(mem.ToInt64() + i * itemSize);
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                    var v = (Native.PDH_VALUE)Marshal.PtrToStructure(new IntPtr(item.ToInt64() + IntPtr.Size), typeof(Native.PDH_VALUE));
                    if (name != null && (v.Status == 0 || v.Status == 1)) each(name, v.Value);
                }
                return true;
            }
            finally { Marshal.FreeHGlobal(mem); }
        }

        // Same approach as Task Manager: add up each engine type across processes, then take the busiest type.
        double GpuBusyPercent()
        {
            var byType = new Dictionary<string, double>();
            double top = 0;
            bool ok = EachInstance(gpuEngines, (name, value) =>
            {
                int at = name.IndexOf("engtype_", StringComparison.Ordinal);
                string type = at < 0 ? name : name.Substring(at);
                double sum;
                byType.TryGetValue(type, out sum);
                sum += value;
                byType[type] = sum;
                if (sum > top) top = sum;
            });
            return ok ? Math.Min(100, top) : double.NaN;
        }

        // Bytes per second over all network adapters.
        static double Total(IntPtr c)
        {
            double sum = 0;
            return EachInstance(c, (name, value) => sum += value) ? sum : double.NaN;
        }

        // Task Manager's memory "In use" share of physical memory.
        static double MemoryPercent()
        {
            var m = new Native.MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX)) };
            if (!Native.GlobalMemoryStatusEx(ref m) || m.TotalPhys == 0) return double.NaN;
            return 100.0 * (m.TotalPhys - m.AvailPhys) / m.TotalPhys;
        }

        public void Sample(Snapshot s)
        {
            Native.PdhCollectQueryData(q);
            double c = One(cpuPct);
            s.CpuPct = double.IsNaN(c) ? c : Math.Min(100, c);
            s.GpuPct = GpuBusyPercent();
            s.PGhz = Avg(p); s.EGhz = Avg(e); s.LGhz = Avg(l);
            s.CpuGhz = One(cpuBase) * One(cpuPerf) / 100 / 1000.0; // Task Manager's "Speed": base frequency times % performance
            s.PkgW = One(pkg) / 1000.0; s.GpuW = One(gpu) / 1000.0;
            s.NetIn = Total(netIn); s.NetOut = Total(netOut);
            s.MemPct = MemoryPercent();
        }

        public void Dispose() { Native.PdhCloseQuery(q); }
    }

    // Whole-laptop power while on battery: the battery's discharge rate. On AC there is no such reading
    // (no platform power counter on this machine, and the battery reads 0 while full or held at the charge limit).
    internal sealed class BatteryPower : IDisposable
    {
        readonly ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT Discharging, DischargeRate FROM BatteryStatus");

        // Watts, or NaN on AC or when not discharging.
        public double Read()
        {
            if (System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Offline) return double.NaN;
            double mw = 0; bool any = false;
            using (var all = searcher.Get())
                foreach (ManagementObject o in all)
                    using (o)
                        if (o["Discharging"] is bool && (bool)o["Discharging"]) { mw += Convert.ToDouble(o["DischargeRate"]); any = true; }
            return any && mw > 0 ? mw / 1000.0 : double.NaN;
        }

        public void Dispose() { searcher.Dispose(); }
    }

    internal sealed class Snapshot
    {
        public double PGhz = double.NaN, EGhz = double.NaN, LGhz = double.NaN, PkgW = double.NaN, GpuW = double.NaN, TempC = double.NaN, GpuMhz = double.NaN, CpuPct = double.NaN, GpuPct = double.NaN, CpuGhz = double.NaN, MemPct = double.NaN, NetIn = double.NaN, NetOut = double.NaN, SysW = double.NaN;
        public int Fan0 = -1, Fan1 = -1, Mode;
    }
}
