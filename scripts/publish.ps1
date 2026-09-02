# publish.ps1 — build a self-contained release zip you can extract and run on a new machine.
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1
#   powershell ... -File scripts\publish.ps1 -Version 1.2.0
#
# The zip contains everything needed at runtime (exe + WebView2 SDK + icon + scripts), so a new
# machine only needs Node.js, DeepSeek Harness, and the WebView2/.NET runtimes; then extract and
# run install.cmd (which regenerates config.json + the desktop shortcut for THAT machine).

param(
    [string]$Version = '',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$pkg  = Split-Path $PSScriptRoot -Parent
$bin  = Join-Path $pkg 'bin'
$stageParent = Join-Path $pkg 'release'

# version (explicit > config.json appVersion > default)
if (-not $Version) {
    $cfgPath = Join-Path $pkg 'config.json'
    if (Test-Path $cfgPath) {
        try { $c = Get-Content $cfgPath -Raw | ConvertFrom-Json; if ($c.appVersion) { $Version = $c.appVersion } } catch { }
    }
}
if (-not $Version) { $Version = '1.0.0' }

# ensure the exe is built
if (-not $SkipBuild) {
    Write-Output '== building exe =='
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $pkg 'scripts\build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'build.ps1 failed.' }
}

# sanity
foreach ($f in @('Dwhale.exe','dps.ico','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll')) {
    if (-not (Test-Path (Join-Path $bin $f))) { throw "Missing $f in bin. Run build.ps1 first." }
}

$stage = Join-Path $stageParent ("DSh-Whale-" + $Version)
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'bin'), (Join-Path $stage 'scripts') | Out-Null

# runtime binaries
foreach ($f in @('Dwhale.exe','dps.ico','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll')) {
    Copy-Item -Path (Join-Path $bin $f) -Destination (Join-Path $stage 'bin') -Force
}
# scripts needed at runtime/setup
foreach ($f in @('install-shortcut.ps1','dsh-safety.ps1','convert-icon.ps1')) {
    Copy-Item -Path (Join-Path (Join-Path $pkg 'scripts') $f) -Destination (Join-Path $stage 'scripts') -Force
}
# docs + template + one-click entry
Copy-Item -Path (Join-Path $pkg 'README.md') -Destination $stage -Force
Copy-Item -Path (Join-Path $pkg 'CHANGELOG.md') -Destination $stage -Force
Copy-Item -Path (Join-Path $pkg 'LICENSE') -Destination $stage -Force
if (Test-Path (Join-Path $pkg 'docs')) { Copy-Item -Path (Join-Path $pkg 'docs') -Destination $stage -Recurse -Force }
Copy-Item -Path (Join-Path $pkg 'config.sample.json') -Destination (Join-Path $stage 'config.sample.json') -Force
Copy-Item -Path (Join-Path $pkg 'dsh-safe-add.cmd') -Destination $stage -Force

# one-click setup: regenerates config.json + desktop shortcut with this machine's paths
$installCmd = @'
@echo off
REM DSh Whale one-click setup for THIS machine.
REM Requires: Node.js, DeepSeek Harness, WebView2 runtime (Win10/11 bundled).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install-shortcut.ps1"
echo.
echo Setup done. A "DSh Web" shortcut was placed on your desktop; double-click it to start.
pause
'@
[System.IO.File]::WriteAllText((Join-Path $stage 'install.cmd'), $installCmd, (New-Object System.Text.UTF8Encoding($false)))

# zip
$zip = Join-Path $stageParent ("DSh-Whale-" + $Version + ".zip")
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Output "== release ready =="
Write-Output ("  folder: " + $stage)
Write-Output ("  zip   : " + $zip)
