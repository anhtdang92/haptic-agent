; CtrlAgent Windows installer (Inno Setup 6).
; The fixed AppId is the upgrade identity. Never change it after public release.

#ifndef DisplayVersion
  #define DisplayVersion "v0.0.0-dev"
#endif
#ifndef PackageVersion
  #define PackageVersion "0.0.0.0"
#endif
#ifndef StagingDir
  #define StagingDir "..\artifacts\staging"
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef RepoRoot
  #define RepoRoot ".."
#endif

#define AppId "{{B836BB38-7B10-48AF-97E5-54BD1DFF1763}"
#define AppName "CtrlAgent"
#define AppPublisher "CtrlAgent Project"
#define AppExeName "CtrlAgent.Gui.exe"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#DisplayVersion}
AppVerName={#AppName} {#DisplayVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/anhtdang92/haptic-agent
AppSupportURL=https://github.com/anhtdang92/haptic-agent/issues
VersionInfoVersion={#PackageVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=CtrlAgent Windows Installer
VersionInfoProductName={#AppName}
DefaultDirName={localappdata}\Programs\CtrlAgent
DefaultGroupName=CtrlAgent
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=CtrlAgent-Setup-{#DisplayVersion}-win-x64
SetupIconFile={#RepoRoot}\src\CtrlAgent.Gui\Assets\icon.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
CreateUninstallRegKey=yes
Uninstallable=yes
ChangesAssociations=no
ChangesEnvironment=no
AllowNoIcons=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start CtrlAgent when Windows starts"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\CtrlAgent"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autoprograms}\CtrlAgent Console"; Filename: "{app}\CtrlAgent.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\CtrlAgent"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{userstartup}\CtrlAgent"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch CtrlAgent"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\CtrlAgent"; ValueType: string; ValueName: "InstalledVersion"; ValueData: "{#DisplayVersion}"; Flags: uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\CtrlAgent"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekeyifempty

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /IM {#AppExeName} /F"; Flags: runhidden; RunOnceId: "StopCtrlAgentGui"
Filename: "{cmd}"; Parameters: "/c taskkill /IM CtrlAgent.App.exe /F"; Flags: runhidden; RunOnceId: "StopCtrlAgentApp"

; User settings, profiles, logs, and encrypted credentials live under AppData and
; are intentionally preserved by uninstall, upgrade, and rollback.
