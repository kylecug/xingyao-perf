# Effect test: full CPU load in each firmware mode, measuring RAPL package power and throughput.
# Only ever sends [3,9] (read) and [3,10,<1|2|3>] (set mode). Restores the original mode at the end.
param([int]$LoadSeconds = 75, [int]$CoolSeconds = 20)
$ErrorActionPreference = 'Stop'
Start-Transcript -Path (Join-Path $PSScriptRoot 'effect-result.txt') -Force | Out-Null

Add-Type -TypeDefinition @'
using System; using System.Threading;
public static class CpuLoad {
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
    public static uint KeepAwake() { return SetThreadExecutionState(0x80000001u); }
    public static uint AllowSleep() { return SetThreadExecutionState(0x80000000u); }
    static volatile bool run; static long[] counts; static Thread[] ts;
    public static void Start(int n) {
        run = true; counts = new long[n * 16]; ts = new Thread[n];
        for (int i = 0; i < n; i++) { int k = i * 16;
            ts[i] = new Thread(() => { ulong x = 88172645463325252UL + (ulong)k; long c = 0;
                while (run) { for (int j = 0; j < 100000; j++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; } c++; Volatile.Write(ref counts[k], c); } });
            ts[i].IsBackground = true; ts[i].Start(); } }
    public static long Total() { long t = 0; for (int i = 0; i < counts.Length; i += 16) t += Volatile.Read(ref counts[i]); return t; }
    public static void Stop() { run = false; if (ts != null) foreach (var t in ts) t.Join(); }
}
'@

$names = @{ 1 = 'Office'; 2 = 'Balanced'; 3 = 'Turbo' }
$acks = @{ 1 = 0x94; 2 = 0x95; 3 = 0x96 }
$script:inst = $null

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
function Measure-Mode([int]$m) {
    Set-Mode $m
    Start-Sleep -Seconds $CoolSeconds
    [CpuLoad]::Start([Environment]::ProcessorCount)
    $sw = [Diagnostics.Stopwatch]::StartNew(); $pw = @(); $fq = @(); $c0 = 0; $t0 = 0
    try {
        while ($sw.Elapsed.TotalSeconds -lt $LoadSeconds) {
            $s = (Get-Counter '\Energy Meter(rapl_package0_pkg)\Power', '\Processor Information(_Total)\Actual Frequency' -ErrorAction Stop).CounterSamples
            $w = [double]($s | Where-Object { $_.Path -match 'energy meter' }).CookedValue / 1000
            $f = [double]($s | Where-Object { $_.Path -match 'actual frequency' }).CookedValue
            $pw += , @($sw.Elapsed.TotalSeconds, $w); $fq += , @($sw.Elapsed.TotalSeconds, $f)
            if ($c0 -eq 0 -and $sw.Elapsed.TotalSeconds -ge ($LoadSeconds - 30)) { $c0 = [CpuLoad]::Total(); $t0 = $sw.Elapsed.TotalSeconds }
        }
        $c1 = [CpuLoad]::Total(); $t1 = $sw.Elapsed.TotalSeconds
    } finally { [CpuLoad]::Stop() }
    $last = { param($a) ($a | Where-Object { $_[0] -ge ($LoadSeconds - 30) } | ForEach-Object { $_[1] } | Measure-Object -Average).Average }
    $peak = ($pw | ForEach-Object { $_[1] } | Measure-Object -Maximum).Maximum
    $modeNow = Read-Mode
    [pscustomobject]@{ Mode = $names[$m]; Readback = $names[$modeNow]; PeakW = [math]::Round($peak, 1); SustainedW = [math]::Round((& $last $pw), 1); SustainedMHz = [math]::Round((& $last $fq)); Score = [math]::Round(($c1 - $c0) / ($t1 - $t0)) }
}

$original = $null; $results = @()
try {
    "KeepAwake previous state: 0x{0:X8} (non-zero means the request succeeded)" -f [CpuLoad]::KeepAwake()
    $board = (Get-CimInstance Win32_BaseBoard).Product
    "Board: $board  BIOS: $((Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion)"
    if ($board -ne 'XINGYAO Series-P916F-PTL') { "STOP: unexpected board, nothing sent."; return }
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    "Admin: $admin"
    if (-not $admin) { "STOP: not elevated, nothing sent."; return }
    Add-Type -AssemblyName System.Windows.Forms
    $line = [System.Windows.Forms.SystemInformation]::PowerStatus.PowerLineStatus
    "Power line: $line"
    if ($line -ne 'Online') { "STOP: plug in AC power first."; return }

    $script:inst = @(Get-CimInstance -Namespace root\WMI -ClassName OemWMIMethod)[0]
    $original = Read-Mode
    if ($original -notin 1, 2, 3) { "STOP: unknown mode $original."; $original = $null; return }
    "Original mode: $($names[$original])"
    foreach ($m in 2, 1, 3) {
        $r = Measure-Mode $m
        $results += $r
        $r | Format-List | Out-String | Write-Output
    }
} catch {
    "ERROR: $($_.Exception.Message)"
} finally {
    [CpuLoad]::Stop()
    if ($null -ne $original -and $null -ne $script:inst) {
        try { "Restoring $($names[$original])"; Set-Mode $original; "Final mode: $($names[(Read-Mode)])" } catch { "RESTORE ERROR: $($_.Exception.Message)" }
    }
    [void][CpuLoad]::AllowSleep()
    if ($results) { "=== SUMMARY ==="; $results | Format-Table -AutoSize | Out-String | Write-Output }
    Stop-Transcript | Out-Null
}
