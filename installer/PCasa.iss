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
UsePreviousPrivileges=no
UsePreviousAppDir=no
DisableDirPage=yes
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
de.RuntimeDownloadDescription=Download von Microsoft. Mit Abbrechen wird die PCasa-Installation beendet.
en.RuntimeDownloadDescription=Downloading from Microsoft. Cancel ends PCasa Setup.
de.ScopeTitle=Datenbereich
en.ScopeTitle=Data scope
de.ScopeDescription=Das Programm wird unter Programme installiert. Wer darf die PCasa-Datenbank gemeinsam verwenden?
en.ScopeDescription=The program is installed under Program Files. Who may share the PCasa catalog?
de.ScopeCurrent=Nur aktueller Benutzer (private Datenbank)
en.ScopeCurrent=Current user only (private catalog)
de.ScopeAll=Alle Benutzer (gemeinsame Datenbank)
en.ScopeAll=All users (shared catalog)

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
; Both elevated and ordinary PCasa users need to write the same SQLite WAL,
; journal and thumbnail packs. Never put user preferences in this directory.
Name: "{commonappdata}\PCasa"; Permissions: users-modify; Check: IsAllUsersSelected
Name: "{commonappdata}\PCasa\Texturen"; Permissions: users-modify; Check: IsAllUsersSelected
Name: "{commonappdata}\PCasa\GPX"; Permissions: users-modify; Check: IsAllUsersSelected
Name: "{commonappdata}\PCasa\Undo"; Permissions: users-modify; Check: IsAllUsersSelected
Name: "{localappdata}\PCasa"; Check: IsCurrentUserSelected
Name: "{localappdata}\PCasa\Texturen"; Check: IsCurrentUserSelected
Name: "{localappdata}\PCasa\GPX"; Check: IsCurrentUserSelected
Name: "{localappdata}\PCasa\Undo"; Check: IsCurrentUserSelected

[InstallDelete]
; Retire only the bundled legacy texture names on upgrade. Never clear the whole
; user-editable Texturen folder (users may have added their own JPGs).
Type: files; Name: "{app}\\Texturen\\Leinwand hell.jpg"
Type: files; Name: "{app}\\Texturen\\Leinwand dunkel.jpg"
Type: files; Name: "{app}\\Texturen\\Leinwand hell - fein.jpg"
Type: files; Name: "{app}\\Texturen\\Leinwand dunkel - fein.jpg"
Type: files; Name: "{app}\\Texturen\\Stein - Schiefer dunkel.jpg"
Type: files; Name: "{app}\\Texturen\\seide1.webp"
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
Source: "PCasa.all-users"; DestDir: "{app}"; Flags: ignoreversion; Check: IsAllUsersSelected
Source: "..\default-textures\*.jpg"; DestDir: "{commonappdata}\PCasa\Texturen"; Flags: ignoreversion onlyifdoesntexist; Check: IsAllUsersSelected
Source: "..\default-textures\*.jpg"; DestDir: "{localappdata}\PCasa\Texturen"; Flags: ignoreversion onlyifdoesntexist; Check: IsCurrentUserSelected
Source: "Check-DesktopRuntime.ps1"; Flags: dontcopy

[Registry]
Root: HKCU; Subkey: "Software\\PCasa"; ValueType: string; ValueName: "PendingSetupLanguage"; ValueData: "{language}"

[Icons]
Name: "{commonprograms}\PCasa"; Filename: "{app}\PCasa.App.exe"; Check: IsAllUsersSelected
Name: "{userprograms}\PCasa"; Filename: "{app}\PCasa.App.exe"; Check: IsCurrentUserSelected
Name: "{commondesktop}\PCasa"; Filename: "{app}\PCasa.App.exe"; Tasks: desktopicon; Check: IsAllUsersSelected
Name: "{userdesktop}\PCasa"; Filename: "{app}\PCasa.App.exe"; Tasks: desktopicon; Check: IsCurrentUserSelected

[Run]
Filename: "{app}\PCasa.App.exe"; Description: "{cm:LaunchProgram,PCasa}"; Flags: nowait postinstall skipifsilent

[Code]
function OldSelfContainedInstallation(): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\hostfxr.dll'));
end;

// Install the official Microsoft prerequisite BEFORE replacing any PCasa file.
// Failure/cancel preserves the prior PCasa installation and catalog.
var
  RuntimeDownloadPage: TDownloadWizardPage;
  InstallScopePage: TInputOptionWizardPage;

function IsAllUsersSelected(): Boolean;
begin
  Result := InstallScopePage.SelectedValueIndex = 1;
end;

function IsCurrentUserSelected(): Boolean;
begin
  Result := not IsAllUsersSelected();
end;

procedure InitializeWizard();
begin
  InstallScopePage := CreateInputOptionPage(wpWelcome,
    'PCasa – ' + CustomMessage('ScopeTitle'),
    CustomMessage('ScopeDescription'), '', True, False);
  InstallScopePage.Add(CustomMessage('ScopeCurrent'));
  InstallScopePage.Add(CustomMessage('ScopeAll'));
  if Pos('/ALLUSERS', UpperCase(GetCmdTail())) > 0 then
    InstallScopePage.SelectedValueIndex := 1
  else if Pos('/CURRENTUSER', UpperCase(GetCmdTail())) > 0 then
    InstallScopePage.SelectedValueIndex := 0
  else if FileExists(ExpandConstant('{autopf}\PCasa\PCasa.all-users')) then
    InstallScopePage.SelectedValueIndex := 1
  else
    InstallScopePage.SelectedValueIndex := 0;
  RuntimeDownloadPage := CreateDownloadPage(
    'Microsoft .NET 10 Desktop Runtime',
    CustomMessage('RuntimeDownloadDescription'),
    nil);
  RuntimeDownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  ScriptPath, Args, PowerShellPath, MetadataPath, RuntimeUrl, RuntimeHash: String;
  MetadataLines: TArrayOfString;
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

  // Resolve Microsoft release metadata in a hidden, short-running helper.
  // The actual download is performed by Inno Setup's native, cancellable UI.
  MetadataPath := ExpandConstant('{tmp}\\pcasa-runtime-download.txt');
  Args := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ScriptPath + '" -ResolveOnly -MetadataPath "' + MetadataPath + '"';
  if not Exec(PowerShellPath, Args, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or
    (ExitCode <> 0) or
    not LoadStringsFromFile(MetadataPath, MetadataLines) then
  begin
    Result := 'Microsoft .NET 10 Desktop Runtime download information could not be obtained.';
    Exit;
  end;
  if GetArrayLength(MetadataLines) <> 2 then
  begin
    Result := 'Invalid Microsoft .NET Desktop Runtime download information.';
    Exit;
  end;
  RuntimeUrl := Trim(MetadataLines[0]);
  RuntimeHash := Trim(MetadataLines[1]);
  if (Pos('https://', RuntimeUrl) <> 1) or (Length(RuntimeHash) <> 128) then
  begin
    Result := 'Invalid Microsoft .NET Desktop Runtime download URL or checksum.';
    Exit;
  end;

  RuntimeDownloadPage.Clear;
  RuntimeDownloadPage.Add(RuntimeUrl, 'windowsdesktop-runtime-win-x64.exe', '');
  RuntimeDownloadPage.Show;
  try
    try
      RuntimeDownloadPage.Download;
    except
      if RuntimeDownloadPage.AbortedByUser then
        Result := CustomMessage('RuntimeDeclined')
      else
        Result := 'Microsoft .NET Desktop Runtime download failed: ' + GetExceptionMessage;
      Exit;
    end;
  finally
    RuntimeDownloadPage.Hide;
  end;

  // The trusted helper checks Microsoft SHA-512 AND Authenticode before
  // launching the official installer with its own visible Windows UI.
  Args := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ScriptPath + '" -InstallDownloaded -InstallerFile "' +
    ExpandConstant('{tmp}\\windowsdesktop-runtime-win-x64.exe') +
    '" -ExpectedHash "' + RuntimeHash + '"';
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
