<#
.SYNOPSIS
    End-to-end check of a Vision installer: silent per-user install, launch the installed app, check
    the Start menu shortcut and "Open with Vision" folder menu, then uninstall and check it cleaned up.

.DESCRIPTION
    CI runs this against the freshly built x64 installer. The installed app is launched with the
    UI self-test (light theme only), which proves it starts and renders from its install folder.

    With -LegacyInstaller (a setup published under the app's former name, DSH, such as
    DSH-0.8.85-win-x64-setup.exe) that version is installed first, so the run is the upgrade users get:
    the new version must land in its own Vision folder and remove the old DSH folder, its Start menu and
    desktop shortcuts and its "Open with DSH" entries, leave one entry in Settings > Apps, and leave the
    user's data folder alone. Without it, only the old folder menu entries are seeded.

    Leaves nothing behind on success; on failure the Inno Setup logs are in -Output.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Installer,
    [Parameter(Mandatory)] [string] $Output,
    [string] $LegacyInstaller = ''
)

$ErrorActionPreference = 'Stop'
$installerPath = (Resolve-Path $Installer).Path
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$appId = '{6F3B2E7A-9C41-4D8B-A5E2-3C7D9B1F0A64}_is1'
$uninstallRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
$uninstallKey = "$uninstallRoot\$appId"
$menuKeys = @('HKCU:\Software\Classes\Directory\shell\Vision', 'HKCU:\Software\Classes\Directory\Background\shell\Vision')
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Vision'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Vision.lnk'

# Legacy: what an install made under the app's former name (DSH) leaves, which the upgrade must remove.
$legacyMenuKeys = @('HKCU:\Software\Classes\Directory\shell\DSH', 'HKCU:\Software\Classes\Directory\Background\shell\DSH')
$legacyInstallDir = Join-Path $env:LOCALAPPDATA 'Programs\DSH'
$legacyShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'DSH.lnk'
$legacyDesktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'DSH.lnk'
# Legacy: the old data folder. No installer may touch it (the app brings it over on its first start).
$legacyData = Join-Path $env:APPDATA 'DSH'
$canary = Join-Path $legacyData 'installer-test-canary.txt'

function Wait-Until([scriptblock] $Condition, [int] $Seconds, [string] $What) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (& $Condition)) {
        if ((Get-Date) -gt $deadline) { throw "Timed out after $Seconds s waiting for $What." }
        Start-Sleep -Milliseconds 500
    }
}

function Invoke-Setup([string] $Path, [string] $Tasks, [string] $Log) {
    $process = Start-Process -FilePath $Path -PassThru -Wait -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/TASKS=$Tasks", "/LOG=`"$Log`"")
    if ($process.ExitCode -ne 0) { throw "$Path exited with code $($process.ExitCode) (see $Log)." }
}

$upgrade = [bool] $LegacyInstaller
if ($upgrade) {
    $legacyPath = (Resolve-Path $LegacyInstaller).Path
    Write-Host "Installing the former version first: $legacyPath"
    Invoke-Setup $legacyPath 'contextmenu,desktopicon' (Join-Path $Output 'legacy-install.log')
    if (-not (Test-Path (Join-Path $legacyInstallDir 'DSH.exe'))) { throw "The former version did not install DSH.exe to $legacyInstallDir." }
    foreach ($path in @($legacyShortcut, $legacyDesktopShortcut) + $legacyMenuKeys) {
        if (-not (Test-Path $path)) { throw "The former version did not create $path, so the upgrade can't be checked against it." }
    }
    Write-Host "  Installed $((Get-ItemProperty $uninstallKey).DisplayName) $((Get-ItemProperty $uninstallKey).DisplayVersion) to $legacyInstallDir"
} else {
    foreach ($key in $legacyMenuKeys) {
        New-Item -Path (Join-Path $key 'command') -Force | Out-Null
        Set-ItemProperty -Path $key -Name '(default)' -Value 'Open with the old app'
    }
}

$madeLegacyData = -not (Test-Path $legacyData)
New-Item -ItemType Directory -Force -Path $legacyData | Out-Null
Set-Content -Path $canary -Value 'the user''s data' -Encoding ascii

Write-Host "Installing $installerPath"
Invoke-Setup $installerPath 'contextmenu' (Join-Path $Output 'install.log')

$exe = Join-Path $installDir 'Vision.exe'
if (-not (Test-Path $exe)) { throw "Vision.exe was not installed to $installDir." }
if (-not (Test-Path $uninstallKey)) { throw 'The uninstall entry is missing from Settings > Apps.' }
$entry = Get-ItemProperty $uninstallKey
if ($entry.DisplayName -ne 'Vision') { throw "Settings > Apps lists '$($entry.DisplayName)', expected Vision." }
$recorded = $entry.'Inno Setup: App Path'
if ((Split-Path -Leaf $recorded.TrimEnd('\')) -ne 'Vision' -or -not (Test-Path (Join-Path $recorded 'Vision.exe'))) {
    throw "Settings > Apps records '$recorded' as the install folder, expected $installDir."
}
Write-Host "  Installed $($entry.DisplayName) $($entry.DisplayVersion) to $installDir"
$others = @(Get-ChildItem $uninstallRoot | ForEach-Object { Get-ItemProperty $_.PSPath } |
    Where-Object { $_.PSChildName -ne $appId -and ($_.DisplayName -eq 'DSH' -or $_.DisplayName -eq 'Vision') })
if ($others) { throw "Settings > Apps has a second entry: $($others.PSChildName -join ', ')." }
if (-not (Test-Path $shortcut)) { throw "The Start menu shortcut is missing ($shortcut)." }
foreach ($key in $menuKeys) {
    $command = (Get-ItemProperty (Join-Path $key 'command')).'(default)'
    if ($command -notlike "*$exe*") { throw "Folder menu entry $key runs '$command', expected $exe." }
}
foreach ($key in $legacyMenuKeys) {
    if (Test-Path $key) { throw "The install left the old folder menu entry $key in place." }
}
Write-Host '  Start menu shortcut and folder menu entries are in place; the old folder menu entries are gone'
if ($upgrade) {
    if (Test-Path $legacyInstallDir) {
        throw "The upgrade left the former version's folder $legacyInstallDir behind: $((Get-ChildItem $legacyInstallDir -Name) -join ', ')"
    }
    foreach ($path in @($legacyShortcut, $legacyDesktopShortcut)) {
        if (Test-Path $path) { throw "The upgrade left the former version's shortcut $path behind." }
    }
    Write-Host "  The former version's folder and shortcuts are gone"
}
if ((Get-Content $canary -Raw).Trim() -ne 'the user''s data') { throw "The install changed the user's data folder ($legacyData)." }

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
Wait-Until { -not (Test-Path $exe) } 120 'Vision.exe to be removed'
Wait-Until { -not (Test-Path $uninstallKey) } 60 'the uninstall entry to be removed'
foreach ($key in $menuKeys) {
    if (Test-Path $key) { throw "Uninstall left the folder menu entry $key behind." }
}
if (Test-Path $shortcut) { throw 'Uninstall left the Start menu shortcut behind.' }
if ((Get-Content $canary -Raw).Trim() -ne 'the user''s data') { throw "Uninstall changed the user's data folder ($legacyData)." }

Remove-Item -Force $canary
if ($madeLegacyData) { Remove-Item -Force -Recurse $legacyData }
Write-Host 'Installer test passed.'
