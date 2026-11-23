# Silent SimHub install for simhub-update.yml. Returns once the installer has exited
# and every file the build and the interface tests load is in place.
param(
    [Parameter(Mandatory)] [string] $Installer,
    [Parameter(Mandatory)] [string] $Dir,
    [int] $TimeoutMinutes = 15
)
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Path $Dir -Force | Out-Null
& $Installer /VERYSILENT /SUPPRESSMSGBOXES /NORESTART "/DIR=$Dir"

# Inno Setup hands off to a SimHubSetup.tmp child, so the launch returns early.
$required = @(
    'SimHubWPF.exe', 'SimHubWPF.exe.config',
    'SimHub.Plugins.dll', 'GameReaderCommon.dll', 'SimHub.Logging.dll', 'SerialDash.dll',
    'BA63Driver.dll', 'SimHub.Plugins.Motion.dll', 'HidSharp.dll', 'ACSharedMemory.dll'
)
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while ($true) {
    $missing = @($required | Where-Object { -not (Test-Path (Join-Path $Dir $_) -PathType Leaf) })
    $running = @(Get-Process -Name 'SimHubSetup*' -ErrorAction SilentlyContinue)
    if ($missing.Count -eq 0 -and $running.Count -eq 0) { break }
    if ((Get-Date) -gt $deadline) {
        throw "SimHub install into $Dir timed out (missing: $($missing -join ', '); installer running: $($running.Count -gt 0))"
    }
    Write-Output "Waiting for the installer..."
    Start-Sleep -Seconds 5
}

# A post-install launch would only hold files open.
Get-Process -Name 'SimHubWPF' -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output "SimHub installed in $Dir"
exit 0
