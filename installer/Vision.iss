; DSH for Windows installer (Inno Setup 6.3 or later, including Inno Setup 7).
;
; Built by scripts/package.ps1:
;   ISCC /DAppVersion=0.7.123 /DArch=x64 /DSourceDir=...\publish\win-x64\DSH /DOutputDir=...\artifacts installer\Vision.iss
;
; Installs per user by default (no administrator rights, no UAC prompt) into
; %LOCALAPPDATA%\Programs\DSH; choosing "install for all users" in the dialog uses Program Files.
; User data (%APPDATA%\DSH: settings, conversations, skills) is never touched by install or uninstall.

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
  #define SourceDir "..\artifacts\publish\win-" + Arch + "\DSH"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define AppName "DSH"
#define AppExe "Vision.exe"
#define RepoUrl "https://github.com/phastings503cm/dshwindows"

[Setup]
AppId={{6F3B2E7A-9C41-4D8B-A5E2-3C7D9B1F0A64}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=DSH for Windows
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
AppComments=A native coding agent for OpenAI-compatible model servers.
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\DSH
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=DSH-{#AppVersion}-win-{#Arch}-setup
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
; Legacy: before the rename to Vision the executable was DSH.exe, its main assembly DSH.dll and the
; libraries Dsh.Core.dll / Dsh.Windows.dll. Inno Setup only replaces the files it installs this time,
; so an upgrade over an older version would otherwise leave those stale files in the install folder.
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
; Legacy: Vision was called DSH, whose "Open with DSH" lived under shell\DSH and ran DSH.exe. An upgrade
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
