#Requires -Version 5.1
<#
.SYNOPSIS
    Build the MOZA plugin, swap it into SimHub, and relaunch.

.DESCRIPTION
    Gracefully closes SimHub (so its file lock on MozaPlugin.dll releases),
    runs dotnet build -c <Configuration> with SIMHUB_PATH set (which fires the
    CopyToSimHub MSBuild target in MozaPlugin.csproj), then relaunches SimHub.

    If the build fails, SimHub is NOT relaunched — that way you don't end up
    with the previous DLL silently running while you think the new one is live.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER SimHubPath
    Override the SimHub install dir. Default: $env:SIMHUB_PATH if set, else
    'C:\Program Files (x86)\SimHub'.

.PARAMETER NoLaunch
    Skip the relaunch step. Useful when you want to inspect the deployed bits
    before SimHub picks them up.

.PARAMETER GracefulTimeoutSeconds
    How long to wait for SimHub to close cleanly before falling back to a
    forced terminate. Default: 15.

.EXAMPLE
    pwsh tools/deploy.ps1
    pwsh tools/deploy.ps1 -NoLaunch
    pwsh tools/deploy.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $SimHubPath,
    [switch] $NoLaunch,
    [int]    $GracefulTimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Resolve paths. Script lives in <repo>/tools/, so the repo root is one level up.
# ---------------------------------------------------------------------------
$repoRoot = Split-Path -Parent $PSScriptRoot
$csproj   = Join-Path $repoRoot 'MozaPlugin.csproj'
if (-not (Test-Path $csproj)) {
    throw "MozaPlugin.csproj not found at '$csproj' — is this script still in <repo>/tools/?"
}

if (-not $SimHubPath) { $SimHubPath = $env:SIMHUB_PATH }
if (-not $SimHubPath) { $SimHubPath = 'C:\Program Files (x86)\SimHub' }
if (-not (Test-Path $SimHubPath)) {
    throw "SimHub install dir '$SimHubPath' does not exist. Set -SimHubPath or `$env:SIMHUB_PATH."
}

$simhubExe = Join-Path $SimHubPath 'SimHubWPF.exe'
if (-not (Test-Path $simhubExe)) {
    throw "SimHubWPF.exe not found at '$simhubExe'. Wrong SimHub path?"
}

# ---------------------------------------------------------------------------
# Step 1 — gracefully close SimHub if it's running.
# ---------------------------------------------------------------------------
# Get-Process drops the trailing ".exe", so SimHubWPF.exe matches as "SimHubWPF".
# Wrap in try/catch since Get-Process without -ErrorAction Stop emits a warning
# but no array when nothing matches.
$simhubProcs = @(Get-Process -Name 'SimHubWPF' -ErrorAction SilentlyContinue)

if ($simhubProcs.Count -gt 0) {
    Write-Host "Closing SimHub (PID $($simhubProcs[0].Id))..." -ForegroundColor Cyan
    foreach ($p in $simhubProcs) {
        # CloseMainWindow posts WM_CLOSE — the same thing the X button does.
        # If SimHub has any "are you sure?" dialog this returns true but the
        # process keeps running until the user clicks through, which is why
        # we still wait below and force-kill on timeout.
        $null = $p.CloseMainWindow()
    }

    $deadline = (Get-Date).AddSeconds($GracefulTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $still = @(Get-Process -Name 'SimHubWPF' -ErrorAction SilentlyContinue)
        if ($still.Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }

    $still = @(Get-Process -Name 'SimHubWPF' -ErrorAction SilentlyContinue)
    if ($still.Count -gt 0) {
        Write-Host "SimHub did not close within ${GracefulTimeoutSeconds}s — forcing terminate." -ForegroundColor Yellow
        foreach ($p in $still) {
            try { $p | Stop-Process -Force -ErrorAction Stop } catch { Write-Host "  (failed to kill PID $($p.Id): $_)" -ForegroundColor Yellow }
        }
        # Brief settle so the file lock actually clears before we try to copy.
        Start-Sleep -Milliseconds 500
    } else {
        Write-Host "SimHub closed." -ForegroundColor Green
    }
} else {
    Write-Host "SimHub not running — skipping close." -ForegroundColor DarkGray
}

# Some SimHub subprocesses linger briefly after the main window closes and can
# also hold open handles. Wait them out the same way.
$subprocs = @(Get-Process -Name 'SimHub.Subprocess.X64','SimHub.Subprocess.X86','CefSharp.BrowserSubprocess' -ErrorAction SilentlyContinue)
if ($subprocs.Count -gt 0) {
    Write-Host "Waiting on SimHub subprocesses to exit ($($subprocs.Count) found)..." -ForegroundColor DarkGray
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline) {
        $still = @(Get-Process -Name 'SimHub.Subprocess.X64','SimHub.Subprocess.X86','CefSharp.BrowserSubprocess' -ErrorAction SilentlyContinue)
        if ($still.Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    }
}

# ---------------------------------------------------------------------------
# Step 2 — build + deploy via the csproj's CopyToSimHub target.
# ---------------------------------------------------------------------------
Write-Host "Building (Configuration=$Configuration, SIMHUB_PATH=$SimHubPath)..." -ForegroundColor Cyan

# Set SIMHUB_PATH for this PowerShell invocation only; the csproj's CopyToSimHub
# target keys on it.
$prevSimhub = $env:SIMHUB_PATH
$env:SIMHUB_PATH = $SimHubPath
try {
    & dotnet build $csproj -c $Configuration
    $buildExit = $LASTEXITCODE
} finally {
    if ($null -eq $prevSimhub) { Remove-Item Env:SIMHUB_PATH -ErrorAction SilentlyContinue }
    else                        { $env:SIMHUB_PATH = $prevSimhub }
}

if ($buildExit -ne 0) {
    Write-Host ""
    Write-Host "Build failed (exit code $buildExit). NOT relaunching SimHub — fix the build and rerun." -ForegroundColor Red
    exit $buildExit
}

# Clean up stale per-culture satellite folders left by older builds. We used to
# ship localized resources as satellite assemblies under SimHub/<culture>/; the
# current build embeds them in MozaPlugin.dll so those folders are dead weight.
# If they still exist alongside the new DLL, .NET may try to load the (now
# possibly out-of-date) satellites for the current culture instead of the
# embedded resources. Safe to delete unconditionally — nothing else in SimHub
# owns these directories.
foreach ($culture in 'es', 'fr', 'ru') {
    $staleDir = Join-Path $SimHubPath $culture
    $staleDll = Join-Path $staleDir 'MozaPlugin.resources.dll'
    if (Test-Path $staleDll) {
        Write-Host "Removing stale satellite '$staleDll'..." -ForegroundColor DarkGray
        try { Remove-Item $staleDll -Force -ErrorAction Stop } catch { Write-Host "  (failed: $_)" -ForegroundColor Yellow }
        # Drop the folder too if it's now empty (avoids leaving orphan dirs in
        # SimHub's root). If anything else lives in there, leave it alone.
        if ((Test-Path $staleDir) -and -not (Get-ChildItem $staleDir -Force)) {
            try { Remove-Item $staleDir -Force -ErrorAction Stop } catch { }
        }
    }
}

# ---------------------------------------------------------------------------
# Step 3 — relaunch SimHub (unless -NoLaunch).
# ---------------------------------------------------------------------------
if ($NoLaunch) {
    Write-Host "Build OK. Skipping relaunch (-NoLaunch)." -ForegroundColor Green
    exit 0
}

Write-Host "Relaunching SimHub..." -ForegroundColor Cyan
# WorkingDirectory matters: SimHub looks up data dirs (PluginsData/, Logs/,
# device caches) relative to the cwd, not the exe path.
Start-Process -FilePath $simhubExe -WorkingDirectory $SimHubPath | Out-Null
Write-Host "Done." -ForegroundColor Green
