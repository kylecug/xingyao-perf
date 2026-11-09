# Read-only: prints the EC charge thresholds and battery state, and appends them to a log.
# Only the three PM2 read queries F1 12 / F1 13 / F1 14 can be sent. Nothing is written to the charge settings.
# Protocol from clmslgs/open-revo ptl_pmc.rs (PR #166), validated there on BIOS 1.00 only. See docs/protocol.md.
# Run from an administrator PowerShell:  .\scripts\battery-read.ps1 -Label before-disable
param([string]$Label = 'manual')
$ErrorActionPreference = 'Stop'

$board = (Get-CimInstance Win32_BaseBoard).Product
$bios = (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion
if ($board -ne 'XINGYAO Series-P916F-PTL' -or $bios -notmatch '1\.01') { "STOP: board '$board' BIOS '$bios' is not the verified machine. Nothing sent."; return }
$hash = (Get-FileHash "$env:WINDIR\System32\drivers\inpoutx64.sys" -Algorithm SHA256).Hash
if ($hash -ne 'F8965FDCE668692C3785AFA3559159F9A18287BC0D53ABB21902895A8ECF221B') { 'STOP: inpoutx64 driver differs from the verified version. Nothing sent.'; return }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
public sealed class BatteryEc : IDisposable {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint creation, uint flags, IntPtr tpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] i, int il, byte[] o, int ol, out int n, IntPtr ov);
    readonly SafeFileHandle h; readonly Mutex mutex = new Mutex(false, @"Local\MechrevoPtlFanDiagnostic");
    public BatteryEc() {
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
    // F1 also has enable and disable arguments, so only the three read arguments are accepted.
    byte Query(byte arg) {
        if (arg != 0x12 && arg != 0x13 && arg != 0x14) throw new ArgumentException("query not allowed");
        Wait(2, 0);
        if ((In(0x6c) & 1) != 0) throw new InvalidOperationException("EC output pending, try again");
        Out(0x6c, 0xf1); Wait(2, 0); Out(0x68, arg); Wait(1, 1);
        return In(0x68);
    }
    public int[] Read() {
        bool got = false;
        try {
            try { got = mutex.WaitOne(3000); } catch (AbandonedMutexException) { got = true; }
            if (!got) throw new InvalidOperationException("EC channel busy");
            return new int[] { Query(0x12), Query(0x13), Query(0x14) };
        } finally { if (got) mutex.ReleaseMutex(); }
    }
    public void Dispose() { h.Dispose(); mutex.Dispose(); }
}
'@

$ec = New-Object BatteryEc
try { $v = $ec.Read() } finally { $ec.Dispose() }
$b = Get-CimInstance -Namespace root\wmi -ClassName BatteryStatus | Select-Object -First 1
$percent = (Get-CimInstance Win32_Battery | Select-Object -First 1).EstimatedChargeRemaining
$service = (Get-CimInstance Win32_Service -Filter "Name='GCUBridge'").State
$line = '{0:yyyy-MM-dd HH:mm:ss} label={1} enabled={2} lower={3} upper={4} battery={5}% ac={6} charging={7} chargeRateMw={8} GCUBridge={9}' -f (Get-Date), $Label, $v[0], $v[1], $v[2], $percent, $b.PowerOnline, $b.Charging, $b.ChargeRate, $service
$line
$log = Join-Path $env:LOCALAPPDATA 'xingyao-perf\battery-read.log'
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
Add-Content $log $line
"Saved to $log"
