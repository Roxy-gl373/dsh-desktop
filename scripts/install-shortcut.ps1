# install-shortcut.ps1 — resolves DSH paths, writes config.json, and creates the Desktop shortcut.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File install-shortcut.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File install-shortcut.ps1 -NoShortcut
#   powershell -NoProfile -ExecutionPolicy Bypass -File install-shortcut.ps1 -DryRun
param(
    [switch]$NoShortcut,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$pkgRoot = Split-Path $PSScriptRoot -Parent   # the dsh-desktop package dir
$binDir  = Join-Path $pkgRoot 'bin'
$exe     = Join-Path $binDir 'Dwhale.exe'
$ico     = Join-Path $binDir 'dps.ico'

# ---- resolve node ----
$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $node) { throw 'node.exe not found. Install Node.js first.' }

# ---- resolve dsh lib/bin.js (scan npx cache + node_modules) ----
$candidates = New-Object System.Collections.Generic.List[object]
$searchRoots = @(
    (Join-Path $env:USERPROFILE 'AppData\Local\npm-cache\_npx'),
    (Join-Path $env:USERPROFILE 'AppData\Roaming\npm\node_modules'),
    (Join-Path $env:LOCALAPPDATA 'npm-cache\_npx')
)
foreach ($r in $searchRoots) {
    if (-not (Test-Path $r)) { continue }
    Get-ChildItem -Path $r -Recurse -Filter 'bin.js' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\@deepseek-ai\\dsh\\lib\\bin\.js$' } |
        ForEach-Object { $candidates.Add($_) }
}
$dshJs = $null
if ($candidates.Count -gt 0) { $dshJs = ($candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }
if (-not $dshJs) {
    # Fall back to the profile's own copy if present.
    $alt = Join-Path $env:USERPROFILE ".dsh\profiles\node_modules\@deepseek-ai\dsh\lib\bin.js"
    if (Test-Path $alt) { $dshJs = $alt }
}
if (-not $dshJs) { throw 'Could not locate @deepseek-ai/dsh/lib/bin.js in the npx cache.' }

# ---- resolve dshCmd (the .cmd/.exe shim) for a PATH-based fallback ----
$dshCmd = (Get-Command dsh -ErrorAction SilentlyContinue).Source
if (-not $dshCmd) {
    $stub = Join-Path (Split-Path $dshJs -Parent) '..\..\..\..\..\.bin\dsh.cmd'
    if (Test-Path $stub) { $dshCmd = (Resolve-Path $stub).Path }
}

$pkgDirName = Split-Path $pkgRoot -Leaf
# A stable logical name for the shortcut (used for the .lnk file on the Desktop).
$shortcutName = 'DSh Web 鲸鱼娘.lnk'

$config = [ordered]@{
    appName        = 'DSh Whale · DeepSeek Harness'
    nodePath       = $node
    dshBinJs       = $dshJs
    dshCmd         = $dshCmd
    dshHome        = (Join-Path $env:USERPROFILE '.dsh')
    webProfile     = 'web'
    webHost        = '127.0.0.1'
    webPort        = 3080
    healthTimeoutMs = 90000
    pollIntervalMs = 3000
    logDir         = (Join-Path $pkgRoot 'logs')
    stateDir       = (Join-Path $pkgRoot 'state')
    snapshotDir    = (Join-Path $pkgRoot 'state\snapshots')
    manifestPath   = (Join-Path $pkgRoot 'state\manifest.json')
    safetyScript   = (Join-Path $pkgRoot 'scripts\dsh-safety.ps1')
    iconPath       = $ico
    configPath     = (Join-Path $pkgRoot 'config.json')
    updateUrl      = ''
    appVersion     = '1.0.1'
}

Write-Output "packageRoot = $pkgRoot"
Write-Output "node        = $node"
Write-Output "dshBinJs    = $dshJs"
Write-Output "dshCmd      = $dshCmd"
Write-Output "dshHome     = $($config.dshHome)"
Write-Output "exe         = $exe  (exists=$(Test-Path $exe))"
Write-Output "icon        = $ico  (exists=$(Test-Path $ico))"

if ($DryRun) { Write-Output 'DRY-RUN: config would be written above; no files changed.'; return }

($config | ConvertTo-Json -Depth 5) | Set-Content -Path $config.configPath -Encoding UTF8
Write-Output "Wrote config.json -> $($config.configPath)"

New-Item -ItemType Directory -Force -Path (Join-Path $pkgRoot 'logs'), (Join-Path $pkgRoot 'state\snapshots') | Out-Null

if (-not $NoShortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $lnk = Join-Path $desktop $shortcutName
    $ws = New-Object -ComObject WScript.Shell
    $sc = $ws.CreateShortcut($lnk)
    $sc.TargetPath = $exe
    $sc.WorkingDirectory = $pkgRoot
    $sc.Description = 'DeepSeek Harness Web (鲸鱼娘) launcher — starts dsh web, logs, and supervises plugin installs.'
    $sc.IconLocation = "$ico,0"
    $sc.Save()
    Write-Output "Created shortcut: $lnk"
} else {
    Write-Output 'Skipped shortcut (-NoShortcut).'
}

Write-Output 'Done.'
