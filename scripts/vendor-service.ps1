# Temporarily turn the official Control Center background off, and put it back. Nothing is uninstalled.
#   -Action status  : show the current state (no admin needed)
#   -Action disable : save the current settings, then disable and stop the service and tray startup
#   -Action restore : put the saved settings back and start the service
# Only GCUBridge, its child GCUService and the Control Center tray and UI are touched. Drivers stay as they are.
param([ValidateSet('status', 'disable', 'restore')][string]$Action = 'status')
$ErrorActionPreference = 'Stop'

$service = 'GCUBridge'
$trayKey = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\ControlCenter3_4ptzsrs9z9qm8\ControlCenterTray'
$stateFile = Join-Path $env:LOCALAPPDATA 'xingyao-perf\vendor-state.txt'
$processes = 'GCUService', 'SystrayComponent', 'GamingCenter3_Cross'

function Show-Status {
    $s = Get-CimInstance Win32_Service -Filter "Name='$service'"
    "Service $service : $($s.State), start mode $($s.StartMode)"
    if (Test-Path $trayKey) { "Tray startup state: $((Get-ItemProperty $trayKey).State)  (2 = enabled, 1 = disabled by user)" } else { 'Tray startup state: not found' }
    foreach ($n in $processes) { "$n running: $([bool](Get-Process $n -ErrorAction SilentlyContinue))" }
    foreach ($d in 'UWACPIDriver', 'inpoutx64') { "Driver $d : $((Get-CimInstance Win32_SystemDriver -Filter "Name='$d'").State)" }
    "Saved settings: $(if (Test-Path $stateFile) { Get-Content $stateFile -Raw } else { 'none' })"
}

if ($Action -eq 'status') { Show-Status; return }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { 'STOP: run this from an administrator PowerShell. Nothing changed.'; return }

if ($Action -eq 'disable') {
    if (Test-Path $stateFile) { "STOP: saved settings already exist at $stateFile. Run -Action restore first. Nothing changed."; return }
    $mode = (Get-CimInstance Win32_Service -Filter "Name='$service'").StartMode
    $tray = if (Test-Path $trayKey) { (Get-ItemProperty $trayKey).State } else { '' }
    New-Item -ItemType Directory -Force (Split-Path $stateFile) | Out-Null
    Set-Content $stateFile "service=$mode`ntray=$tray"
    Set-Service $service -StartupType Disabled
    Stop-Service $service -Force
    foreach ($n in $processes) { Get-Process $n -ErrorAction SilentlyContinue | Stop-Process -Force }
    if ($tray -ne '') { Set-ItemProperty $trayKey -Name State -Value 1 }
    'Disabled. Restore with -Action restore.'
}
else {
    if (-not (Test-Path $stateFile)) { 'STOP: no saved settings, so nothing to restore.'; return }
    $saved = @{}
    Get-Content $stateFile | ForEach-Object { $k, $v = $_ -split '=', 2; $saved[$k] = $v }
    $type = switch ($saved['service']) { 'Auto' { 'Automatic' } 'Manual' { 'Manual' } default { 'Automatic' } }
    Set-Service $service -StartupType $type
    Start-Service $service
    if ($saved['tray'] -ne '' -and (Test-Path $trayKey)) { Set-ItemProperty $trayKey -Name State -Value ([int]$saved['tray']) }
    Remove-Item $stateFile
    'Restored. The official tray comes back at next sign-in.'
}
Show-Status
