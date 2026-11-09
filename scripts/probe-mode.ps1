# Read-only probe. Sends exactly one firmware query: [3, 9] = "get performance mode".
# The script contains no mode-write command.
$ErrorActionPreference = 'Stop'
Start-Transcript -Path (Join-Path $PSScriptRoot 'probe-result.txt') -Force | Out-Null
try {
    $board = (Get-CimInstance Win32_BaseBoard).Product
    $bios = (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion
    "Board: $board"
    "BIOS : $bios"
    if ($board -ne 'XINGYAO Series-P916F-PTL') { "STOP: unexpected board, nothing sent."; return }

    $oem = Get-ChildItem 'C:\Program Files\OEM' -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'AiStoneService\MyControlCenter\UserPofiles\MainOption.json') } | Select-Object -First 1
    $cc = $null
    if ($oem) {
        $json = Get-Content (Join-Path $oem.FullName 'AiStoneService\MyControlCenter\UserPofiles\MainOption.json') -Raw | ConvertFrom-Json
        $cc = [int]$json.OperatingMode
        "Control Center OperatingMode: $cc  (0=Office 1=Gaming 2=Turbo 3=Custom)"
    }

    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    "Admin: $admin"
    if (-not $admin) { "STOP: not elevated, nothing sent. Run from an administrator PowerShell."; return }

    $method = (Get-CimClass -Namespace root\WMI -ClassName OemWMIMethod).CimClassMethods['OemWMIfun']
    $inParam = $method.Parameters['u8Input']
    $maxIn = $inParam.Qualifiers['MAX'].Value
    "u8Input MAX qualifier: $maxIn"
    $size = if ($maxIn -gt 0) { [int]$maxIn } else { 64 }

    $instances = @(Get-CimInstance -Namespace root\WMI -ClassName OemWMIMethod)
    "Instances: $($instances.Count)"
    $instances | ForEach-Object { "  $($_.InstanceName) Active=$($_.Active)" }
    if ($instances.Count -eq 0) { "STOP: no instance."; return }

    $buf = New-Object byte[] $size
    $buf[0] = 3
    $buf[1] = 9
    "Sending [3, 9] padded to $size bytes"
    $r = Invoke-CimMethod -InputObject $instances[0] -MethodName OemWMIfun -Arguments @{ u8Input = [byte[]]$buf }
    "ReturnValue: $($r.ReturnValue)"
    "u32Resrved : $($r.u32Resrved)"
    $out = [byte[]]$r.u8Output
    "Output length: $($out.Length)"
    "First 16 bytes: " + (($out | Select-Object -First 16 | ForEach-Object { '{0:X2}' -f $_ }) -join ' ')

    $names = @{ 1 = 'Office'; 2 = 'Balanced'; 3 = 'Turbo' }
    $mode = [int]$out[1]
    "Status byte[0]: $($out[0]) (expected 0)"
    "Mode byte[1]  : $mode = $($names[$mode])"
    if ($null -ne $cc) {
        if ($out[0] -eq 0 -and $mode -eq ($cc + 1)) { "RESULT: MATCH with Control Center (assuming Gaming = Balanced)" }
        else { "RESULT: MISMATCH or unexpected reply. Do not proceed to write tests." }
    }
} catch {
    "ERROR: $($_.Exception.Message)"
} finally {
    Stop-Transcript | Out-Null
}
