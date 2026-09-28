<#
.SYNOPSIS
    End-to-end check of a DSH installer: silent per-user install, launch the installed app, check
    the Start menu shortcut and "Open with DSH" folder menu, then uninstall and check it cleaned up.

.DESCRIPTION
    CI runs this against the freshly built x64 installer. The installed app is launched with the
    UI self-test (light theme only), which proves it starts and renders from its install folder.
    Leaves nothing behind on success; on failure the Inno Setup logs are in -Output.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Installer,
    [Parameter(Mandatory)] [string] $Output
)

$ErrorActionPreference = 'Stop'
$installerPath = (Resolve-Path $Installer).Path
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$appId = '{6F3B2E7A-9C41-4D8B-A5E2-3C7D9B1F0A64}_is1'
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
$menuKeys = @('HKCU:\Software\Classes\Directory\shell\DSH', 'HKCU:\Software\Classes\Directory\Background\shell\DSH')
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\DSH'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'DSH.lnk'

function Wait-Until([scriptblock] $Condition, [int] $Seconds, [string] $What) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (& $Condition)) {
        if ((Get-Date) -gt $deadline) { throw "Timed out after $Seconds s waiting for $What." }
        Start-Sleep -Milliseconds 500
    }
}

Write-Host "Installing $installerPath"
$log = Join-Path $Output 'install.log'
$process = Start-Process -FilePath $installerPath -PassThru -Wait -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/TASKS=contextmenu', "/LOG=`"$log`"")
if ($process.ExitCode -ne 0) { throw "The installer exited with code $($process.ExitCode) (see $log)." }

$exe = Join-Path $installDir 'DSH.exe'
if (-not (Test-Path $exe)) { throw "DSH.exe was not installed to $installDir." }
if (-not (Test-Path $uninstallKey)) { throw 'The uninstall entry is missing from Settings > Apps.' }
$entry = Get-ItemProperty $uninstallKey
Write-Host "  Installed $($entry.DisplayName) $($entry.DisplayVersion) to $installDir"
if (-not (Test-Path $shortcut)) { throw "The Start menu shortcut is missing ($shortcut)." }
foreach ($key in $menuKeys) {
    $command = (Get-ItemProperty (Join-Path $key 'command')).'(default)'
    if ($command -notlike "*$exe*") { throw "Folder menu entry $key runs '$command', expected $exe." }
}
Write-Host '  Start menu shortcut and folder menu entries are in place'

Write-Host 'Launching the installed app (UI self-test, light theme)'
& (Join-Path $PSScriptRoot 'self-test.ps1') -Exe $exe -Output (Join-Path $Output 'selftest') -Theme light
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'The installed app failed its self-test.' }

Write-Host 'Uninstalling'
$uninstaller = Join-Path $installDir 'unins000.exe'
if (-not (Test-Path $uninstaller)) { throw "The uninstaller is missing ($uninstaller)." }
$process = Start-Process -FilePath $uninstaller -PassThru -Wait -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $Output 'uninstall.log')`"")
if ($process.ExitCode -ne 0) { throw "The uninstaller exited with code $($process.ExitCode)." }
# The uninstaller hands off to a copy of itself in %TEMP%, so wait for the files to go.
Wait-Until { -not (Test-Path $exe) } 120 'DSH.exe to be removed'
Wait-Until { -not (Test-Path $uninstallKey) } 60 'the uninstall entry to be removed'
foreach ($key in $menuKeys) {
    if (Test-Path $key) { throw "Uninstall left the folder menu entry $key behind." }
}
if (Test-Path $shortcut) { throw 'Uninstall left the Start menu shortcut behind.' }
Write-Host 'Installer test passed.'
