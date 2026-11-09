# Builds dist\xingyao-perf.exe with the C# compiler that ships with Windows. No SDK needed.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $out | Out-Null
$exe = Join-Path $out 'xingyao-perf.exe'
# A running exe can't be overwritten but can be renamed, so a build works while the app is open.
Get-ChildItem $out -Filter '*.exe.*.old' -ErrorAction SilentlyContinue | ForEach-Object { try { Remove-Item $_.FullName -ErrorAction Stop } catch {} }
if (Test-Path $exe) { Move-Item $exe "$exe.$(Get-Date -Format yyyyMMddHHmmss).old" }
& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$exe" "/win32manifest:$root\src\app.manifest" `
    /r:System.dll /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "$root\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
"Built $exe ($([math]::Round((Get-Item $exe).Length / 1KB)) KB)"
