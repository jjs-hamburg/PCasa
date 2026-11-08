#define MyAppName "PCasa"
#define MyAppVersion "0.1.Alpha.1"

[Setup]
AppId={{3A78A421-2B72-45E6-8CCB-A1085696E6D1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={autopf}\PCasa
DefaultGroupName=PCasa
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=PCasa-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousPrivileges=yes
UsePreviousAppDir=yes
UninstallDisplayIcon={app}\PCasa.App.exe
CloseApplications=yes
CloseApplicationsFilter=PCasa.App.exe
RestartApplications=no
AppMutex=Global\PCasa.Desktop.SingleInstance
WizardStyle=modern
SetupLogging=yes
VersionInfoVersion=0.1.0.1
VersionInfoDescription=PCasa Setup
VersionInfoProductName=PCasa
VersionInfoProductVersion=0.1.0.1

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
; Both elevated and ordinary PCasa users need to write the same SQLite WAL,
; journal and thumbnail packs. Never put user preferences in this directory.
Name: "{commonappdata}\PCasa"; Permissions: users-modify; Check: IsAdminInstallMode

[InstallDelete]
; A machine-wide marker must never leak into a per-user installation.
Type: files; Name: "{app}\PCasa.all-users"

[Files]
Source: "publish\PCasa\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installer\PCasa.all-users"; DestDir: "{app}"; Flags: ignoreversion; Check: IsAdminInstallMode

[Icons]
Name: "{autoprograms}\PCasa"; Filename: "{app}\PCasa.App.exe"
Name: "{autodesktop}\PCasa"; Filename: "{app}\PCasa.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\PCasa.App.exe"; Description: "{cm:LaunchProgram,PCasa}"; Flags: nowait postinstall skipifsilent
