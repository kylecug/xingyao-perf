# Battery charge limit through the PM2 EC port. Run from an administrator PowerShell.
#   -Action status            : read only (F1 12/13/14)
#   -Action set -Limit 80     : save the current thresholds once, then write the limit (60 or 80)
#   -Action restore           : write back the thresholds saved before the first set
# Protocol and write order from clmslgs/open-revo ptl_pmc.rs (PR #166), validated there on BIOS 1.00 only.
# The write commands have NOT been verified on this machine (BIOS 1.01). See docs/protocol.md.
param([ValidateSet('status', 'set', 'restore')][string]$Action = 'status', [ValidateSet(60, 80)][int]$Limit = 80)
$ErrorActionPreference = 'Stop'

$board = (Get-CimInstance Win32_BaseBoard).Product
$bios = (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion
if ($board -ne 'XINGYAO Series-P916F-PTL' -or $bios -notmatch '1\.01') { "STOP: board '$board' BIOS '$bios' is not the verified machine. Nothing sent."; return }
$hash = (Get-FileHash "$env:WINDIR\System32\drivers\inpoutx64.sys" -Algorithm SHA256).Hash
if ($hash -ne 'F8965FDCE668692C3785AFA3559159F9A18287BC0D53ABB21902895A8ECF221B') { 'STOP: inpoutx64 driver differs from the verified version. Nothing sent.'; return }
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { 'STOP: run this from an administrator PowerShell. Nothing sent.'; return }

# A type can be added only once per PowerShell session, so a second run in the same window reuses it.
if (-not ('BatteryLimitEc' -as [type])) { Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
public sealed class BatteryLimitEc : IDisposable {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint creation, uint flags, IntPtr tpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] i, int il, byte[] o, int ol, out int n, IntPtr ov);
    readonly SafeFileHandle h; readonly Mutex mutex = new Mutex(false, @"Local\MechrevoPtlFanDiagnostic");
    public string Note = "";
    public BatteryLimitEc() {
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
    // Only the battery commands are accepted. A partly sent command is never retried.
    byte Send(byte cmd, byte arg, bool reply) {
        bool ok = (cmd == 0xf1 && arg >= 0x10 && arg <= 0x14) || ((cmd == 0xf2 || cmd == 0xf3) && arg <= 100);
        if (!ok) throw new ArgumentException("command not allowed");
        Wait(2, 0);
        if ((In(0x6c) & 1) != 0) throw new InvalidOperationException("EC output pending before command, nothing sent for it");
        Out(0x6c, cmd); Wait(2, 0); Out(0x68, arg);
        if (reply) { Wait(1, 1); return In(0x68); }
        // These commands should not reply. If one does, read it so it is not mistaken for the next answer.
        Wait(2, 0); Thread.Sleep(30);
        if ((In(0x6c) & 1) != 0) Note += string.Format("unexpected reply 0x{0:X2} to {1:X2} {2:X2}; ", In(0x68), cmd, arg);
        return 0;
    }
    void Lock(Action a) {
        bool got = false;
        try {
            try { got = mutex.WaitOne(3000); } catch (AbandonedMutexException) { got = true; }
            if (!got) throw new InvalidOperationException("EC channel busy");
            a();
        } finally { if (got) mutex.ReleaseMutex(); }
    }
    public int[] Read() { int[] r = null; Lock(() => { r = new int[] { Send(0xf1, 0x12, true), Send(0xf1, 0x13, true), Send(0xf1, 0x14, true) }; }); return r; }
    // Order from open-revo: disable, upper, lower, enable. Echoes must match.
    public void Write(bool enabled, int lower, int upper) {
        Lock(() => {
            Send(0xf1, 0x10, false);
            int e = Send(0xf3, (byte)upper, true); if (e != upper) throw new InvalidOperationException("upper echo " + e + ", wanted " + upper);
            e = Send(0xf2, (byte)lower, true); if (e != lower) throw new InvalidOperationException("lower echo " + e + ", wanted " + lower);
            if (enabled) Send(0xf1, 0x11, false);
        });
    }
    public void Dispose() { h.Dispose(); mutex.Dispose(); }
}
'@ }

$stateFile = Join-Path $env:LOCALAPPDATA 'xingyao-perf\battery-original.txt'
$log = Join-Path $env:LOCALAPPDATA 'xingyao-perf\battery-read.log'
function Show($label, $v) {
    $b = Get-CimInstance -Namespace root\wmi -ClassName BatteryStatus | Select-Object -First 1
    $pct = (Get-CimInstance Win32_Battery | Select-Object -First 1).EstimatedChargeRemaining
    $line = '{0:yyyy-MM-dd HH:mm:ss} label={1} enabled={2} lower={3} upper={4} battery={5}% ac={6} charging={7} discharging={8} chargeRateMw={9}' -f (Get-Date), $label, $v[0], $v[1], $v[2], $pct, $b.PowerOnline, $b.Charging, $b.Discharging, $b.ChargeRate
    $line; New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null; Add-Content $log $line
}
function Same($v, $e, $l, $u) { return $v[0] -eq [int]$e -and $v[1] -eq $l -and $v[2] -eq $u }
function Write-Checked($ec, $e, $l, $u, $before) {
    try {
        $ec.Write($e, $l, $u); Start-Sleep -Milliseconds 300
        $now = $ec.Read()
        if (-not (Same $now $e $l $u)) { throw "read-back $($now -join ',') does not match $([int]$e),$l,$u" }
        return $now
    } catch {
        $err = $_.Exception.Message
        "WRITE FAILED: $err"
        try { $ec.Write([bool]$before[0], $before[1], $before[2]); "Rolled back to $($before -join ','), read-back $($ec.Read() -join ',')" } catch { "ROLLBACK FAILED: $($_.Exception.Message). Run -Action restore, or restore the official background." }
        throw $err
    }
}

$ec = New-Object BatteryLimitEc
try {
    $v = $ec.Read()
    if ($v[0] -gt 1 -or $v[1] -gt 100 -or $v[2] -gt 100) { "STOP: unexpected reply $($v -join ','). Nothing written."; return }
    if ($Action -eq 'status') { Show 'status' $v; return }
    if ($Action -eq 'set') {
        if (Same $v $true $Limit 100) { 'Already set.'; Show 'set-unchanged' $v; return }
        if (-not (Test-Path $stateFile)) { Set-Content $stateFile ($v -join ','); "Saved original thresholds $($v -join ',') to $stateFile" }
        Show 'before-set' $v
        $now = Write-Checked $ec $true $Limit 100 $v
        Show "set-$Limit" $now
    } else {
        if (-not (Test-Path $stateFile)) { 'STOP: no saved thresholds, so nothing to restore.'; return }
        $o = (Get-Content $stateFile -Raw).Trim() -split ',' | ForEach-Object { [int]$_ }
        Show 'before-restore' $v
        $now = Write-Checked $ec ([bool]$o[0]) $o[1] $o[2] $v
        Remove-Item $stateFile
        Show 'restored' $now
    }
    if ($ec.Note) { "NOTE: $($ec.Note)" }
} finally { $ec.Dispose() }
