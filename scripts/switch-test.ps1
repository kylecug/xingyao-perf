# Write test: switches firmware mode to the target, observes, then restores the original mode.
# Only ever sends [3,9] (read) and [3,10,<1|2|3>] (set mode).
param([ValidateSet('office','turbo')][string]$Target = 'office', [int]$HoldSeconds = 15)
$ErrorActionPreference = 'Stop'
Start-Transcript -Path (Join-Path $PSScriptRoot "switch-result-$Target.txt") -Force | Out-Null

$names = @{ 1 = 'Office'; 2 = 'Balanced'; 3 = 'Turbo' }
$acks = @{ 1 = 0x94; 2 = 0x95; 3 = 0x96 }
$script:inst = $null

function Call-Fw([byte[]]$bytes) {
    $buf = New-Object byte[] 64
    [Array]::Copy($bytes, $buf, $bytes.Length)
    $r = Invoke-CimMethod -InputObject $script:inst -MethodName OemWMIfun -Arguments @{ u8Input = [byte[]]$buf }
    return [byte[]]$r.u8Output
}
function Read-Mode {
    $o = Call-Fw ([byte[]](3, 9))
    if ($o[0] -ne 0) { throw "read status $($o[0])" }
    return [int]$o[1]
}
function Set-Mode([int]$m) {
    if ($m -notin 1, 2, 3) { throw "bad mode $m" }
    $o = Call-Fw ([byte[]](3, 10, $m))
    "SET $($names[$m]): ack=0x{0:X2} expected=0x{1:X2}" -f $o[0], $acks[$m]
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        $now = Read-Mode
        if ($now -eq $m) { "  read-back OK after $($sw.ElapsedMilliseconds) ms"; return }
        Start-Sleep -Milliseconds 20
    } while ($sw.ElapsedMilliseconds -lt 3000)
    "  read-back FAILED: mode is $($names[$now]) after 3000 ms"
}
function Cc-Mode {
    $oem = Get-ChildItem 'C:\Program Files\OEM' -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'AiStoneService\MyControlCenter\UserPofiles\MainOption.json') } | Select-Object -First 1
    $p = Join-Path $oem.FullName 'AiStoneService\MyControlCenter\UserPofiles\MainOption.json'
    "  CC file: OperatingMode=$((Get-Content $p -Raw | ConvertFrom-Json).OperatingMode) written=$((Get-Item $p).LastWriteTime)"
}

$original = $null
try {
    $board = (Get-CimInstance Win32_BaseBoard).Product
    "Board: $board  BIOS: $((Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion)"
    if ($board -ne 'XINGYAO Series-P916F-PTL') { "STOP: unexpected board, nothing sent."; return }
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    "Admin: $admin"
    if (-not $admin) { "STOP: not elevated, nothing sent."; return }
    "GCUService running: $([bool](Get-Process GCUService -ErrorAction SilentlyContinue))"

    $script:inst = @(Get-CimInstance -Namespace root\WMI -ClassName OemWMIMethod)[0]
    $original = Read-Mode
    if ($original -notin 1, 2, 3) { "STOP: unknown mode $original, nothing written."; $original = $null; return }
    "Original mode: $($names[$original])"
    Cc-Mode

    $t = if ($Target -eq 'turbo') { 3 } else { 1 }
    if ($t -eq $original) { "STOP: already in target mode."; return }
    Set-Mode $t
    "Holding $HoldSeconds s, sampling every second:"
    for ($i = 1; $i -le $HoldSeconds; $i++) {
        Start-Sleep -Seconds 1
        "  t=$i s mode=$($names[(Read-Mode)])"
    }
    Cc-Mode
} catch {
    "ERROR: $($_.Exception.Message)"
} finally {
    if ($null -ne $original -and $null -ne $script:inst) {
        try {
            "Restoring $($names[$original])"
            Set-Mode $original
            "Final mode: $($names[(Read-Mode)])"
            Cc-Mode
        } catch { "RESTORE ERROR: $($_.Exception.Message)" }
    }
    Stop-Transcript | Out-Null
}
