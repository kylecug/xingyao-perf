# Measures the three Windows power modes (Best power efficiency, Balanced, Best performance) under a
# one-thread and an all-thread CPU load: package power and core frequency. Needs no admin rights and writes
# nothing to firmware or the EC. The power mode in use before the test is put back at the end.
#   .\scripts\power-mode-test.ps1 [-WarmSeconds 15] [-SampleSeconds 15]
param([int]$WarmSeconds = 15, [int]$SampleSeconds = 15)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class PmTest
{
    [DllImport("powrprof.dll")] public static extern uint PowerGetActualOverlayScheme(out Guid g);
    [DllImport("powrprof.dll")] public static extern uint PowerSetActiveOverlayScheme(Guid g);
    static volatile bool run;
    static Thread[] threads;
    public static void Start(int n)
    {
        run = true;
        threads = new Thread[n];
        for (int i = 0; i < n; i++)
        {
            threads[i] = new Thread(() => { double x = 1; while (run) { for (int k = 0; k < 100000; k++) x = x * 1.0000001 + 0.5; } }) { IsBackground = true };
            threads[i].Start();
        }
    }
    public static void Stop() { run = false; if (threads != null) foreach (var t in threads) t.Join(); }
}
'@

$modes = [ordered]@{
    'Best power efficiency' = [Guid]'961cc777-2547-4f9d-8174-7d86181b8a7a'
    'Balanced'              = [Guid]::Empty
    'Best performance'      = [Guid]'ded574b5-45a0-4f42-8737-46345c09c238'
}
$all = [Environment]::ProcessorCount
$original = [Guid]::Empty
if ([PmTest]::PowerGetActualOverlayScheme([ref]$original) -ne 0) { throw 'cannot read the power mode' }
"Power mode before the test: $original. Logical processors: $all. AC: $((Get-CimInstance Win32_Battery).BatteryStatus -eq 2)"

function Measure-Load([int]$threads) {
    [PmTest]::Start($threads)
    try {
        Start-Sleep $WarmSeconds
        $w = @(); $f = @()
        $counters = '\Energy Meter(rapl_package0_pkg)\Power', '\Processor Information(*)\Actual Frequency'
        $samples = Get-Counter -Counter $counters -SampleInterval 1 -MaxSamples $SampleSeconds
        foreach ($s in $samples) {
            $w += ($s.CounterSamples | Where-Object { $_.Path -like '*energy meter*' }).CookedValue / 1000
            $cores = $s.CounterSamples | Where-Object { $_.Path -like '*actual frequency*' -and $_.InstanceName -notlike '*_total*' }
            $f += if ($threads -eq 1) { ($cores | Measure-Object CookedValue -Maximum).Maximum } else { ($cores | Measure-Object CookedValue -Average).Average }
        }
        [pscustomobject]@{ W = ($w | Measure-Object -Average).Average; MHz = ($f | Measure-Object -Average).Average }
    } finally { [PmTest]::Stop() }
}

try {
    foreach ($name in $modes.Keys) {
        if ([PmTest]::PowerSetActiveOverlayScheme($modes[$name]) -ne 0) { throw "cannot set $name" }
        Start-Sleep 5
        foreach ($n in 1, $all) {
            $r = Measure-Load $n
            '{0,-22} {1,2} threads: {2,5:0.0} W, {3} {4,5:0} MHz' -f $name, $n, $r.W, $(if ($n -eq 1) { 'fastest core' } else { 'mean core   ' }), $r.MHz
            Start-Sleep 10 # cool down between loads
        }
    }
} finally {
    [void][PmTest]::PowerSetActiveOverlayScheme($original)
    "Power mode restored to $original."
}
