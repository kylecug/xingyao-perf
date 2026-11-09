# Manual fan speed test through the PM2 EC port. Run from an administrator PowerShell.
#   -Action status                       : read fan RPM, EC fan mode and temperature (read only)
#   -Action set -Percent 100 -Seconds 60 : hold both fans at a fixed PWM, print readings every second, then restore auto
#   -Action auto                         : restore the EC's automatic fan curve
# Commands from Kurisu934/FanControl.MechrevoPtl (MIT), validated there on P916F-PTL BIOS 1.01:
#   D6 <pwm 0-255> shared PWM for both fans, D5 10 back to automatic. NOT yet tested on this machine.
# Manual PWM bypasses the EC temperature curve, so the script restores auto when it ends, on Ctrl+C,
# and early if the temperature reaches -MaxTempC. It re-sends the PWM every 5 seconds while holding.
param([ValidateSet('status', 'set', 'auto')][string]$Action = 'status', [ValidateRange(30, 100)][int]$Percent = 100,
      [ValidateRange(5, 600)][int]$Seconds = 60, [int]$MaxTempC = 90)
$ErrorActionPreference = 'Stop'

$board = (Get-CimInstance Win32_BaseBoard).Product
$bios = (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion
if ($board -ne 'XINGYAO Series-P916F-PTL' -or $bios -notmatch '1\.01') { "STOP: board '$board' BIOS '$bios' is not the verified machine. Nothing sent."; return }
$hash = (Get-FileHash "$env:WINDIR\System32\drivers\inpoutx64.sys" -Algorithm SHA256).Hash
if ($hash -ne 'F8965FDCE668692C3785AFA3559159F9A18287BC0D53ABB21902895A8ECF221B') { 'STOP: inpoutx64 driver differs from the verified version. Nothing sent.'; return }
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { 'STOP: run this from an administrator PowerShell. Nothing sent.'; return }

# A type can be added only once per PowerShell session, so a second run in the same window reuses it.
if (-not ('FanTestEc' -as [type])) { Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
public sealed class FanTestEc : IDisposable {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint creation, uint flags, IntPtr tpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] i, int il, byte[] o, int ol, out int n, IntPtr ov);
    readonly SafeFileHandle h; readonly Mutex mutex = new Mutex(false, @"Local\MechrevoPtlFanDiagnostic");
    public string Note = "";
    public FanTestEc() {
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
    // Only these commands are accepted: reads D5 16-19 and E7 11, auto D5 10, shared PWM D6.
    byte Send(byte cmd, byte arg, bool reply) {
        bool ok = (cmd == 0xd5 && arg >= 0x16 && arg <= 0x19) || (cmd == 0xe7 && arg == 0x11) || (cmd == 0xd5 && arg == 0x10) || cmd == 0xd6;
        if (!ok) throw new ArgumentException("command not allowed");
        Wait(2, 0);
        if ((In(0x6c) & 1) != 0) throw new InvalidOperationException("EC output pending, nothing sent");
        Out(0x6c, cmd); Wait(2, 0); Out(0x68, arg);
        if (reply) { Wait(1, 1); return In(0x68); }
        // Whether these write commands reply is not documented. Read any byte so it is not taken as the next answer.
        Wait(2, 0); Thread.Sleep(30);
        if ((In(0x6c) & 1) != 0) Note += string.Format("reply 0x{0:X2} to {1:X2} {2:X2}; ", In(0x68), cmd, arg);
        return 0;
    }
    T Locked<T>(Func<T> f) {
        bool got = false;
        try {
            try { got = mutex.WaitOne(3000); } catch (AbandonedMutexException) { got = true; }
            if (!got) throw new InvalidOperationException("EC channel busy");
            return f();
        } finally { if (got) mutex.ReleaseMutex(); }
    }
    int Rpm(byte lo, byte hi) {
        for (int a = 0; a < 3; a++) { byte b = Send(0xd5, hi, true), l = Send(0xd5, lo, true), c = Send(0xd5, hi, true); if (b == c) return l + 256 * c; }
        return -1;
    }
    // EC fan mode (same value as the performance mode), fan 0 RPM, fan 1 RPM.
    public int[] Read() { return Locked(() => new int[] { Send(0xe7, 0x11, true), Rpm(0x18, 0x19), Rpm(0x16, 0x17) }); }
    public void SetPwm(int percent) { Locked(() => Send(0xd6, (byte)Math.Round(percent * 255.0 / 100), false)); }
    public void Auto() { Locked(() => Send(0xd5, 0x10, false)); }
    public void Dispose() { h.Dispose(); mutex.Dispose(); }
}
'@ }

function Read-Temp {
    $v = @(Get-CimInstance -Namespace root\WMI -ClassName EsifDeviceInformation -Property InstanceName, Temperature | Where-Object { $_.InstanceName -match '_0$' -and $_.Temperature -gt 0 -and $_.Temperature -lt 125 } | ForEach-Object { [int]$_.Temperature })
    if ($v.Count) { return ($v | Measure-Object -Maximum).Maximum }
    return -1
}
function Show($ec, $label) {
    $r = $ec.Read()
    '{0:HH:mm:ss} {1,-8} fan0={2,5} fan1={3,5} rpm  ecMode={4}  {5} C' -f (Get-Date), $label, $r[1], $r[2], $r[0], (Read-Temp)
}

$ec = New-Object FanTestEc
try {
    if ($Action -eq 'status') { Show $ec 'status'; return }
    if ($Action -eq 'auto') { $ec.Auto(); "Sent D5 10 (auto)."; for ($i = 0; $i -lt 5; $i++) { Show $ec 'auto'; Start-Sleep 1 }; return }

    Show $ec 'before'
    $pwm = [math]::Round($Percent * 255 / 100)
    "Holding both fans at $Percent% (D6 $('{0:X2}' -f [int]$pwm)) for $Seconds s. Ctrl+C stops early and restores auto."
    $sw = [Diagnostics.Stopwatch]::StartNew(); $next = 0
    while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
        if ($sw.Elapsed.TotalSeconds -ge $next) { $ec.SetPwm($Percent); $next += 5 }
        Show $ec "$Percent%"
        $t = Read-Temp
        if ($t -ge $MaxTempC) { "Temperature $t C reached $MaxTempC C, stopping early."; break }
        Start-Sleep 1
    }
} finally {
    if ($Action -eq 'set') {
        try { $ec.Auto(); 'Restored auto (D5 10).'; for ($i = 0; $i -lt 8; $i++) { Show $ec 'auto'; Start-Sleep 1 } }
        catch { "RESTORE FAILED: $($_.Exception.Message). Run: .\scripts\fan-test.ps1 -Action auto" }
    }
    if ($ec.Note) { "NOTE: $($ec.Note)" }
    $ec.Dispose()
}
