<#
.SYNOPSIS
    End-to-end check of a Vision installer: silent per-user install, launch the installed app, check
    the Start menu shortcut and "Open with Vision" folder menu, then uninstall and check it cleaned up.

.DESCRIPTION
    CI runs this against the freshly built x64 installer. The installed app is launched with the
    UI self-test (light theme only), which proves it starts and renders from its install folder.

    With -LegacyInstaller (a setup published under the app's former name, DSH, such as
    DSH-0.8.85-win-x64-setup.exe) the run is the upgrade users get, end to end:
      1. that version is installed, and its own self-test fills a data folder (chats, a task list,
         memories, a credential vault encrypted the way it was then) that becomes %APPDATA%\DSH,
         as if DSH had been used; a provider key is stored under DSH's Credential Manager name;
      2. the new version is installed: it must land in its own Vision folder and remove the old DSH
         folder, its Start menu and desktop shortcuts and its "Open with DSH" entries, leave one
         entry in Settings > Apps, and leave the user's data alone;
      3. Vision.exe --migrate-only brings the data and the key over the way the first start does:
         the data must be in %APPDATA%\Vision (paths inside it rewritten), DSH's folder gone, the
         key under Vision's name with the same value and DSH's entry gone, and Vision must read
         back every chat, task and vault value; a second run must change nothing;
      4. uninstalling must leave the user's data (now in %APPDATA%\Vision) alone.
    The upgrade run refuses to start if %APPDATA%\DSH or %APPDATA%\Vision already exists, so it never
    touches a real user's data. Without -LegacyInstaller, only the old folder menu entries are seeded.

    Leaves nothing behind on success; on failure the Inno Setup logs and the migration report are in -Output.
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
$Output = (Resolve-Path $Output).Path
$appId = '{6F3B2E7A-9C41-4D8B-A5E2-3C7D9B1F0A64}_is1'
$uninstallRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
$uninstallKey = "$uninstallRoot\$appId"
$menuKeys = @('HKCU:\Software\Classes\Directory\shell\Vision', 'HKCU:\Software\Classes\Directory\Background\shell\Vision')
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Vision'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Vision.lnk'
$data = Join-Path $env:APPDATA 'Vision'

# Legacy: what an install made under the app's former name (DSH) leaves, which the upgrade must remove.
$legacyMenuKeys = @('HKCU:\Software\Classes\Directory\shell\DSH', 'HKCU:\Software\Classes\Directory\Background\shell\DSH')
$legacyInstallDir = Join-Path $env:LOCALAPPDATA 'Programs\DSH'
$legacyShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'DSH.lnk'
$legacyDesktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'DSH.lnk'
# Legacy: the old data folder. No installer may touch it (the app brings it over on its first start).
$legacyData = Join-Path $env:APPDATA 'DSH'
$canaryName = 'installer-test-canary.txt'
$canary = Join-Path $legacyData $canaryName

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

# Runs a GUI exe with arguments and returns its exit code (Start-Process -Wait would also wait for its children).
function Invoke-App([string] $Path, [string[]] $Arguments, [int] $Seconds, [string] $What) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru
    # Touching Handle makes ExitCode available after the process ends.
    $null = $process.Handle
    if (-not $process.WaitForExit($Seconds * 1000)) {
        $process.Kill()
        throw "$What did not finish within $Seconds s."
    }
    return $process.ExitCode
}

# Credential Manager entries written and checked the way the app does (generic, UTF-16 value, this PC).
# Only ever answers whether an entry is there or matches: no value is printed.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class InstallerTestCredentials
{
    private const uint Generic = 1;
    private const uint LocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public static void Write(string target, string secret, string userName, string comment)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = Generic, TargetName = target, CredentialBlobSize = (uint)bytes.Length, CredentialBlob = blob,
                Persist = LocalMachine, UserName = userName, Comment = comment,
            };
            if (!CredWrite(ref credential, 0)) throw new InvalidOperationException("CredWrite failed: " + Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public static bool Exists(string target)
    {
        IntPtr handle;
        if (!CredRead(target, Generic, 0, out handle)) return false;
        CredFree(handle);
        return true;
    }

    public static bool Matches(string target, string secret, string userName)
    {
        IntPtr handle;
        if (!CredRead(target, Generic, 0, out handle)) return false;
        try
        {
            var credential = (Credential)Marshal.PtrToStructure(handle, typeof(Credential));
            var bytes = new byte[credential.CredentialBlobSize];
            if (bytes.Length > 0) Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes) == secret && credential.UserName == userName;
        }
        finally
        {
            CredFree(handle);
        }
    }

    public static void Delete(string target)
    {
        CredDelete(target, Generic, 0);
    }
}
'@

# A path as it appears inside a JSON string (backslashes escaped).
function ConvertTo-JsonText([string] $Text) { return (ConvertTo-Json $Text).Trim('"') }

# The data folders a chat recorded as its working folder, from the app's conversation files.
function Get-ChatFolders([string] $Root) {
    return @(Get-ChildItem (Join-Path $Root 'conversations') -Filter '*.json' | ForEach-Object {
        (Get-Content $_.FullName -Raw -Encoding utf8 | ConvertFrom-Json).session.cwd } | Where-Object { $_ })
}

$upgrade = [bool] $LegacyInstaller
$keyTarget = $null
$legacyKeyTarget = $null
if ($upgrade) {
    foreach ($folder in @($legacyData, $data)) {
        if (Test-Path $folder) { throw "$folder already exists. The upgrade test seeds and removes these folders, so it only runs where neither exists (a CI runner)." }
    }

    $legacyPath = (Resolve-Path $LegacyInstaller).Path
    Write-Host "Installing the former version first: $legacyPath"
    Invoke-Setup $legacyPath 'contextmenu,desktopicon' (Join-Path $Output 'legacy-install.log')
    $legacyExe = Join-Path $legacyInstallDir 'DSH.exe'
    if (-not (Test-Path $legacyExe)) { throw "The former version did not install DSH.exe to $legacyInstallDir." }
    foreach ($path in @($legacyShortcut, $legacyDesktopShortcut) + $legacyMenuKeys) {
        if (-not (Test-Path $path)) { throw "The former version did not create $path, so the upgrade can't be checked against it." }
    }
    Write-Host "  Installed $((Get-ItemProperty $uninstallKey).DisplayName) $((Get-ItemProperty $uninstallKey).DisplayVersion) to $legacyInstallDir"

    # Legacy: data as the former version leaves it. Its self-test writes a whole data folder with its own code
    # (into a folder of its choosing, never %APPDATA%\DSH); that folder becomes %APPDATA%\DSH, and the paths in
    # it that named its old place name %APPDATA%\DSH, as if DSH had been used there.
    Write-Host 'Filling a data folder with the former version (its self-test)'
    $legacyRun = Join-Path $Output 'legacy-selftest'
    $code = Invoke-App $legacyExe @('--self-test', "`"$legacyRun`"", '--theme', 'light') 300 'The former version''s self-test'
    $legacyHome = Join-Path $legacyRun 'home'
    Write-Host "  Its self-test exited with $code"
    foreach ($file in @('settings.json', 'vault.json', 'task-queue.json')) {
        if (-not (Test-Path (Join-Path $legacyHome $file))) { throw "The former version's self-test wrote no $file to $legacyHome." }
    }
    Copy-Item -Recurse -Path $legacyHome -Destination $legacyData
    Remove-Item -Recurse -Force (Join-Path $legacyData 'logs') -ErrorAction SilentlyContinue
    $fromJson = ConvertTo-JsonText $legacyHome
    $toJson = ConvertTo-JsonText $legacyData
    Get-ChildItem $legacyData -Recurse -File -Filter '*.json' | ForEach-Object {
        $text = [IO.File]::ReadAllText($_.FullName)
        if ($text.Contains($fromJson)) { [IO.File]::WriteAllText($_.FullName, $text.Replace($fromJson, $toJson)) }
    }
    $legacyChats = @(Get-ChildItem (Join-Path $legacyData 'conversations') -Filter '*.json').Count
    $legacyTasks = @((Get-Content (Join-Path $legacyData 'task-queue.json') -Raw | ConvertFrom-Json).tasks).Count
    $legacyVault = @(Get-Content (Join-Path $legacyData 'vault.json') -Raw | ConvertFrom-Json).Count
    $legacySettings = Get-Content (Join-Path $legacyData 'settings.json') -Raw | ConvertFrom-Json
    $legacyRoutes = @($legacySettings.providers).Count
    $scratchChats = @(Get-ChatFolders $legacyData | Where-Object { $_.StartsWith($legacyData + '\', [StringComparison]::OrdinalIgnoreCase) }).Count
    if ($legacyChats -lt 1 -or $legacyTasks -lt 1 -or $legacyVault -lt 1 -or $legacyRoutes -lt 1) {
        throw "The former version's data is missing something: $legacyChats chats, $legacyTasks tasks, $legacyVault vault entries, $legacyRoutes routes."
    }
    Write-Host "  %APPDATA%\DSH: $legacyChats chats ($scratchChats without a project), $legacyTasks tasks, $legacyVault vault entries, $legacyRoutes routes"

    # Legacy: a provider key under the former version's Credential Manager name (DSH/provider/<route>), stored as it stored them.
    $route = "$($legacySettings.providers[0].name)|$($legacySettings.providers[0].model)"
    $legacyKeyTarget = "DSH/provider/$route"
    $keyTarget = "Vision/provider/$route"
    $keyUser = 'installer-test'
    $keySecret = 'upgrade-test-' + [Guid]::NewGuid().ToString('N')
    if ([InstallerTestCredentials]::Exists($legacyKeyTarget) -or [InstallerTestCredentials]::Exists($keyTarget)) {
        throw "Credential Manager already has $legacyKeyTarget or $keyTarget."
    }
    [InstallerTestCredentials]::Write($legacyKeyTarget, $keySecret, $keyUser, 'Stored by DSH for Windows')
    Write-Host "  Stored a key as $legacyKeyTarget"
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
if ((Test-Path $data) -and $upgrade) { throw "The install created $data; only the app may, when it brings the old data over." }

if ($upgrade) {
    Write-Host 'Bringing the former version''s data and key over (Vision.exe --migrate-only)'
    $report = Join-Path $Output 'migrate-only.json'
    $code = Invoke-App $exe @('--migrate-only', "`"$report`"") 120 'Vision.exe --migrate-only'
    if (-not (Test-Path $report)) { throw "Vision.exe --migrate-only exited with $code and wrote no report." }
    Get-Content $report | ForEach-Object { Write-Host "  $_" }
    if ($code -ne 0) { throw "Vision.exe --migrate-only exited with $code." }
    $result = Get-Content $report -Raw | ConvertFrom-Json
    if ($result.result -ne 'Moved') { throw "The data was not moved: $($result.result) ($($result.problem))." }
    if ($result.dataFolder.TrimEnd('\') -ne $data) { throw "Vision runs on $($result.dataFolder), expected $data." }

    # The data: all of it in Vision's folder, none left in DSH's.
    if (Test-Path $legacyData) { throw "$legacyData is still there after the move: $((Get-ChildItem $legacyData -Name) -join ', ')" }
    if ((Get-Content (Join-Path $data $canaryName) -Raw).Trim() -ne 'the user''s data') { throw "A file in the old data folder didn't arrive intact in $data." }
    $chats = @(Get-ChildItem (Join-Path $data 'conversations') -Filter '*.json').Count
    if ($chats -ne $legacyChats) { throw "$data has $chats chats; DSH's folder had $legacyChats." }
    $settingsText = Get-Content (Join-Path $data 'settings.json') -Raw
    if ($settingsText.IndexOf((ConvertTo-JsonText ($legacyData + '\')), [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Vision's settings.json still names a path in $legacyData."
    }
    $folders = Get-ChatFolders $data
    $stale = @($folders | Where-Object { $_.StartsWith($legacyData + '\', [StringComparison]::OrdinalIgnoreCase) })
    if ($stale) { throw "Chats still record working folders in $legacyData`: $($stale -join ', ')" }
    $moved = @($folders | Where-Object { $_.StartsWith($data + '\', [StringComparison]::OrdinalIgnoreCase) }).Count
    if ($moved -ne $scratchChats) { throw "$moved chats work in $data; $scratchChats worked in DSH's folder." }

    # What Vision reads back with its own code: every chat, task, route and vault value (the vault's encryption
    # constants carry the former name and must never change).
    $read = $result.readBack
    if ($read.conversations -ne $legacyChats -or $read.conversationItems -lt $legacyChats) { throw "Vision reads $($read.conversations) chats ($($read.conversationItems) items); there are $legacyChats." }
    if ($read.queueTasks -ne $legacyTasks) { throw "Vision reads $($read.queueTasks) tasks; there are $legacyTasks." }
    if ($read.routes -ne $legacyRoutes) { throw "Vision reads $($read.routes) routes; there are $legacyRoutes." }
    if ($read.vaultEntries -ne $legacyVault -or $read.vaultValuesReadable -ne $legacyVault) {
        throw "Vision decrypts $($read.vaultValuesReadable) of $($read.vaultEntries) vault values; there are $legacyVault ($($read.vaultProblem))."
    }

    # The key: under Vision's name with the same value and user name, and gone from DSH's.
    if ($result.keysBroughtOver -lt 1) { throw "No Credential Manager entry was brought over." }
    if (-not [InstallerTestCredentials]::Matches($keyTarget, $keySecret, $keyUser)) { throw "$keyTarget is missing or doesn't hold the key that was under $legacyKeyTarget." }
    if ([InstallerTestCredentials]::Exists($legacyKeyTarget)) { throw "$legacyKeyTarget is still there after the key was brought over." }
    if ($read.routesWithKeys -lt 1) { throw 'Vision finds no route key under its own names.' }
    Write-Host "  Moved to $data with $chats chats ($moved working folders rewritten), $($read.queueTasks) tasks, $($read.vaultValuesReadable) vault values that decrypt; the key is now $keyTarget"

    # Once only: a second run finds it done and changes nothing.
    $again = Join-Path $Output 'migrate-only-again.json'
    $code = Invoke-App $exe @('--migrate-only', "`"$again`"") 120 'Vision.exe --migrate-only (again)'
    $second = Get-Content $again -Raw | ConvertFrom-Json
    if ($code -ne 0 -or $second.result -ne 'AlreadyDone' -or $second.keysBroughtOver -ne 0) {
        throw "A second --migrate-only run exited with $code, $($second.result), $($second.keysBroughtOver) keys."
    }
    if ($second.readBack.conversations -ne $legacyChats -or $second.readBack.vaultValuesReadable -ne $legacyVault -or (Test-Path $legacyData)) {
        throw 'A second --migrate-only run changed the data.'
    }
    if (-not [InstallerTestCredentials]::Matches($keyTarget, $keySecret, $keyUser)) { throw "A second run changed $keyTarget." }
    Write-Host '  A second run found it done and changed nothing'
    $canary = Join-Path $data $canaryName
}

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
if ((Get-Content $canary -Raw).Trim() -ne 'the user''s data') { throw "Uninstall changed the user's data folder ($(Split-Path $canary))." }
if ($upgrade -and -not (Test-Path (Join-Path $data 'settings.json'))) { throw "Uninstall removed the user's data ($data)." }

if ($upgrade) {
    Remove-Item -Recurse -Force $data
    [InstallerTestCredentials]::Delete($keyTarget)
    [InstallerTestCredentials]::Delete($legacyKeyTarget)
} else {
    Remove-Item -Force $canary
    if ($madeLegacyData) { Remove-Item -Force -Recurse $legacyData }
}
Write-Host 'Installer test passed.'
