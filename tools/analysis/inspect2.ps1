# Read-only: reflection-only metadata inspection. Nothing from the assembly is executed.
$root = (Get-ChildItem 'C:\Program Files\OEM' -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'AiStoneService\MyControlCenter\GCUService.exe') } | Select-Object -First 1).FullName
$dir = Join-Path $root 'AiStoneService\MyControlCenter'
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve({
    param($s, $e)
    $n = (New-Object Reflection.AssemblyName($e.Name)).Name
    $p = Join-Path $dir "$n.dll"
    if (Test-Path $p) { return [Reflection.Assembly]::ReflectionOnlyLoadFrom($p) }
    try { return [Reflection.Assembly]::ReflectionOnlyLoad($e.Name) } catch { return $null }
})
$asm = [Reflection.Assembly]::ReflectionOnlyLoadFrom("$dir\GCUService.exe")
try { $types = $asm.GetTypes() } catch [Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ } }
$F = [Reflection.BindingFlags]'Public,NonPublic,Instance,Static,DeclaredOnly'

"##### OperatingMode enum"
$t = $types | Where-Object { $_.FullName -eq 'Define.GCU2_Define+OperatingMode' }
$t.GetFields($F) | Where-Object { $_.IsLiteral } | ForEach-Object { "  $($_.Name) = $($_.GetRawConstantValue())" }

"##### WMIEC fields (constants / addresses)"
foreach ($tn in 'MyControlCenter.WMIEC','MyControlCenter.WMIBIOS') {
    $t = $types | Where-Object { $_.FullName -eq $tn }
    $t.GetFields($F) | Select-Object -First 30 | ForEach-Object { $v = ''; if ($_.IsLiteral) { $v = $_.GetRawConstantValue() }; "  $tn.$($_.Name) : $($_.FieldType.Name) $v" }
}

"##### WCF"
$wcf = $types | Where-Object { $_.Namespace -match 'WCF' -or $_.Name -match 'WCF|ICommunication|IService' }
foreach ($t in $wcf) {
    "=== $($t.FullName) interface=$($t.IsInterface)"
    $t.GetMethods($F) | Select-Object -First 70 | ForEach-Object { $ps = try { ($_.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', ' } catch { '?' }; "   $($_.ReturnType.Name) $($_.Name)($ps)" }
}

"##### methods anywhere with SetOperatingMode / SwitchMode"
foreach ($t in $types) {
    foreach ($m in $t.GetMethods($F)) {
        if ($m.Name -match '(?i)^(Set|Switch|Change|Apply).*(OperatingMode|PowerMode|ModeSwitch)|OperatingModeProfileIndex$') { "  $($t.FullName)::$($m.Name)" }
    }
}

"##### user-string heap scan"
$bytes = [IO.File]::ReadAllBytes("$dir\GCUService.exe")
$u = [Text.Encoding]::Unicode.GetString($bytes)
[regex]::Matches($u, '[\x20-\x7e]{4,120}') | ForEach-Object { $_.Value } | Where-Object { $_ -match '(?i)net\.pipe|net\.tcp|http://(localhost|127)|OemWMI|root\\+WMI|SELECT \*|\\\\\.\\|Global\\|GCUBridge|Pipe' } | Sort-Object -Unique | Select-Object -First 60
