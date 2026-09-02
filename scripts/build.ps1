# build.ps1 — compile the DSh Whale launcher into bin\Dwhale.exe.
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
# Requires: csc.exe (.NET Framework), the WebView2 SDK DLLs, and bin\dps.ico.
# The WebView2 SDK is searched in: .\lib\webview2, .\bin, and a few well-known
# locations (Microsoft Office bundles it). If you have network, the preferred
# path is to restore the official package and drop the three DLLs into .\lib\webview2.
param(
    [string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'bin\Dwhale.exe'),
    [switch]$SkipCopyWebview2
)
$ErrorActionPreference = 'Stop'
$pkg = Split-Path $PSScriptRoot -Parent
$srcDir = Join-Path $pkg 'src'
$binDir = Join-Path $pkg 'bin'
$ico = Join-Path $binDir 'dps.ico'

# locate csc
$csc = @(
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'csc.exe not found. Install .NET Framework 4.x.' }

# locate WebView2 SDK DLLs
$wv2Core = $null; $wv2WinForms = $null; $wv2Loader = $null
$candidates = @(
    (Join-Path $pkg 'lib\webview2'),
    $binDir,
    'C:\Program Files\Microsoft Office\root\Office16\ADDINS\Microsoft Power Query for Excel Integrated\bin',
    'C:\Program Files\Microsoft Office\root\Office16\WritingAssistant'
)
foreach ($c in $candidates) {
    if (-not (Test-Path $c)) { continue }
    $core = Join-Path $c 'Microsoft.Web.WebView2.Core.dll'
    $wf = Join-Path $c 'Microsoft.Web.WebView2.WinForms.dll'
    $ld = Join-Path $c 'WebView2Loader.dll'
    if ((Test-Path $core) -and (Test-Path $wf) -and (Test-Path $ld)) {
        $wv2Core = $core; $wv2WinForms = $wf; $wv2Loader = $ld; break
    }
}
if (-not $wv2Core) { throw 'WebView2 SDK not found. Drop Microsoft.Web.WebView2.Core.dll / .WinForms.dll / WebView2Loader.dll into .\lib\webview2, or restore the Microsoft.Web.WebView2 NuGet package.' }

$sources = Get-ChildItem $srcDir -Filter '*.cs' | Select-Object -ExpandProperty FullName
if (-not (Test-Path $ico)) { throw "Icon not found: $ico" }
New-Item -ItemType Directory -Force -Path $binDir | Out-Null

$refs = @(
    'System.Windows.Forms.dll',
    'System.Drawing.dll',
    'System.Web.Extensions.dll',
    $wv2Core,
    $wv2WinForms
)

$cmd = @($csc) + @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/win32icon:'+$ico,'/out:'+$Out)
foreach ($r in $refs) { $cmd += ('/r:' + $r) }
$cmd += $sources

Write-Output "csc: $csc"
Write-Output "WebView2 SDK: $wv2Core / $wv2WinForms"
Write-Output "sources: $($sources -join ', ')"
& $csc /nologo /target:winexe /platform:anycpu /optimize+ "/win32icon:$ico" "/out:$Out" $(foreach ($r in $refs) { "/r:$r" }) $sources
if ($LASTEXITCODE -ne 0) { throw "csc failed (exit $LASTEXITCODE)" }

# ship the WebView2 loader + managed dlls next to the exe
if (-not $SkipCopyWebview2) {
    foreach ($f in @($wv2Core, $wv2WinForms, $wv2Loader)) {
        if ((Split-Path $f -Parent) -ne $binDir) { Copy-Item -Path $f -Destination $binDir -Force }
    }
    Write-Output "WebView2 SDK DLLs present in $binDir"
}
Write-Output "Built: $Out"
