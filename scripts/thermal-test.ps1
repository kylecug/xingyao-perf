# Thermal comparison: full CPU load per mode, logging package power, frequency, temperature and fan RPM.
#   -Mode balanced,turbo : switch through WMI ([3,10,m]) and restore the original mode afterwards (default)
#   -Mode keep           : write nothing; measure whatever mode the official Control Center has set
# EC port access is read-only: mode (E7/11) and fan RPM queries (D5/16..19) only.
# Port protocol from Kurisu934/FanControl.MechrevoPtl (MIT), validated there on P916F-PTL BIOS 1.01.
#   -Load cpu|gpu|both   : which load to run (default cpu). GPU load uses the system OpenCL runtime.
#   -GpuSmokeSeconds N   : run only the GPU load for N seconds and print power; needs no admin, writes nothing
param([string[]]$Mode = @('balanced', 'turbo'), [ValidateSet('cpu', 'gpu', 'both')][string]$Load = 'cpu', [int]$CoolSeconds = 120, [int]$LoadSeconds = 150, [int]$SampleSeconds = 5, [int]$GpuSmokeSeconds = 0)
$ErrorActionPreference = 'Stop'
$tag = ($Mode -join '-') + "-$Load"
if ($GpuSmokeSeconds -gt 0) { $tag = 'gpu-smoke' }
Start-Transcript -Path (Join-Path $PSScriptRoot "thermal-result-$tag.txt") -Force | Out-Null

Add-Type -TypeDefinition @'
using System; using System.Threading; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles;
public static class CpuLoad {
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
    public static uint KeepAwake() { return SetThreadExecutionState(0x80000001u); }
    public static uint AllowSleep() { return SetThreadExecutionState(0x80000000u); }
    static volatile bool run; static long[] counts; static Thread[] ts;
    public static void Start(int n) {
        run = true; counts = new long[n * 16]; ts = new Thread[n];
        for (int i = 0; i < n; i++) { int k = i * 16;
            ts[i] = new Thread(() => { ulong x = 88172645463325252UL + (ulong)k; long c = 0;
                while (run) { for (int j = 0; j < 100000; j++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; } c++; Volatile.Write(ref counts[k], c); } });
            ts[i].IsBackground = true; ts[i].Start(); } }
    public static long Total() { if (counts == null) return 0; long t = 0; for (int i = 0; i < counts.Length; i += 16) t += Volatile.Read(ref counts[i]); return t; }
    public static void Stop() { run = false; if (ts != null) foreach (var t in ts) t.Join(); ts = null; }
}
public static class GpuLoad {
    const string CL = "OpenCL.dll";
    [DllImport(CL)] static extern int clGetPlatformIDs(uint n, IntPtr[] p, out uint c);
    [DllImport(CL)] static extern int clGetDeviceIDs(IntPtr plat, ulong type, uint n, IntPtr[] d, out uint c);
    [DllImport(CL)] static extern int clGetDeviceInfo(IntPtr dev, uint param, UIntPtr size, byte[] val, out UIntPtr ret);
    [DllImport(CL)] static extern IntPtr clCreateContext(IntPtr props, uint n, IntPtr[] devs, IntPtr cb, IntPtr ud, out int err);
    [DllImport(CL)] static extern IntPtr clCreateCommandQueue(IntPtr ctx, IntPtr dev, ulong props, out int err);
    [DllImport(CL)] static extern IntPtr clCreateProgramWithSource(IntPtr ctx, uint n, string[] src, IntPtr len, out int err);
    [DllImport(CL)] static extern int clBuildProgram(IntPtr prog, uint n, IntPtr[] devs, string opts, IntPtr cb, IntPtr ud);
    [DllImport(CL)] static extern IntPtr clCreateKernel(IntPtr prog, string name, out int err);
    [DllImport(CL)] static extern IntPtr clCreateBuffer(IntPtr ctx, ulong flags, UIntPtr size, IntPtr host, out int err);
    [DllImport(CL)] static extern int clSetKernelArg(IntPtr k, uint idx, UIntPtr size, ref IntPtr val);
    [DllImport(CL, EntryPoint = "clSetKernelArg")] static extern int clSetKernelArgInt(IntPtr k, uint idx, UIntPtr size, ref int val);
    [DllImport(CL)] static extern int clEnqueueNDRangeKernel(IntPtr q, IntPtr k, uint dim, IntPtr off, UIntPtr[] global, IntPtr local, uint nw, IntPtr w, IntPtr e);
    [DllImport(CL)] static extern int clFinish(IntPtr q);
    const string Src = "kernel void burn(global float* o, int iters) { float x = (float)get_global_id(0) * 1e-6f + 1.0f; float y = 0.5f; for (int i = 0; i < iters; i++) { y = sin(x + y) * cos(y) + sqrt(fabs(x * y) + 1.0f); x = x * 0.999f + y * 0.001f; } o[get_global_id(0)] = y; }";
    const int Items = 262144;
    static volatile bool run; static long done; static System.Threading.Thread th; static string error;
    static void Check(int e, string what) { if (e != 0) throw new InvalidOperationException(what + " failed: " + e); }
    public static string Start() {
        uint np; Check(clGetPlatformIDs(0, null, out np), "platforms");
        var ps = new IntPtr[np]; Check(clGetPlatformIDs(np, ps, out np), "platforms");
        IntPtr dev = IntPtr.Zero;
        foreach (var p in ps) { uint nd; var ds = new IntPtr[1]; if (clGetDeviceIDs(p, 4, 1, ds, out nd) == 0 && nd > 0) { dev = ds[0]; break; } }
        if (dev == IntPtr.Zero) throw new InvalidOperationException("no OpenCL GPU found");
        var name = new byte[256]; UIntPtr nl; Check(clGetDeviceInfo(dev, 0x102B, (UIntPtr)256, name, out nl), "device name");
        int e; var devs = new[] { dev };
        var ctx = clCreateContext(IntPtr.Zero, 1, devs, IntPtr.Zero, IntPtr.Zero, out e); Check(e, "context");
        var q = clCreateCommandQueue(ctx, dev, 0, out e); Check(e, "queue");
        var prog = clCreateProgramWithSource(ctx, 1, new[] { Src }, IntPtr.Zero, out e); Check(e, "program");
        Check(clBuildProgram(prog, 1, devs, null, IntPtr.Zero, IntPtr.Zero), "build");
        var k = clCreateKernel(prog, "burn", out e); Check(e, "kernel");
        var buf = clCreateBuffer(ctx, 1, (UIntPtr)(Items * 4), IntPtr.Zero, out e); Check(e, "buffer");
        Check(clSetKernelArg(k, 0, (UIntPtr)IntPtr.Size, ref buf), "arg0");
        run = true; done = 0; error = null;
        th = new System.Threading.Thread(() => {
            try {
                int iters = 50; var global = new[] { (UIntPtr)Items };
                var sw = new System.Diagnostics.Stopwatch();
                while (run) {
                    Check(clSetKernelArgInt(k, 1, (UIntPtr)4, ref iters), "arg1");
                    sw.Restart();
                    Check(clEnqueueNDRangeKernel(q, k, 1, IntPtr.Zero, global, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero), "enqueue");
                    Check(clFinish(q), "finish");
                    System.Threading.Interlocked.Add(ref done, (long)Items * iters / 1000);
                    // Keep each dispatch well under the 2 s GPU watchdog.
                    if (sw.ElapsedMilliseconds < 80 && iters < 200000) iters *= 2;
                    else if (sw.ElapsedMilliseconds > 300 && iters > 50) iters /= 2;
                }
            } catch (Exception x) { error = x.Message; run = false; }
        });
        th.IsBackground = true; th.Start();
        return System.Text.Encoding.ASCII.GetString(name, 0, Math.Max(0, (int)(uint)nl - 1));
    }
    public static long Total() { return System.Threading.Interlocked.Read(ref done); }
    public static string Error() { return error; }
    public static void Stop() { run = false; if (th != null) th.Join(); th = null; }
}
public sealed class EcRead : IDisposable {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint creation, uint flags, IntPtr tpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] i, int il, byte[] o, int ol, out int n, IntPtr ov);
    readonly SafeFileHandle h; readonly Mutex mutex = new Mutex(false, @"Local\MechrevoPtlFanDiagnostic");
    public EcRead() {
        h = CreateFile(@"\\.\inpoutx64", 0xc0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    byte In(ushort p) {
        byte[] i = new byte[16], o = new byte[16]; i[0] = (byte)p; int n;
        if (!DeviceIoControl(h, 0x9c402004, i, 2, o, 16, out n, IntPtr.Zero) || n != 1) throw new InvalidOperationException("port read failed");
        return o[0];
    }
    void Out(ushort p, byte v) {
        byte[] i = new byte[16], o = new byte[16]; i[0] = (byte)p; i[2] = v; int n;
        if (!DeviceIoControl(h, 0x9c402008, i, 3, o, 16, out n, IntPtr.Zero)) throw new InvalidOperationException("port write failed");
    }
    void Wait(byte mask, byte want) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((In(0x6c) & mask) != want) { if (sw.ElapsedMilliseconds > 1000) throw new TimeoutException("EC handshake timeout"); Thread.Sleep(1); }
    }
    // Only these read queries can be issued. A pending output byte belongs to someone else, so the query is skipped.
    byte Query(byte cmd, byte arg) {
        bool ok = (cmd == 0xe7 && arg == 0x11) || (cmd == 0xd5 && arg >= 0x16 && arg <= 0x19);
        if (!ok) throw new ArgumentException("query not allowed");
        Wait(2, 0);
        if ((In(0x6c) & 1) != 0) throw new InvalidOperationException("EC output pending, sample skipped");
        Out(0x6c, cmd); Wait(2, 0); Out(0x68, arg); Wait(1, 1);
        return In(0x68);
    }
    int Rpm(byte lo, byte hi) {
        for (int a = 0; a < 3; a++) { byte b = Query(0xd5, hi); byte l = Query(0xd5, lo); byte c = Query(0xd5, hi); if (b == c) return l + 256 * c; }
        return -1;
    }
    public string Sample() {
        bool got = false;
        try { try { got = mutex.WaitOne(500); } catch (AbandonedMutexException) { got = true; }
            if (!got) return "busy,busy,busy";
            return Query(0xe7, 0x11) + "," + Rpm(0x18, 0x19) + "," + Rpm(0x16, 0x17);
        } finally { if (got) mutex.ReleaseMutex(); }
    }
    public void Dispose() { h.Dispose(); mutex.Dispose(); }
}
'@

$names = @{ 1 = 'office'; 2 = 'balanced'; 3 = 'turbo' }
$codes = @{ office = 1; balanced = 2; turbo = 3 }
$acks = @{ 1 = 0x94; 2 = 0x95; 3 = 0x96 }
$script:inst = $null
$script:ec = $null
$script:fanFailures = 0

function Call-Fw([byte[]]$bytes) {
    $buf = New-Object byte[] 64
    [Array]::Copy($bytes, $buf, $bytes.Length)
    $r = Invoke-CimMethod -InputObject $script:inst -MethodName OemWMIfun -Arguments @{ u8Input = [byte[]]$buf }
    return [byte[]]$r.u8Output
}
function Read-Mode { $o = Call-Fw ([byte[]](3, 9)); if ($o[0] -ne 0) { throw "read status $($o[0])" }; return [int]$o[1] }
function Set-Mode([int]$m) {
    if ($m -notin 1, 2, 3) { throw "bad mode $m" }
    $o = Call-Fw ([byte[]](3, 10, $m))
    if ($o[0] -ne $acks[$m]) { throw ("bad ack 0x{0:X2} for {1}" -f $o[0], $names[$m]) }
    Start-Sleep -Milliseconds 400
    $now = Read-Mode
    if ($now -ne $m) { throw "read-back is $($names[$now]), wanted $($names[$m])" }
    "SET $($names[$m]) OK"
}
function Read-Temp {
    $v = @(Get-CimInstance -Namespace root\WMI -ClassName EsifDeviceInformation -Property InstanceName, Temperature -ErrorAction Stop | Where-Object { $_.InstanceName -match '_0$' -and $_.Temperature -gt 0 -and $_.Temperature -lt 125 } | ForEach-Object { [int]$_.Temperature })
    if ($v.Count) { return ($v | Measure-Object -Maximum).Maximum }
    return -1
}
function Read-Ec {
    if (-not $script:ec -or $script:fanFailures -ge 5) { return 'na,na,na' }
    try { $s = $script:ec.Sample(); $script:fanFailures = 0; return $s }
    catch { $script:fanFailures++; return 'err,err,err' }
}
function Sample-Line($phase, $t) {
    $s = (Get-Counter '\Energy Meter(rapl_package0_pkg)\Power', '\Energy Meter(rapl_package0_pp1)\Power', '\Processor Information(_Total)\Actual Frequency', '\Processor Information(_Total)\Performance Limit Flags' -ErrorAction Stop).CounterSamples
    $w = [double]($s | Where-Object { $_.Path -match 'package0_pkg' }).CookedValue / 1000
    $gw = [double]($s | Where-Object { $_.Path -match 'package0_pp1' }).CookedValue / 1000
    $f = [double]($s | Where-Object { $_.Path -match 'actual frequency' }).CookedValue
    $l = [double]($s | Where-Object { $_.Path -match 'limit flags' }).CookedValue
    [pscustomobject]@{ Phase = $phase; T = [int]$t; Watts = [math]::Round($w, 1); GpuW = [math]::Round($gw, 1); MHz = [math]::Round($f); TempC = (Read-Temp); LimitFlags = [int]$l; Ec = (Read-Ec) }
}
function Avg($rows, $prop, $from) {
    $x = @($rows | Where-Object { $_.T -ge $from -and $_.$prop -ge 0 } | ForEach-Object { $_.$prop })
    if ($x.Count) { return [math]::Round(($x | Measure-Object -Average).Average, 1) }
    return -1
}
function Fan-Avg($rows, $idx, $from) {
    $x = @($rows | Where-Object { $_.T -ge $from } | ForEach-Object { $p = $_.Ec -split ','; $n = 0; if ([int]::TryParse($p[$idx], [ref]$n) -and $n -ge 0) { $n } })
    if ($x.Count) { return [math]::Round(($x | Measure-Object -Average).Average) }
    return -1
}
function Run-One($label) {
    # Run-One returns its summary object through the pipeline, so progress must go to the host.
    Write-Host "--- $label : idle $CoolSeconds s ---"
    $sw = [Diagnostics.Stopwatch]::StartNew(); $next = 0
    while ($sw.Elapsed.TotalSeconds -lt $CoolSeconds) {
        if ($sw.Elapsed.TotalSeconds -ge $next) { Sample-Line 'idle' $sw.Elapsed.TotalSeconds | ForEach-Object { Write-Host "$($_.Phase) t=$($_.T) pkg=$($_.Watts)W gpu=$($_.GpuW)W $($_.MHz)MHz $($_.TempC)C flags=$($_.LimitFlags) ec=$($_.Ec)" }; $next += 30 }
        Start-Sleep -Seconds 1
    }
    Write-Host "--- $label : load $LoadSeconds s ---"
    $rows = @(); $c0 = 0; $g0 = 0; $t0 = 0; $from = $LoadSeconds - 30
    if ($Load -ne 'cpu') { Write-Host "GPU load on: $([GpuLoad]::Start())" }
    if ($Load -ne 'gpu') { [CpuLoad]::Start([Environment]::ProcessorCount) }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        while ($sw.Elapsed.TotalSeconds -lt $LoadSeconds) {
            $r = Sample-Line 'load' $sw.Elapsed.TotalSeconds
            $rows += $r
            Write-Host "load t=$($r.T) pkg=$($r.Watts)W gpu=$($r.GpuW)W $($r.MHz)MHz $($r.TempC)C flags=$($r.LimitFlags) ec=$($r.Ec)"
            if ($t0 -eq 0 -and $sw.Elapsed.TotalSeconds -ge $from) { $c0 = [CpuLoad]::Total(); $g0 = [GpuLoad]::Total(); $t0 = $sw.Elapsed.TotalSeconds }
            Start-Sleep -Seconds $SampleSeconds
        }
        $c1 = [CpuLoad]::Total(); $g1 = [GpuLoad]::Total(); $t1 = $sw.Elapsed.TotalSeconds
        if ([GpuLoad]::Error()) { Write-Host "GPU LOAD ERROR: $([GpuLoad]::Error())" }
    } finally { [CpuLoad]::Stop(); [GpuLoad]::Stop() }
    [pscustomobject]@{
        Run = $label; Wmi = $names[(Read-Mode)]
        PeakW = ($rows | ForEach-Object { $_.Watts } | Measure-Object -Maximum).Maximum
        SustW = (Avg $rows 'Watts' $from); SustGpuW = (Avg $rows 'GpuW' $from); SustMHz = (Avg $rows 'MHz' $from)
        PeakC = ($rows | ForEach-Object { $_.TempC } | Measure-Object -Maximum).Maximum; SustC = (Avg $rows 'TempC' $from)
        Fan0 = (Fan-Avg $rows 1 $from); Fan1 = (Fan-Avg $rows 2 $from)
        Score = if ($t1 -gt $t0) { [math]::Round(($c1 - $c0) / ($t1 - $t0)) } else { -1 }
        GpuScore = if ($t1 -gt $t0) { [math]::Round(($g1 - $g0) / ($t1 - $t0)) } else { -1 }
    }
}

$original = $null; $wrote = $false; $results = @()
try {
    "KeepAwake previous state: 0x{0:X8}" -f [CpuLoad]::KeepAwake()
    if ($GpuSmokeSeconds -gt 0) {
        "GPU load on: $([GpuLoad]::Start())"
        for ($i = 1; $i -le $GpuSmokeSeconds; $i++) {
            $s = (Get-Counter '\Energy Meter(rapl_package0_pkg)\Power', '\Energy Meter(rapl_package0_pp1)\Power').CounterSamples
            "t=$i pkg=$([math]::Round(($s | Where-Object { $_.Path -match 'pkg' }).CookedValue / 1000, 1))W pp1=$([math]::Round(($s | Where-Object { $_.Path -match 'pp1' }).CookedValue / 1000, 1))W work=$([GpuLoad]::Total()) err=$([GpuLoad]::Error())"
        }
        return
    }
    $board = (Get-CimInstance Win32_BaseBoard).Product
    $bios = (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion
    "Board: $board  BIOS: $bios"
    if ($board -ne 'XINGYAO Series-P916F-PTL') { "STOP: unexpected board, nothing sent."; return }
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    "Admin: $admin"
    if (-not $admin) { "STOP: not elevated, nothing sent."; return }
    foreach ($m in $Mode) { if ($m -ne 'keep' -and -not $codes.ContainsKey($m)) { "STOP: unknown mode $m"; return } }
    Add-Type -AssemblyName System.Windows.Forms
    $line = [System.Windows.Forms.SystemInformation]::PowerStatus.PowerLineStatus
    "Power line: $line"
    if ($line -ne 'Online') { "STOP: plug in AC power first."; return }

    $script:inst = @(Get-CimInstance -Namespace root\WMI -ClassName OemWMIMethod)[0]
    $original = Read-Mode
    "Firmware mode now: $($names[$original])"
    "GCUService running: $([bool](Get-Process GCUService -ErrorAction SilentlyContinue))"

    "ESIF temperatures:"
    Get-CimInstance -Namespace root\WMI -ClassName EsifDeviceInformation -Property InstanceName, Temperature | ForEach-Object { "  $($_.InstanceName) = $($_.Temperature)" }

    $hash = (Get-FileHash "$env:WINDIR\System32\drivers\inpoutx64.sys" -Algorithm SHA256).Hash
    if ($bios -eq '1.01' -and $hash -eq 'F8965FDCE668692C3785AFA3559159F9A18287BC0D53ABB21902895A8ECF221B') {
        try { $script:ec = New-Object EcRead; "EC read first sample (mode,fan0,fan1): $(Read-Ec)" } catch { "EC read unavailable: $($_.Exception.Message)" }
    } else { "EC read skipped: BIOS or driver hash differs from the validated version." }

    foreach ($m in $Mode) {
        if ($m -eq 'keep') { $results += Run-One "keep-$($names[$original])" }
        else { Set-Mode $codes[$m]; $wrote = $true; $results += Run-One $m }
    }
} catch {
    "ERROR: $($_.Exception.Message)"
} finally {
    [CpuLoad]::Stop(); [GpuLoad]::Stop()
    if ($wrote -and $null -ne $original) {
        try { "Restoring $($names[$original])"; Set-Mode $original; "Final mode: $($names[(Read-Mode)])" } catch { "RESTORE ERROR: $($_.Exception.Message)" }
    }
    [void][CpuLoad]::AllowSleep()
    if ($script:ec) { $script:ec.Dispose() }
    if ($results) { "=== SUMMARY ==="; $results | Format-Table -Property * -AutoSize | Out-String -Width 200 | Write-Output }
    Stop-Transcript | Out-Null
}
