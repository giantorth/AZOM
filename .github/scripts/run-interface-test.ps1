# Runs tests/simhub-interface for simhub-update.yml. Exit 1 (test failures) is
# recorded in $RUNNER_TEMP/interface-failed and the step still passes, so the update
# PR opens as a draft with the report; any other non-zero exit is a setup error and
# fails the job.
param(
    [Parameter(Mandatory)] [string] $Plugin,
    [Parameter(Mandatory)] [string] $SimHub,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Label,
    [Parameter(Mandatory)] [string] $Report,
    [switch] $Contract
)

$exe = Join-Path $PSScriptRoot '../../tests/simhub-interface/bin/Release/simhub-interface-test.exe'
$harnessArgs = @(
    '--simhub', $SimHub, '--plugin', $Plugin, '--expect-version', $Version,
    '--label', $Label, '--report', $Report
)
if ($Contract) { $harnessArgs += '--contract' }

& $exe @harnessArgs
$code = $LASTEXITCODE
if ($code -eq 1) {
    New-Item -ItemType File -Force -Path (Join-Path $env:RUNNER_TEMP 'interface-failed') | Out-Null
}
elseif ($code -ne 0) {
    Write-Output "::error::simhub-interface-test setup error (exit $code) for $Label"
    exit $code
}
exit 0
