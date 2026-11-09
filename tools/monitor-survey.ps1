# Read-only survey of monitoring data sources. Writes nothing and sends no firmware commands.
"--- effect test running? ---"
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'effect-test' } | Select-Object ProcessId,CreationDate | Format-Table -AutoSize

"--- core topology ---"
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class Topo {
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetSystemCpuSetInformation(IntPtr info, uint len, out uint ret, IntPtr proc, uint flags);
  public static List<string> Get() {
    var r = new List<string>(); uint len; GetSystemCpuSetInformation(IntPtr.Zero,0,out len,IntPtr.Zero,0);
    IntPtr p = Marshal.AllocHGlobal((int)len); GetSystemCpuSetInformation(p,len,out len,IntPtr.Zero,0);
    int off = 0; while (off < len) { int size = Marshal.ReadInt32(p,off);
      int group = Marshal.ReadInt16(p,off+12); int lp = Marshal.ReadByte(p,off+14); int core = Marshal.ReadByte(p,off+15);
      int llc = Marshal.ReadByte(p,off+16); int eff = Marshal.ReadByte(p,off+18);
      r.Add(string.Format("LP{0} core={1} llc={2} efficiencyClass={3}",lp,core,llc,eff)); off += size; }
    Marshal.FreeHGlobal(p); return r; }
}
'@
[Topo]::Get()

"--- per-core frequency sample (MHz) ---"
(Get-Counter '\Processor Information(0,*)\Actual Frequency' -ErrorAction SilentlyContinue).CounterSamples | Where-Object { $_.InstanceName -notmatch '_Total' } | ForEach-Object { "$($_.InstanceName) = $([math]::Round($_.CookedValue))" }

"--- RAPL (mW) ---"
(Get-Counter '\Energy Meter(*)\Power').CounterSamples | ForEach-Object { "$($_.InstanceName) = $([math]::Round($_.CookedValue))" }

"--- thermal zones (two samples 3 s apart) ---"
1..2 | ForEach-Object { (Get-Counter '\Thermal Zone Information(*)\High Precision Temperature').CounterSamples | ForEach-Object { "$($_.InstanceName) = $($_.CookedValue)" }; if ($_ -eq 1) { Start-Sleep 3 } }

"--- GPU counters ---"
Get-Counter -ListSet * -ErrorAction SilentlyContinue | Where-Object { $_.CounterSetName -match 'GPU|Graphics|Intel|DPTF|DTT|IPF' } | ForEach-Object { "$($_.CounterSetName): " + (($_.Counter | Select-Object -First 6) -join ' | ') }

"--- Intel GPU libraries ---"
foreach ($n in 'ControlLib.dll','igcl64.dll','ze_loader.dll','ze_intel_gpu64.dll','igdgmm64.dll') { "$n : " + (Test-Path "$env:WINDIR\System32\$n") }

"--- other sensors in root\WMI ---"
(Get-CimClass -Namespace root\WMI -ClassName EsifDeviceInformation -ErrorAction SilentlyContinue).CimClassMethods | ForEach-Object { "EsifDeviceInformation method: $($_.Name)" }
Get-CimClass -Namespace root\WMI | Where-Object { $_.CimClassName -match '(?i)thermal|temperature|esif' } | Select-Object -ExpandProperty CimClassName
Get-Service | Where-Object { $_.Name -match '(?i)ipf|dptf|esif|dtt|IntelGFX|igfx|cplsp' } | Select-Object Name,Status | Format-Table -AutoSize
