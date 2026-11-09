param([string]$Path, [string]$StringPattern = '.', [string]$TypePattern = '', [string]$MethodPattern = '.')
# Read-only: reflection-only metadata/IL inspection. Nothing from the assembly is executed.
$dir = Split-Path $Path
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve({
    param($s, $e)
    $n = (New-Object Reflection.AssemblyName($e.Name)).Name
    foreach ($x in 'dll','exe') { $p = Join-Path $dir "$n.$x"; if (Test-Path -LiteralPath $p) { return [Reflection.Assembly]::ReflectionOnlyLoadFrom($p) } }
    try { return [Reflection.Assembly]::ReflectionOnlyLoad($e.Name) } catch { return $null }
})
try { $asm = [Reflection.Assembly]::ReflectionOnlyLoadFrom($Path) } catch { "not loadable: $($_.Exception.Message)"; return }
"Assembly: $($asm.FullName)"
try { $types = $asm.GetTypes() } catch [Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ } }
"Types: $($types.Count)"
$mod = $asm.ManifestModule
$F = [Reflection.BindingFlags]'Public,NonPublic,Instance,Static,DeclaredOnly'
$ops = @{}
foreach ($fld in [Reflection.Emit.OpCodes].GetFields('Public,Static')) { $o = $fld.GetValue($null); $ops[[uint16]($o.Value -band 0xFFFF)] = $o }
function Disasm($mb) {
    try { $body = $mb.GetMethodBody() } catch { return }
    if (-not $body) { return }
    $il = $body.GetILAsByteArray(); $i = 0
    while ($i -lt $il.Length) {
        $v = [uint16]$il[$i]; $i++
        if ($v -eq 0xFE) { $v = [uint16](0xFE00 -bor $il[$i]); $i++ }
        $op = $ops[$v]; if (-not $op) { continue }
        switch ($op.OperandType.ToString()) {
            'InlineNone' { if ($op.Name -match '^ldc\.i4') { "    $($op.Name)" } }
            'ShortInlineI' { "    $($op.Name) $($il[$i])"; $i += 1 }
            'ShortInlineBrTarget' { $i += 1 }
            'ShortInlineVar' { $i += 1 }
            'InlineVar' { $i += 2 }
            'ShortInlineR' { $i += 4 }
            'InlineI' { "    $($op.Name) 0x{0:X}" -f [BitConverter]::ToInt32($il, $i); $i += 4 }
            'InlineBrTarget' { $i += 4 }
            'InlineR' { $i += 8 }
            'InlineI8' { $i += 8 }
            'InlineSwitch' { $n = [BitConverter]::ToInt32($il, $i); $i += 4 + 4 * $n }
            'InlineString' { $t = [BitConverter]::ToInt32($il, $i); $i += 4; try { "    ldstr `"$($mod.ResolveString($t))`"" } catch {} }
            default {
                $t = [BitConverter]::ToInt32($il, $i); $i += 4
                if ($op.Name -match 'call|newobj') {
                    try {
                        $g = if ($mb.DeclaringType.IsGenericType) { $mb.DeclaringType.GetGenericArguments() } else { $null }
                        $h = if ($mb.IsGenericMethod) { $mb.GetGenericArguments() } else { $null }
                        $m = $mod.ResolveMember($t, $g, $h)
                        "    $($op.Name) $($m.DeclaringType.Name)::$($m.Name)"
                    } catch { "    $($op.Name) <0x$('{0:X}' -f $t)>" }
                }
            }
        }
    }
}
if ($TypePattern) {
    foreach ($ty in ($types | Where-Object { $_.FullName -match $TypePattern })) {
        foreach ($mb in @($ty.GetMethods($F)) + @($ty.GetConstructors($F))) {
            if ($mb.Name -notmatch $MethodPattern) { continue }
            "--- $($ty.FullName)::$($mb.Name)"
            Disasm $mb
        }
    }
    return
}
foreach ($ty in $types) { foreach ($mb in @($ty.GetMethods($F)) + @($ty.GetConstructors($F))) {
    $hits = @(Disasm $mb | Where-Object { $_ -match '^\s+ldstr' -and $_ -match $StringPattern })
    if ($hits.Count) { "$($ty.FullName)::$($mb.Name)"; $hits | Select-Object -Unique }
} }
