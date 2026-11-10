#define MyAppName "PCasa"
#ifndef MyAppVersion
  #error MyAppVersion must be passed by tools/Build-PCasaInstaller.ps1
#endif
#ifndef MyFileVersion
  #error MyFileVersion must be passed by tools/Build-PCasaInstaller.ps1
#endif

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
SetupIconFile=..\src\PCasa.App\Assets\PCasa.ico
SetupLogging=yes
VersionInfoVersion={#MyFileVersion}
VersionInfoDescription=PCasa Setup
VersionInfoProductName=PCasa
VersionInfoProductVersion={#MyFileVersion}

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Messages]
de.PrivilegesRequiredOverrideCurrentUser=Für &aktuellen Benutzer installieren
de.PrivilegesRequiredOverrideCurrentUserRecommended=Für &aktuellen Benutzer installieren (empfohlen)
en.PrivilegesRequiredOverrideCurrentUser=Install for &current user
en.PrivilegesRequiredOverrideCurrentUserRecommended=Install for &current user (recommended)

[CustomMessages]
de.RemovePCasaData=Auch alle PCasa-Daten löschen (Datenbank, Sicherungen, Cache und Einstellungen)?%n%nJa: PCasa wird bei einer Neuinstallation jungfräulich gestartet.%nNein: Daten für eine spätere Neuinstallation behalten.%nAbbrechen: Deinstallation nicht starten.
en.RemovePCasaData=Also delete all PCasa data (catalog, backups, cache, settings)?%n%nYes: next PCasa installation starts fresh.%nNo: preserve data for reinstall.%nCancel: do not uninstall.
de.DataRemovalFailed=PCasa-Daten konnten nicht vollständig gelöscht werden. Bitte prüfen Sie:
en.DataRemovalFailed=PCasa data could not be completely deleted. Please check:
de.AllUsersRemovalWarning=%nACHTUNG: Der gemeinsame PCasa-Katalog unter ProgramData wird für alle Benutzer entfernt.
en.AllUsersRemovalWarning=%nWARNING: The shared PCasa catalog under ProgramData will be removed for all users.
de.RuntimeConsent=PCasa benötigt die Microsoft .NET 10 Desktop Runtime (x64). Sie ist auf diesem Computer nicht installiert.%n%nSoll sie jetzt von Microsoft heruntergeladen und mit sichtbarem Microsoft-Installationsprogramm installiert werden? Der Download kann einige Zeit dauern.%n%nNein bricht die PCasa-Installation ab, ohne die bestehende Installation zu verändern.
en.RuntimeConsent=PCasa requires the Microsoft .NET 10 Desktop Runtime (x64), which is not installed.%n%nDownload it from Microsoft and run the visible Microsoft installer now? Downloading may take some time.%n%nNo cancels PCasa setup without changing the existing installation.
de.RuntimeDeclined=Die Microsoft .NET 10 Desktop Runtime wird benötigt. Die PCasa-Installation wurde nicht gestartet.
en.RuntimeDeclined=Microsoft .NET 10 Desktop Runtime is required. PCasa installation has not started.
de.RuntimeSilentMissing=Microsoft .NET 10 Desktop Runtime fehlt. Bei einer unbeaufsichtigten Installation muss sie vorher installiert werden.
en.RuntimeSilentMissing=Microsoft .NET 10 Desktop Runtime is missing. Install it before running unattended setup.

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
Type: files; Name: "{app}\ReachFramework.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\UIAutomation*.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\wpfgfx_cor3.dll"; Check: OldSelfContainedInstallation
Type: files; Name: "{app}\vcruntime140_cor3.dll"; Check: OldSelfContainedInstallation
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
  ScriptPath, Args, PowerShellPath: String;
begin
  Result := '';
  ExtractTemporaryFile('Check-DesktopRuntime.ps1');
  ScriptPath := ExpandConstant('{tmp}\\Check-DesktopRuntime.ps1');
  PowerShellPath := ExpandConstant('{sys}\\WindowsPowerShell\\v1.0\\powershell.exe');
  Args := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '" -ProbeOnly';
  if not Exec(PowerShellPath, Args, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Result := 'Microsoft .NET 10 Desktop Runtime could not be checked.';
    Exit;
  end;
  if ExitCode = 0 then Exit;
  if ExitCode <> 10 then
  begin
    Result := 'Microsoft .NET 10 Desktop Runtime prerequisite check failed.';
    Exit;
  end;

  // Never download or install a prerequisite before explicit user consent.
  // Unattended installation must provision the runtime separately.
  if WizardSilent then
  begin
    Result := CustomMessage('RuntimeSilentMissing');
    Exit;
  end;
  if MsgBox(CustomMessage('RuntimeConsent'), mbConfirmation, MB_YESNO) <> IDYES then
  begin
    Result := CustomMessage('RuntimeDeclined');
    Exit;
  end;

  // The PowerShell helper downloads and validates the official binary.
  // Microsoft's runtime installer itself is launched with its own visible UI.
  Args := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '"';
  if not Exec(PowerShellPath, Args, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Result := 'Microsoft .NET 10 Desktop Runtime could not be started.';
    Exit;
  end;
  if (ExitCode <> 0) and (ExitCode <> 3010) then
  begin
    Result := 'Microsoft .NET 10 Desktop Runtime installation failed or was cancelled. ' +
      'The existing PCasa installation has not been changed.';
    Exit;
  end;
  if ExitCode = 3010 then NeedsRestart := True;
end;

[Code]
// Data deletion must be an explicit decision at uninstall, never an update
// or a silent automation. Do not touch original photos in monitored folders.
var
  RemoveUserData: Boolean;
  RemoveSharedData: Boolean;

function InitializeUninstall(): Boolean;
var
  Choice: Integer;
begin
  RemoveUserData := False;
  RemoveSharedData := False;
  Result := True;
  // Silent uninstall retains personal data, unless the caller explicitly
  // supplies the purge flag (used by controlled installer regression tests).
  if UninstallSilent() then
  begin
    if Pos('/PCASAPURGEDATA', UpperCase(GetCmdTail())) <> 0 then
    begin
      RemoveUserData := True;
      RemoveSharedData := FileExists(ExpandConstant('{app}\PCasa.all-users'));
    end;
    Exit;
  end;

  if FileExists(ExpandConstant('{app}\PCasa.all-users')) then
    Choice := MsgBox(CustomMessage('RemovePCasaData') +
      CustomMessage('AllUsersRemovalWarning'), mbConfirmation, MB_YESNOCANCEL)
  else
    Choice := MsgBox(CustomMessage('RemovePCasaData'), mbConfirmation, MB_YESNOCANCEL);
  if Choice = IDCANCEL then
  begin
    Result := False;
    Exit;
  end;
  if Choice = IDYES then
  begin
    RemoveUserData := True;
    // The ProgramData catalog is only used by the all-users installation.
    // The installed marker must be checked before [Files] removes it.
    RemoveSharedData := FileExists(ExpandConstant('{app}\PCasa.all-users'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserData, SharedData, Failures: String;
begin
  if (CurUninstallStep <> usPostUninstall) or not RemoveUserData then Exit;
  Failures := '';
  UserData := ExpandConstant('{localappdata}\PCasa');
  if DirExists(UserData) and not DelTree(UserData, True, True, True) then
    Failures := UserData;
  if RemoveSharedData then
  begin
    SharedData := ExpandConstant('{commonappdata}\PCasa');
    if DirExists(SharedData) and not DelTree(SharedData, True, True, True) then
      Failures := Failures + #13#10 + SharedData;
  end;
  if Failures <> '' then
    MsgBox(CustomMessage('DataRemovalFailed') + #13#10 + Failures,
      mbError, MB_OK);
end;
