# NOTE: on this machine (2026-11-12) every register read back as 0 and each mapping took about a minute, so this
# does not work here; see docs/protocol.md. Kept for reference.
# Reads the package power limits (PL1, PL2) that are in force through the processor's MMIO RAPL registers.
# Read only: it maps one 4 KB page of the processor thermal device (PCI 00:04.0, Intel IPF Processor Participant,
# device B01D) through the inpoutx64 driver already installed here, reads it and unmaps it. Nothing is written.
# Register layout as in Linux intel_rapl (processor_thermal_rapl): unit 0x5938, package energy 0x593C,
# package power limit 0x59A0 (same bit layout as MSR 0x606 / 0x611 / 0x610).
# The MSR copy of the limits (MSR 0x610) cannot be read with this driver; the lower of the two applies.
# Run in an administrator PowerShell:  .\scripts\read-pl.ps1 [-Samples 5]
# Results are also appended to %LOCALAPPDATA%\xingyao-perf\pl-read.log.
param([int]$Samples = 5)
$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Run this in an administrator PowerShell.' }
$driver = Join-Path $env:WINDIR 'System32\drivers\inpoutx64.sys'
if ((Get-FileHash $driver -Algorithm SHA256).Hash -ne 'F8965FDCE668692C3785AFA3559159F9A18287BC0D53ABB21902895A8ECF221B') { throw 'inpoutx64 driver differs from the verified version.' }

$dev = Get-CimInstance Win32_PnPEntity | Where-Object { $_.DeviceID -like 'PCI\VEN_8086&DEV_B01D*' } | Select-Object -First 1
if (-not $dev) { throw 'Processor thermal device (B01D) not found.' }
$mem = Get-CimAssociatedInstance -InputObject $dev -ResultClassName Win32_DeviceMemoryAddress | Select-Object -First 1
$base = [UInt64]$mem.StartingAddress
if ($mem.EndingAddress - $mem.StartingAddress + 1 -lt 0x6000) { throw "Unexpected register range $($mem.Name)." }

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class PlRead
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint creation, uint flags, IntPtr tpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] i, int il, byte[] o, int ol, out int n, IntPtr ov);
    const uint MapPhys = 0x9C40201C, UnmapPhys = 0x9C402020; // InpOut32 IOCTL_WINIO_MAPPHYSTOLIN / UNMAPPHYSADDR

    // Maps one page at a page-aligned physical address and returns the 32-bit values at the given offsets.
    public static uint[] Read(ulong page, int[] offsets)
    {
        using (var h = CreateFile(@"\\.\inpoutx64", 0xc0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            // tagPhysStruct: size, physical address, section handle, linear address, section object (5 x 64 bit)
            var s = new byte[40];
            BitConverter.GetBytes((ulong)0x1000).CopyTo(s, 0);
            BitConverter.GetBytes(page).CopyTo(s, 8);
            int n;
            if (!DeviceIoControl(h, MapPhys, s, 40, s, 40, out n, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            long lin = BitConverter.ToInt64(s, 24);
            if (lin == 0) throw new InvalidOperationException("mapping failed");
            try
            {
                var r = new uint[offsets.Length];
                for (int i = 0; i < offsets.Length; i++) r[i] = (uint)Marshal.ReadInt32(new IntPtr(lin + offsets[i]));
                return r;
            }
            finally { DeviceIoControl(h, UnmapPhys, s, 40, null, 0, out n, IntPtr.Zero); }
        }
    }
}
'@

function Window($bits, $tu) { $y = ($bits -shr 17) -band 0x1F; $z = ($bits -shr 22) -band 3; [math]::Pow(2, $y) * (1 + $z / 4) / [math]::Pow(2, $tu) }

$log = Join-Path $env:LOCALAPPDATA 'xingyao-perf\pl-read.log'
$mode = (Get-Content (Join-Path $env:LOCALAPPDATA 'xingyao-perf\settings.ini') -ErrorAction SilentlyContinue | Where-Object { $_ -like 'mode=*' }) -replace 'mode=', ''
$modeName = @{ '1' = 'office'; '2' = 'balanced'; '3' = 'performance' }[$mode]
$ac = (Get-CimInstance Win32_Battery).BatteryStatus -eq 2
$page = $base + 0x5000
"Device $($dev.DeviceID)`nRegisters at 0x{0:X}, official mode (from settings.ini): {1}, AC: {2}" -f $base, $modeName, $ac

$prev = $null
for ($i = 0; $i -lt $Samples; $i++) {
    $v = [PlRead]::Read($page, [int[]](0x938, 0x93C, 0x9A0, 0x9A4))
    $unit = $v[0]; $pu = $unit -band 0xF; $eu = ($unit -shr 8) -band 0x1F; $tu = ($unit -shr 16) -band 0xF
    $lo = $v[2]; $hi = $v[3]
    $pl1 = ($lo -band 0x7FFF) / [math]::Pow(2, $pu); $pl2 = ($hi -band 0x7FFF) / [math]::Pow(2, $pu)
    $meter = (Get-Counter '\Energy Meter(rapl_package0_pkg)\Power').CounterSamples[0].CookedValue / 1000
    $energyW = if ($prev) { (([long]$v[1] - [long]$prev[1] + 4294967296) % 4294967296) / [math]::Pow(2, $eu) / ((Get-Date) - $prevAt).TotalSeconds } else { [double]::NaN }
    $line = '{0:yyyy-MM-dd HH:mm:ss} mode={1} ac={2} unit=0x{3:X8} limit=0x{4:X8}{5:X8}  PL1={6:0.###} W enabled={7} clamp={8} window={9:0.###} s  PL2={10:0.###} W enabled={11} window={12:0.###} s  locked={13}  energy-register {14:0.0} W vs counter {15:0.0} W' -f `
        (Get-Date), $modeName, $ac, $unit, $hi, $lo, $pl1, (($lo -shr 15) -band 1), (($lo -shr 16) -band 1), (Window $lo $tu), $pl2, (($hi -shr 15) -band 1), (Window $hi $tu), (($hi -shr 31) -band 1), $energyW, $meter
    $line
    Add-Content -Path $log -Value $line
    $prev = $v; $prevAt = Get-Date
    Start-Sleep 1
}
'(The energy figures should roughly agree; that confirms the right registers were read.)'
