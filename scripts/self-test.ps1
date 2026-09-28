<#
.SYNOPSIS
    Runs DSH's built-in UI self-test (DSH.exe --self-test) and fails when it reports a problem.

.DESCRIPTION
    The self-test starts DSH against a throwaway data folder and a demo project, fills a chat with
    every kind of transcript row, opens code mode with a terminal, and renders the main window and
    every settings page, the setup wizard and Memory & Skills to PNG files in -Output. Any exception
    while doing so makes it exit non-zero.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Output,
    [string[]] $Theme = @('light', 'dark'),
    [int] $TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$failed = @()

foreach ($t in $Theme) {
    Write-Host "Self-test ($t theme)"
    $process = Start-Process -FilePath $exePath -ArgumentList @('--self-test', "`"$Output`"", '--theme', $t) -PassThru
    # Touching Handle makes ExitCode available after the process ends.
    $null = $process.Handle
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        $failed += "$t (timed out after $TimeoutSeconds s)"
        continue
    }
    $report = Join-Path $Output "report-$t.txt"
    if (Test-Path $report) { Get-Content $report | ForEach-Object { Write-Host "  $_" } }
    else { Write-Host '  (no report written)' }
    if ($process.ExitCode -ne 0) { $failed += "$t (exit code $($process.ExitCode))" }
}

$logs = Join-Path $Output 'home\logs'
if (Test-Path $logs) {
    Get-ChildItem $logs -Filter 'crash-*.txt' | ForEach-Object {
        Write-Host "---- $($_.Name)"
        Get-Content $_.FullName | Select-Object -First 40 | ForEach-Object { Write-Host "  $_" }
    }
}

if ($failed.Count -gt 0) {
    Write-Error "Self-test failed: $($failed -join '; ')"
    exit 1
}
Write-Host 'Self-test passed.'
