#define MyAppName "PCasa"
#define MyAppVersion "0.1.Alpha.3"

[Setup]
AppId={{3A78A421-2B72-45E6-8CCB-A1085696E6D1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={autopf}\PCasa
DefaultGroupName=PCasa
DisableProgramGroupPage=yes
OutputDir=..\dist
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
VersionInfoVersion=0.1.0.3
VersionInfoDescription=PCasa Setup
VersionInfoProductName=PCasa
VersionInfoProductVersion=0.1.0.3

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

; Alpha.2 was self-contained. Upgrading in-place must not strand Microsoft's
; bundled .NET/WPF runtime in the PCasa directory. Only remove these files
; when the *previous* installation has hostfxr.dll. Third-party dependencies
; needed by the new app are restored from [Files] afterwards.
Type: files; Name: "{app}\System.*.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\Microsoft.*.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\Presentation*.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\WindowsBase.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\Accessibility.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\mscorlib.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\netstandard.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\System.Xaml.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\Ijwhost.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\coreclr.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\clrjit.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\clrgc.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\clrgcexp.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\clretwrc.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\mscorrc.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\mscordaccore*.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\mscordbi.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\msquic.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\PenImc_cor3.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\D3DCompiler_47_cor3.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\DirectWriteForwarder.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\createdump.exe"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\hostpolicy.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\hostfxr.dll"; Check: OldSelfContainedInstallation

[Files]
Source: "..\publish\PCasa\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "PCasa.all-users"; DestDir: "{app}"; Flags: ignoreversion; Check: IsAdminInstallMode
Source: "Check-DesktopRuntime.ps1"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\PCasa"; Filename: "{app}\PCasa.App.exe"
Name: "{autodesktop}\PCasa"; Filename: "{app}\PCasa.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\PCasa.App.exe"; Description: "{cm:LaunchProgram,PCasa}"; Flags: nowait postinstall skipifsilent

[Code]
function OldSelfContainedInstallation(): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\hostfxr.dll'));
end;

// Install the official Microsoft prerequisite BEFORE replacing any PCasa file.
// Failure/cancel preserves the prior PCasa installation and catalog.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  ScriptPath, Args: String;
begin
  Result := '';
  ExtractTemporaryFile('Check-DesktopRuntime.ps1');
  ScriptPath := ExpandConstant('{tmp}\\Check-DesktopRuntime.ps1');
  Args := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '"';
  if not Exec(ExpandConstant('{sys}\\WindowsPowerShell\\v1.0\\powershell.exe'),
     Args, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Result := 'Die Microsoft .NET 10 Desktop Runtime konnte nicht geprüft werden.';
    Exit;
  end;
  if (ExitCode <> 0) and (ExitCode <> 3010) then
  begin
    Result := 'Microsoft .NET 10 Desktop Runtime x64 fehlt oder konnte nicht installiert werden. ' +
      'Die bestehende PCasa-Installation wurde nicht verändert. ' +
      'Bitte die offizielle Desktop Runtime installieren und Setup erneut starten.';
    Exit;
  end;
  if ExitCode = 3010 then
    NeedsRestart := True;
end;
