; CtrlAgent Windows installer (Inno Setup 6).
; Built by .github/workflows/release.yml, which passes:
;   /DAppVersion=<tag>        e.g. v0.1.0
;   /DStagingDir=<dir>        the assembled self-contained package
;   /DRepoRoot=<dir>          repository root (for the icon)
; Installs per-user by default (no admin prompt); the user can elevate to
; install for all users from the dialog.

#ifndef AppVersion
  #define AppVersion "v0.0.0"
#endif

[Setup]
AppId={{8C1B2A44-52F1-4B7E-9E51-CTRLAGENT001}
AppName=CtrlAgent
AppVersion={#AppVersion}
AppPublisher=CtrlAgent Project
AppPublisherURL=https://github.com/anhtdang92/haptic-agent
AppSupportURL=https://github.com/anhtdang92/haptic-agent/issues
DefaultDirName={autopf}\CtrlAgent
DefaultGroupName=CtrlAgent
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=CtrlAgent-Setup-{#AppVersion}
SetupIconFile={#RepoRoot}\src\CtrlAgent.Gui\Assets\icon.ico
UninstallDisplayIcon={app}\CtrlAgent.Gui.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startup"; Description: "Start CtrlAgent when Windows starts (tray app)"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\CtrlAgent"; Filename: "{app}\CtrlAgent.Gui.exe"
Name: "{group}\CtrlAgent console host"; Filename: "{app}\CtrlAgent.App.exe"
Name: "{group}\Hardware validation wizard"; Filename: "{app}\CtrlAgent.App.exe"; Parameters: "--validate"
Name: "{autodesktop}\CtrlAgent"; Filename: "{app}\CtrlAgent.Gui.exe"; Tasks: desktopicon
Name: "{userstartup}\CtrlAgent"; Filename: "{app}\CtrlAgent.Gui.exe"; Tasks: startup

[Run]
Filename: "{app}\CtrlAgent.Gui.exe"; Description: "Launch CtrlAgent"; Flags: nowait postinstall skipifsilent
