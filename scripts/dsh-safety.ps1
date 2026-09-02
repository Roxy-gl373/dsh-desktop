# dsh-safety.ps1 — DeepSeek Harness plugin safety CLI (snapshot / install / rollback / verify / monitor).
#
#   snapshot-before-install  -> every plugin add first archives the current profile state.
#   crash-driven rollback    -> if the server crashes while a plugin install is pending, the
#                               launcher/monitor restores the last known-good snapshot.
#   verify                   -> after a successful health check, promote the state to 'good'.
#
# Compatible with Windows PowerShell 5.1 (invoked by the Dwhale.exe launcher) and PowerShell 7.
#
# Usage (in the user's normal shell; this script writes to $DSH_HOME / the web profile):
#   powershell -NoProfile -ExecutionPolicy Bypass -File dsh-safety.ps1 -Action Status  -Config config.json
#   powershell -NoProfile -ExecutionPolicy Bypass -File dsh-safety.ps1 -Action Snapshot -Reason baseline -Config config.json
#   powershell -NoProfile -ExecutionPolicy Bypass -File dsh-safety.ps1 -Action Add -Plugin <spec> -Config config.json
#   powershell -NoProfile -ExecutionPolicy Bypass -File dsh-safety.ps1 -Action Verify -Config config.json
#   powershell -NoProfile -ExecutionPolicy Bypass -File dsh-safety.ps1 -Action Rollback -Snapshot <id> -Config config.json
#   powershell -NoProfile -ExecutionPolicy Bypass -File dsh-safety.ps1 -Action Monitor -Config config.json

param(
    [ValidateSet('Snapshot','Add','Rollback','Status','Verify','Monitor')]
    [string]$Action = 'Status',
    [string]$Plugin,
    [string]$Snapshot,
    [string]$Config,
    [string]$Name,
    [string]$Reason,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- helpers
function Resolve-ConfigPath {
    if ($Config -and (Test-Path $Config)) { return (Resolve-Path $Config).Path }
    $here = $PSScriptRoot
    $cands = @(
        (Join-Path $here 'config.json'),
        (Join-Path (Split-Path $here -Parent) 'config.json'),
        (Join-Path (Split-Path (Split-Path $here -Parent) -Parent) 'config.json')
    )
    foreach ($c in $cands) { if (Test-Path $c) { return (Resolve-Path $c).Path } }
    return $null
}

function Read-Config {
    $p = Resolve-ConfigPath
    if (-not $p) { throw 'config.json not found. Run install-shortcut.ps1 first.' }
    $cfg = Get-Content $p -Raw | ConvertFrom-Json
    if (-not $cfg.dshHome) { $cfg.dshHome = (Join-Path $env:USERPROFILE '.dsh') }
    if (-not $cfg.webProfile) { $cfg.webProfile = 'web' }
    if (-not $cfg.webHost) { $cfg.webHost = '127.0.0.1' }
    if (-not $cfg.webPort) { $cfg.webPort = 3080 }
    if (-not $cfg.logDir) { $cfg.logDir = (Join-Path (Split-Path $p -Parent) 'logs') }
    if (-not $cfg.stateDir) { $cfg.stateDir = (Join-Path (Split-Path $p -Parent) 'state') }
    if (-not $cfg.snapshotDir) { $cfg.snapshotDir = (Join-Path $cfg.stateDir 'snapshots') }
    if (-not $cfg.manifestPath) { $cfg.manifestPath = (Join-Path $cfg.stateDir 'manifest.json') }
    if (-not $cfg.safetyScript) { $cfg.safetyScript = $PSCommandPath }
    $cfg | Add-Member -NotePropertyName cfgPath -NotePropertyValue $p -Force
    return $cfg
}

$script:cfg = $null
$script:logWriter = $null

function New-QuotedArgString {
    param([string[]]$ArgList)
    if ($null -eq $ArgList -or $ArgList.Count -eq 0) { return '' }
    $parts = foreach ($a in $ArgList) {
        if ($a -match '[\s"]') {
            '"' + ($a -replace '"', '\"') + '"'
        } else { $a }
    }
    return ($parts -join ' ')
}

function Init-Log {
    if ($null -eq $script:logWriter) {
        if ($null -eq $script:cfg) { $script:cfg = Read-Config }
        New-Item -ItemType Directory -Force -Path $script:cfg.logDir | Out-Null
        $f = Join-Path $script:cfg.logDir 'dsh-safety.log'
        $script:logWriter = New-Object System.IO.StreamWriter($f, $true)
        $script:logWriter.AutoFlush = $true
    }
}
function Log {
    param([string]$m, [string]$lvl = 'INFO')
    $line = ('{0}  [{1}]  {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $lvl, $m)
    # Write-Host keeps this off the success pipeline so it cannot pollute function
    # return values (e.g. New-Snapshot's id), while still reaching the host/stdout.
    Write-Host $line
    Init-Log
    $script:logWriter.WriteLine($line)
}

function Invoke-Cmd {
    param([string]$File, [string[]]$ArgList, [string]$WorkDir, [switch]$Silent)
    $sb = New-Object System.Text.StringBuilder
    $pi = New-Object System.Diagnostics.ProcessStartInfo
    $pi.FileName = $File
    $pi.UseShellExecute = $false
    $pi.RedirectStandardOutput = $true
    $pi.RedirectStandardError = $true
    $pi.CreateNoWindow = $true
    $pi.Arguments = (New-QuotedArgString $ArgList)
    if ($WorkDir) { $pi.WorkingDirectory = $WorkDir }
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $pi
    $p.OutputDataReceived += { param($s, $e) if ($e.Data) { [void]$sb.AppendLine($e.Data); if (-not $Silent) { Write-Output $e.Data } } }
    $p.ErrorDataReceived  += { param($s, $e) if ($e.Data) { [void]$sb.AppendLine($e.Data); if (-not $Silent) { Write-Output ("[err] " + $e.Data) } } }
    $p.Start() | Out-Null
    $p.BeginOutputReadLine(); $p.BeginErrorReadLine()
    $p.WaitForExit()
    [pscustomobject]@{ ExitCode = $p.ExitCode; Output = $sb.ToString() }
}

function Resolve-DshInvoker {
    # Prefer node + dsh lib/bin.js (works without a .cmd on PATH); fall back to a .cmd.
    if ($script:cfg.dshBinJs -and (Test-Path $script:cfg.dshBinJs)) {
        $node = $script:cfg.nodePath
        if (-not $node -or -not (Test-Path $node)) { $node = (Get-Command node -ErrorAction SilentlyContinue).Source }
        if ($node -and (Test-Path $node)) {
            return [pscustomobject]@{ File = $node; BaseArgs = @($script:cfg.dshBinJs); NodeJs = $true }
        }
    }
    if ($script:cfg.dshCmd -and (Test-Path $script:cfg.dshCmd)) {
        # .cmd must be launched through cmd.exe /c
        return [pscustomobject]@{ File = 'cmd.exe'; BaseArgs = @('/c', $script:cfg.dshCmd); NodeJs = $false }
    }
    $cmd = Get-Command dsh -ErrorAction SilentlyContinue
    if ($cmd) { return [pscustomobject]@{ File = $cmd.Source; BaseArgs = @(); NodeJs = $false } }
    throw 'Cannot resolve dsh launcher. Set dshBinJs/dshCmd in config.json.'
}

function Invoke-DshPlugin {
    param([string]$Profile, [string[]]$CmdArgs, [string]$WorkDir)
    $inv = Resolve-DshInvoker
    $args = @($inv.BaseArgs)
    if ($inv.NodeJs) {
        $args += @('--profile', $Profile, 'plugin') + $CmdArgs
    } else {
        $args += @('plugin', '--profile', $Profile) + $CmdArgs
    }
    return (Invoke-Cmd -File $inv.File -ArgList $args -WorkDir $WorkDir)
}

# ---------------------------------------------------------------- manifest + snapshots
function Read-Manifest {
    $p = $script:cfg.manifestPath
    if (Test-Path $p) { return (Get-Content $p -Raw | ConvertFrom-Json) }
    return [pscustomobject]@{ lastGoodSnapshotId = $null; pendingInstall = $null }
}
function Write-Manifest {
    param($m)
    New-Item -ItemType Directory -Force -Path (Split-Path $script:cfg.manifestPath -Parent) | Out-Null
    ($m | ConvertTo-Json -Depth 8) | Set-Content -Path $script:cfg.manifestPath -Encoding UTF8
}

function ProfileDir { (Join-Path $script:cfg.dshHome ('profiles\' + $script:cfg.webProfile)) }
function HomeDir { $script:cfg.dshHome }

function New-Snapshot {
    param([string]$Kind = 'snapshot', [string]$Plugin = '', [string]$Reason = '')
    $id = ($Kind + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $dir = Join-Path $script:cfg.snapshotDir $id
    New-Item -ItemType Directory -Force -Path (Join-Path $dir 'profile'), (Join-Path $dir 'home') | Out-Null

    $prof = ProfileDir
    $homeDirV = HomeDir
    $files = @(
        @{ Src = (Join-Path $prof 'package.json'); Dst = (Join-Path $dir 'profile\package.json'); Req = $true },
        @{ Src = (Join-Path $prof 'pnpm-lock.yaml'); Dst = (Join-Path $dir 'profile\pnpm-lock.yaml'); Req = $true },
        @{ Src = (Join-Path $prof 'pnpm-workspace.yaml'); Dst = (Join-Path $dir 'profile\pnpm-workspace.yaml'); Req = $false },
        @{ Src = (Join-Path $prof 'cordis.patch.yml'); Dst = (Join-Path $dir 'profile\cordis.patch.yml'); Req = $false },
        @{ Src = (Join-Path $homeDirV 'cordis.patch.yml'); Dst = (Join-Path $dir 'home\cordis.patch.yml'); Req = $false },
        @{ Src = (Join-Path $homeDirV 'settings.yaml'); Dst = (Join-Path $dir 'home\settings.yaml'); Req = $false }
    )
    $captured = 0
    foreach ($f in $files) {
        if (Test-Path $f.Src) { Copy-Item -Path $f.Src -Destination $f.Dst -Force; $captured++ }
        elseif ($f.Req) { Log ("WARN: required file missing in snapshot: " + $f.Src) 'WARN' }
    }
    $meta = [pscustomobject]@{
        id = $id; time = (Get-Date).ToString('o'); kind = $Kind; plugin = $Plugin
        reason = $Reason; profile = $script:cfg.webProfile; dshHome = $script:cfg.dshHome
    }
    ($meta | ConvertTo-Json -Depth 5) | Set-Content -Path (Join-Path $dir 'meta.json') -Encoding UTF8
    Log ("Snapshot created: $id ($captured files) profile=$($script:cfg.webProfile)")
    return $id
}

function Restore-Snapshot {
    param([string]$Id)
    $dir = Join-Path $script:cfg.snapshotDir $Id
    if (-not (Test-Path $dir)) { throw "Snapshot not found: $Id" }
    $prof = ProfileDir
    $homeDirV = HomeDir
    foreach ($rel in @('profile\package.json','profile\pnpm-lock.yaml','profile\pnpm-workspace.yaml','profile\cordis.patch.yml','home\cordis.patch.yml','home\settings.yaml')) {
        $src = Join-Path $dir $rel
        if (Test-Path $src) {
            $dst = if ($rel.StartsWith('profile')) { (Join-Path $prof ($rel.Substring(8))) }
                   else { (Join-Path $homeDirV ($rel.Substring(5))) }
            New-Item -ItemType Directory -Force -Path (Split-Path $dst -Parent) | Out-Null
            Copy-Item -Path $src -Destination $dst -Force
            Log ("Restored: $rel -> $dst")
        }
    }
}

# ---------------------------------------------------------------- actions
function Invoke-SnapshotAction {
    param([string]$Kind, [string]$Plugin = '', [string]$Reason = '')
    $id = New-Snapshot -Kind $Kind -Plugin $Plugin -Reason $Reason
    # A user-initiated snapshot is an explicit "this is good" checkpoint: promote it to last good.
    if ($Kind -ne 'preinstall') {
        $m = Read-Manifest
        $m.lastGoodSnapshotId = $id
        Write-Manifest $m
        Log "Marked $id as last known good."
    }
    return $id
}

function Invoke-AddAction {
    param([string]$Spec)
    if (-not $Spec) { throw 'Add requires -Plugin <spec>.' }
    $m = Read-Manifest
    $snap = New-Snapshot -Kind 'preinstall' -Plugin $Spec -Reason 'before plugin add'
    $m.lastGoodSnapshotId = $snap
    $m.pendingInstall = [pscustomobject]@{ plugin = $Spec; snapshotId = $snap; installedAt = (Get-Date).ToString('o') }
    Write-Manifest $m

    Log ("Adding plugin: $Spec")
    if ($DryRun) { Log 'DRY-RUN: skipping dsh plugin add.'; return }

    $r = Invoke-DshPlugin -Profile $script:cfg.webProfile -CmdArgs @('add', $Spec) -WorkDir (ProfileDir)
    Log ("dsh plugin add exit=$($r.ExitCode)")
    if ($r.ExitCode -ne 0) {
        Log "Install FAILED -> rolling back to $snap" 'WARN'
        Invoke-RollbackAction -Id $snap
        return
    }
    Log "Install OK. Pending install recorded ($Spec). RESTART the server, then run Verify (or let the supervisor auto-verify after health recovers)."
}

function Invoke-RollbackAction {
    param([string]$Id)
    $m = Read-Manifest
    if (-not $Id -or $Id -eq 'lastgood') {
        $Id = $m.lastGoodSnapshotId
        if (-not $Id -and $m.pendingInstall) { $Id = $m.pendingInstall.snapshotId }
    }
    if (-not (Test-Path (Join-Path $script:cfg.snapshotDir $Id))) {
        if ($m.lastGoodSnapshotId -and (Test-Path (Join-Path $script:cfg.snapshotDir $m.lastGoodSnapshotId))) { $Id = $m.lastGoodSnapshotId }
        else { throw "No snapshot found to roll back to. Run: -Action Snapshot" }
    }
    Log ("Rolling back to snapshot: $Id")
    Restore-Snapshot -Id $Id
    if (-not $DryRun) {
        Log 'Running pnpm install in profile dir to reconcile node_modules...'
        $pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
        if ($pnpm) {
            # Run the shim in-process so the .ps1 wrapper works on Windows.
            $out = (& $pnpm.Source --dir (ProfileDir) --no-frozen-lockfile install 2>&1 | Out-String)
            if ($out) { Log ("pnpm: " + $out.Trim()) }
            Log ("pnpm install exit=$LASTEXITCODE")
        } else {
            Log 'WARN: pnpm not found; node_modules may still contain the removed package.' 'WARN'
        }
    }
    $m.pendingInstall = $null
    $m.lastGoodSnapshotId = $Id
    Write-Manifest $m
    Log "Rollback complete. State now at $Id."
}

function Test-Health {
    param([string]$Url)
    try {
        $req = [System.Net.HttpWebRequest]::Create(($Url.TrimEnd('/') + '/'))
        $req.Method = 'GET'; $req.Timeout = 2500; $req.KeepAlive = $false
        $resp = $req.GetResponse()
        try { $code = [int]$resp.StatusCode } finally { $resp.Close() }
        return ($code -ge 200 -and $code -lt 500)
    } catch { return $false }
}

function Invoke-VerifyAction {
    $m = Read-Manifest
    $url = 'http://' + $script:cfg.webHost + ':' + $script:cfg.webPort
    $ok = Test-Health $url
    if ($ok -and $m.pendingInstall) {
        $g = New-Snapshot -Kind 'good' -Plugin $m.pendingInstall.plugin -Reason 'verified healthy after install'
        $m.lastGoodSnapshotId = $g
        $m.pendingInstall = $null
        Write-Manifest $m
        Log "Verified. Promoted state to good snapshot $g. Pending install cleared."
    } elseif ($ok) {
        Log 'Server healthy; no pending install to clear.'
    } else {
        Log "Server NOT healthy at $url. Pending install remains: $($m.pendingInstall.plugin). Run Rollback if the plugin broke the server." 'WARN'
    }
}

function Invoke-StatusAction {
    $m = Read-Manifest
    $url = 'http://' + $script:cfg.webHost + ':' + $script:cfg.webPort
    $ok = Test-Health $url
    $snaps = @()
    if (Test-Path $script:cfg.snapshotDir) {
        $snaps = @(Get-ChildItem $script:cfg.snapshotDir -Directory | Sort-Object Name | ForEach-Object {
            $kind = ''; $plugin = ''
            $mi = Join-Path $_.FullName 'meta.json'
            if (Test-Path $mi) { $j = Get-Content $mi -Raw | ConvertFrom-Json; $kind = $j.kind; $plugin = $j.plugin }
            [pscustomobject]@{ Id = $_.Name; Kind = $kind; Plugin = $plugin }
        })
    }
    [pscustomobject]@{
        webProfile = $script:cfg.webProfile
        webUrl = $url
        serverHealthy = $ok
        lastGoodSnapshotId = $m.lastGoodSnapshotId
        pendingInstall = $m.pendingInstall
        snapshots = $snaps
    } | ConvertTo-Json -Depth 8
}

function Invoke-MonitorAction {
    Log 'Monitor started. Checks server health + detects crash-driven rollback. Ctrl+C to stop.'
    $url = 'http://' + $script:cfg.webHost + ':' + $script:cfg.webPort
    $fails = 0
    while ($true) {
        Start-Sleep -Seconds 5
        $ok = Test-Health $url
        $m = Read-Manifest
        if ($ok) { $fails = 0; continue }
        $fails++
        if ($fails -ge 3 -and $m.pendingInstall) {
            Log ("Health lost x$fails with pending install -> auto-rollback.") 'WARN'
            Invoke-RollbackAction -Id $m.pendingInstall.snapshotId
            $fails = 0
        } elseif ($fails -ge 6) {
            Log "Health lost x$fails; no pending install. Manual intervention may be needed." 'WARN'
            $fails = 0
        }
    }
}

# ---------------------------------------------------------------- dispatch
$script:cfg = Read-Config
switch ($Action) {
    'Snapshot' { Invoke-SnapshotAction -Kind $(if ($Name) { $Name } else { 'manual' }) -Reason $Reason }
    'Add'      { Invoke-AddAction -Spec $Plugin }
    'Rollback' { Invoke-RollbackAction -Id $Snapshot }
    'Verify'   { Invoke-VerifyAction }
    'Status'   { Invoke-StatusAction }
    'Monitor'  { Invoke-MonitorAction }
}
