; Vision for Windows installer (Inno Setup 6.3 or later, including Inno Setup 7).
;
; Built by scripts/package.ps1:
;   ISCC /DAppVersion=0.9.123 /DArch=x64 /DSourceDir=...\publish\win-x64\Vision /DOutputDir=...\artifacts installer\Vision.iss
;
; Installs per user by default (no administrator rights, no UAC prompt) into
; %LOCALAPPDATA%\Programs\Vision; choosing "install for all users" in the dialog uses Program Files.
; User data (%APPDATA%\Vision: settings, conversations, skills) is never touched by install or uninstall.
;
; Upgrading an install made under the app's former name (DSH, up to 0.8): the AppId is the same, so this
; setup upgrades it rather than adding a second app. The new version goes into its own Vision folder; once
; it is installed, [Code] below removes the old DSH folder, its shortcuts and its "Open with DSH" entries.
; The user's data is brought over by the app itself on its first start (see DataFolderMigration).

#if VER < EncodeVer(6,3,0)
  #error Inno Setup 6.3 or later is required (x64compatible architecture identifiers).
#endif

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-" + Arch + "\Vision"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define AppName "Vision"
#define AppExe "Vision.exe"
#define RepoUrl "https://github.com/phastings503cm/vision-windows"

[Setup]
; Never change this AppId: it is what makes this setup an upgrade of every earlier install, including those
; made under the former name, instead of a second entry in Settings > Apps.
AppId={{6F3B2E7A-9C41-4D8B-A5E2-3C7D9B1F0A64}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Vision for Windows
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
AppComments=A native coding agent for OpenAI-compatible model servers.
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
; {autopf} is %LOCALAPPDATA%\Programs for a per-user install and Program Files for an all-users one.
; UsePreviousAppDir=no: an upgrade of an install made under the former name moves from its DSH folder to
; this one instead of staying there (an upgrade of a Vision install lands in the same folder anyway).
DefaultDirName={autopf}\Vision
UsePreviousAppDir=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=Vision-{#AppVersion}-win-{#Arch}-setup
SetupIconFile=..\assets\vision.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
#if VER >= EncodeVer(6,6,0)
; Follows the Windows light/dark setting, like the app. The logo PNGs have transparent corners so
; they sit cleanly on either background; several sizes keep them sharp at 100/150/200% scaling.
WizardStyle=modern dynamic
WizardImageFile=wizard-large.png,wizard-large-150.png,wizard-large-2x.png
WizardSmallImageFile=wizard-small.png,wizard-small-150.png,wizard-small-2x.png
#else
WizardStyle=modern
WizardSmallImageFile=wizard-small.bmp,wizard-small-2x.bmp
#endif
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
MinVersion=10.0.17763
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
  #if VER >= EncodeVer(7,0,0)
; Inno Setup 7 can build the setup program itself as 64-bit. (The ARM64 installer stays 32-bit,
; which every ARM64 Windows runs.)
SetupArchitecture=x64
  #endif
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "contextmenu"; Description: "Add ""Open with Vision"" to the folder right-click menu"; GroupDescription: "Explorer integration:"

[InstallDelete]
; Legacy: under its former name the executable was DSH.exe, its main assembly DSH.dll and the libraries
; Dsh.Core.dll / Dsh.Windows.dll. Inno Setup only replaces the files it installs this time, so installing
; into a folder that still holds that version (chosen by hand on the folder page) would otherwise leave them.
Type: files; Name: "{app}\DSH.exe"
Type: files; Name: "{app}\DSH.dll"
Type: files; Name: "{app}\DSH.deps.json"
Type: files; Name: "{app}\DSH.runtimeconfig.json"
Type: files; Name: "{app}\Dsh.Core.dll"
Type: files; Name: "{app}\Dsh.Windows.dll"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "A native coding agent"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Legacy: under its former name the app's "Open with DSH" lived under shell\DSH and ran DSH.exe. An upgrade
; removes it (whatever the tasks chosen now), so Explorer never keeps a dead entry next to the new one.
Root: HKA; Subkey: "Software\Classes\Directory\shell\DSH"; ValueType: none; Flags: deletekey
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\DSH"; ValueType: none; Flags: deletekey
; "Open with Vision" on folders and on the background of an open folder. HKA is HKCU for a per-user
; install and HKLM for an all-users one.
Root: HKA; Subkey: "Software\Classes\Directory\shell\Vision"; ValueType: string; ValueName: ""; ValueData: "Open with Vision"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\shell\Vision"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\shell\Vision\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\Vision"; ValueType: string; ValueName: ""; ValueData: "Open with Vision"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\Vision"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\Vision\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%V"""; Tasks: contextmenu

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  // The Settings > Apps entry of the previous install (same GUID as AppId above). Inno Setup rewrites it
  // at the end of this install, so it is read before then.
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F3B2E7A-9C41-4D8B-A5E2-3C7D9B1F0A64}_is1';
  // Legacy: what an install made under the former name (DSH, up to 0.8) left: a folder named DSH holding
  // DSH.exe, Start menu and desktop shortcuts named DSH.lnk, and, while it runs, the mutex
  // Local\DSH-Windows-<user> (Vision's own is Local\Vision-Windows-<user>, see SingleInstance.cs).
  LegacyFolderName = 'DSH';
  LegacyExe = 'DSH.exe';
  LegacyShortcut = 'DSH.lnk';
  LegacyMutexPrefix = 'Local\DSH-Windows-';

var
  PreviousDirRead: Boolean;
  PreviousDir: String;
  LegacyFound: Boolean;

// The folder the previous install went to ('' when there is none), as its Settings > Apps entry records it.
function PreviousInstallDir(): String;
var
  Root: Integer;
  Path: String;
begin
  if not PreviousDirRead then
  begin
    PreviousDirRead := True;
    PreviousDir := '';
    if IsAdminInstallMode then Root := HKLM else Root := HKCU;
    if RegQueryStringValue(Root, UninstallKey, 'Inno Setup: App Path', Path) then
      PreviousDir := RemoveBackslashUnlessRoot(Trim(Path));
  end;
  Result := PreviousDir;
end;

function HoldsLegacyApp(const Dir: String): Boolean;
begin
  Result := (Dir <> '') and FileExists(AddBackslash(Dir) + LegacyExe);
end;

// Legacy: the mutex the app held under its former name (its single-instance id DSH-Windows plus the user name).
function LegacyMutexName(): String;
var
  User: String;
begin
  User := GetUserNameString;
  StringChangeEx(User, '\', '_', True);
  Result := LegacyMutexPrefix + User;
end;

// Whether Path is Folder or inside it (case-insensitive, whole folder names only).
function IsWithin(const Path, Folder: String): Boolean;
var
  P, F: String;
begin
  P := AddBackslash(Lowercase(Path));
  F := AddBackslash(Lowercase(Folder));
  Result := Copy(P, 1, Length(F)) = F;
end;

// Whether Dir, the folder of an install made under the former name, may be removed whole: it is named DSH (a
// folder the user named otherwise may hold other things, so it is left alone), it is apart from this install,
// it holds no portable copy's data, and that app isn't running.
function MayRemoveLegacyFolder(const Dir: String): Boolean;
var
  App: String;
begin
  Result := False;
  if not HoldsLegacyApp(Dir) then Exit;
  if CompareText(ExtractFileName(Dir), LegacyFolderName) <> 0 then
  begin
    Log('Leaving the former install in place: its folder has a name of the user''s own: ' + Dir);
    Exit;
  end;
  App := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  if IsWithin(App, Dir) or IsWithin(Dir, App) then
  begin
    Log('Leaving the former install''s folder in place: it is this install''s folder too: ' + Dir);
    Exit;
  end;
  if FileExists(AddBackslash(Dir) + 'portable') or DirExists(AddBackslash(Dir) + 'data') then
  begin
    Log('Leaving the former install in place: it holds a portable copy''s data: ' + Dir);
    Exit;
  end;
  if CheckForMutexes(LegacyMutexName()) then
  begin
    Log('Leaving the former install in place: it is still running: ' + Dir);
    Exit;
  end;
  Result := True;
end;

// Lets "Preparing to install" close the old app (Restart Manager) like it closes a running Vision.exe. Called
// before the install starts, so this is also where the previous install's folder is first read.
procedure RegisterExtraCloseApplicationsResources;
var
  Dir: String;
begin
  Dir := PreviousInstallDir();
  if HoldsLegacyApp(Dir) then
    RegisterExtraCloseApplicationsResource(False, AddBackslash(Dir) + LegacyExe);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Dir: String;
begin
  // Before anything is installed (and before [InstallDelete]): note whether the previous install is one made
  // under the former name.
  if CurStep = ssInstall then
    LegacyFound := HoldsLegacyApp(PreviousInstallDir());

  // Only once the new version is fully installed is the old one removed, so a failed or cancelled install
  // leaves the old one working.
  if (CurStep <> ssPostInstall) or not LegacyFound then Exit;
  Dir := PreviousInstallDir();
  DeleteFile(ExpandConstant('{autoprograms}\') + LegacyShortcut);
  DeleteFile(ExpandConstant('{autodesktop}\') + LegacyShortcut);
  if MayRemoveLegacyFolder(Dir) then
  begin
    if DelTree(Dir, True, True, True) then
      Log('Removed the former install: ' + Dir)
    else
      Log('Could not remove all of the former install: ' + Dir);
  end;
end;
