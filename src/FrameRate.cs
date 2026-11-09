using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace XingyaoPerf
{
    // Frames per second of one process, counted from the DXGI "Present" events Windows already emits (ETW),
    // the way PresentMon does. Nothing touches the game process. Covers Direct3D 10-12 (DXGI), not Vulkan
    // or OpenGL. Needs administrator rights. The session runs only while a game overlay is shown.
    internal sealed class FrameRate : IDisposable
    {
        const string SessionName = "xingyao-perf-fps";
        static Guid DxgiProvider = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
        const ushort PresentStart = 42, PresentMpoStart = 55; // Present_Start, PresentMultiplaneOverlay_Start

        const uint WnodeFlagTracedGuid = 0x20000, RealTimeMode = 0x100, ControlStop = 1, EnableProvider = 1;
        const uint ProcessTraceRealTime = 0x100, ProcessTraceEventRecord = 0x10000000;
        const int PropsSize = 120, NameChars = 1024, LogFileSize = 448;
        const ulong InvalidHandle = 0xFFFFFFFFFFFFFFFF;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void EventRecordCallback(IntPtr record);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint StartTraceW(out ulong handle, string name, IntPtr props);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint ControlTraceW(ulong handle, string name, IntPtr props, uint code);
        [DllImport("advapi32.dll")] static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint code, byte level, ulong any, ulong all, uint timeout, IntPtr parameters);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern ulong OpenTraceW(IntPtr logfile);
        [DllImport("advapi32.dll")] static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong handle);

        readonly EventRecordCallback callback; // kept alive while the session runs
        readonly object gate = new object();
        ulong session, trace = InvalidHandle;
        IntPtr logFile, loggerName;
        Thread thread;
        volatile int target; // process id being counted, 0 for none
        // QPC timestamps of recent presents. ETW delivers events in batches about once a second, so counting
        // arrivals per second jumps around; the rate is worked out from the events' own timestamps instead.
        readonly long[] stamps = new long[2048];
        int stampCount; // total stored since the target changed
        bool failed;

        public FrameRate() { callback = OnEvent; }

        public bool Running { get { return thread != null; } }

        void OnEvent(IntPtr record)
        {
            // EVENT_RECORD: EventHeader.ProcessId at 12, EventHeader.EventDescriptor.Id at 40.
            // EventHeader.TimeStamp at 16 is a QPC value, because the session uses ClientContext 1.
            if (Marshal.ReadInt32(record, 12) != target) return;
            ushort id = (ushort)Marshal.ReadInt16(record, 40);
            if (id != PresentStart && id != PresentMpoStart) return;
            long ts = Marshal.ReadInt64(record, 16);
            lock (stamps) { stamps[stampCount % stamps.Length] = ts; stampCount++; }
        }

        static IntPtr NewProps()
        {
            int size = PropsSize + NameChars * 2;
            IntPtr p = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, size);                  // Wnode.BufferSize
            Marshal.WriteInt32(p, 40, 1);                    // Wnode.ClientContext: QPC timestamps
            Marshal.WriteInt32(p, 44, (int)WnodeFlagTracedGuid);
            Marshal.WriteInt32(p, 48, 64);                   // BufferSize in KB
            Marshal.WriteInt32(p, 64, (int)RealTimeMode);    // LogFileMode
            Marshal.WriteInt32(p, 68, 1);                    // FlushTimer: deliver at least every second
            Marshal.WriteInt32(p, 116, PropsSize);           // LoggerNameOffset
            return p;
        }

        // Starts counting frames of this process. Returns false if the session cannot run (for example without admin).
        public bool Track(int pid)
        {
            lock (gate)
            {
                if (target != pid) { target = pid; lock (stamps) stampCount = 0; }
                if (thread != null) return true;
                if (failed) return false;
                try { Start(); return true; }
                catch (Exception e) { failed = true; Log.Write("fps: " + e.Message); StopLocked(); return false; }
            }
        }

        // Frames per second over the last second of presents, by their timestamps; NaN when there were none
        // in the last 2 seconds.
        public double Read()
        {
            long freq = System.Diagnostics.Stopwatch.Frequency, now = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (stamps)
            {
                if (stampCount < 2) return double.NaN;
                int stored = Math.Min(stampCount, stamps.Length);
                long newest = stamps[(stampCount - 1) % stamps.Length];
                if (now - newest > 2 * freq) return double.NaN;
                long oldest = newest;
                int n = 1;
                for (int k = 2; k <= stored; k++)
                {
                    long t = stamps[(stampCount - k) % stamps.Length];
                    if (newest - t > freq) break;
                    oldest = t; n++;
                }
                if (n < 2 || newest == oldest) return double.NaN;
                return (n - 1) * (double)freq / (newest - oldest);
            }
        }

        void Start()
        {
            IntPtr props = NewProps();
            try
            {
                uint r = StartTraceW(out session, SessionName, props);
                if (r == 183) // ERROR_ALREADY_EXISTS: left over from a crash
                {
                    ControlTraceW(0, SessionName, props, ControlStop);
                    Marshal.FreeHGlobal(props);
                    props = NewProps();
                    r = StartTraceW(out session, SessionName, props);
                }
                if (r != 0) throw new InvalidOperationException("StartTrace " + r);
            }
            finally { Marshal.FreeHGlobal(props); }

            uint e = EnableTraceEx2(session, ref DxgiProvider, EnableProvider, 4, ulong.MaxValue, 0, 0, IntPtr.Zero);
            if (e != 0) throw new InvalidOperationException("EnableTraceEx2 " + e);

            // EVENT_TRACE_LOGFILEW on x64: LoggerName at 8, ProcessTraceMode at 28, EventRecordCallback at 424.
            logFile = Marshal.AllocHGlobal(LogFileSize);
            for (int i = 0; i < LogFileSize; i++) Marshal.WriteByte(logFile, i, 0);
            loggerName = Marshal.StringToHGlobalUni(SessionName);
            Marshal.WriteIntPtr(logFile, 8, loggerName);
            Marshal.WriteInt32(logFile, 28, unchecked((int)(ProcessTraceRealTime | ProcessTraceEventRecord)));
            Marshal.WriteIntPtr(logFile, 424, Marshal.GetFunctionPointerForDelegate(callback));
            trace = OpenTraceW(logFile);
            if (trace == InvalidHandle) throw new InvalidOperationException("OpenTrace " + Marshal.GetLastWin32Error());

            ulong[] handles = { trace };
            thread = new Thread(() =>
            {
                uint p = ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
                if (p != 0 && p != 1223) Log.Write("fps: ProcessTrace " + p); // 1223: cancelled by Stop
            }) { IsBackground = true, Name = "fps" };
            thread.Start();
            Log.Write("fps: session started");
        }

        public void Stop() { lock (gate) StopLocked(); }

        void StopLocked()
        {
            target = 0;
            if (session != 0)
            {
                IntPtr props = NewProps();
                ControlTraceW(session, null, props, ControlStop); // ends ProcessTrace
                Marshal.FreeHGlobal(props);
                session = 0;
            }
            if (trace != InvalidHandle) { CloseTrace(trace); trace = InvalidHandle; }
            if (thread != null) { thread.Join(2000); thread = null; }
            if (logFile != IntPtr.Zero) { Marshal.FreeHGlobal(logFile); logFile = IntPtr.Zero; }
            if (loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(loggerName); loggerName = IntPtr.Zero; }
        }

        public void Dispose() { Stop(); }
    }
}
