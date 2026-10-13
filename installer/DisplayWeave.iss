; Inno Setup script for DisplayWeave. Per-user, no administrator rights. Replaces the former
; custom C# setup and install.ps1, so nothing runs PowerShell to install or at sign-in.
;
; The payload is baked into this Authenticode-signed setup, so there is no untrusted ZIP to verify
; here; the app's updater verifies a downloaded setup's release-key signature before running it.
;
; build-setup.ps1 compiles this with ISCC and passes:
;   /DAppVersion=<version>  the release version (folder name under versions\)
;   /DPayloadDir=<dir>      the extracted package payload (DisplayWeave.exe, native-v2-*, etc.)
;   /DOutputDir=<dir>       where to write DisplayWeave-Setup-<version>.exe

#ifndef AppVersion
  #error AppVersion must be defined (build with build-setup.ps1)
#endif

[Setup]
AppId={{B3F7B1C2-6E4A-4C8E-9D2E-7A1F4C5B9E30}
AppName=DisplayWeave
AppVersion={#AppVersion}
AppPublisher=Alexandre St-Pierre
DefaultDirName={localappdata}\DisplayWeave
DefaultGroupName=DisplayWeave
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename=DisplayWeave-Setup-{#AppVersion}
SetupIconFile={#SourcePath}\..\src\DisplayWeave\app.ico
UninstallDisplayName=DisplayWeave
UninstallDisplayIcon={app}\versions\{#AppVersion}\DisplayWeave.exe
; A per-user app that only writes under %LOCALAPPDATA% must not trip Windows' installer-elevation detection.
CloseApplications=no
; Always honour /DIR (the updater passes the existing root); never silently reuse a prior install's folder.
UsePreviousAppDir=no
; The automated tests pass /testmode=1 so they touch no shared state (Start menu, uninstall registry).
CreateUninstallRegKey=NeedUninstallReg
#ifdef Sign
; Release builds (build-setup.ps1 -Sign) sign the setup and the uninstaller built into it, so the installed
; unins000.exe carries the same publisher signature as the rest of DisplayWeave.
SignTool=artifactsigning
SignedUninstaller=yes
#endif

[Files]
; The version payload is self-contained under versions\<version>\. Old version folders from earlier
; installs are left in place, because a hooked application may still have the previous DLL loaded.
Source: "{#PayloadDir}\*"; DestDir: "{app}\versions\{#AppVersion}"; Flags: recursesubdirs createallsubdirs ignoreversion
; The stable launcher also sits at the install root; the shortcut and startup entry point at it.
Source: "{#PayloadDir}\DisplayWeave.Launcher.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\DisplayWeave"; Filename: "{app}\DisplayWeave.Launcher.exe"; IconFilename: "{app}\versions\{#AppVersion}\DisplayWeave.exe"; Check: ShouldCreateIcons
Name: "{app}\DisplayWeave"; Filename: "{app}\DisplayWeave.Launcher.exe"; IconFilename: "{app}\versions\{#AppVersion}\DisplayWeave.exe"

[Tasks]
; Offered only on a first install; an update leaves the sign-in setting as it is.
Name: "autostart"; Description: "Start DisplayWeave when I sign in"; Flags: unchecked; Check: IsFirstInstall

[Run]
; Runs on interactive installs (as a checkbox) and on the updater's silent install (to relaunch).
Filename: "{app}\DisplayWeave.Launcher.exe"; Description: "Open DisplayWeave now"; Flags: nowait postinstall; Check: ShouldLaunch

[UninstallDelete]
Type: files; Name: "{app}\current.json"
Type: filesandordirs; Name: "{app}\health"
Type: dirifempty; Name: "{app}"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

var
  PreviousVersion: String;
  UnrelatedRunning: Boolean;

function GetCurrentProcessId: Cardinal; external 'GetCurrentProcessId@kernel32.dll stdcall';
function ProcessIdToSessionId(ProcessId: Cardinal; var SessionId: Cardinal): Integer; external 'ProcessIdToSessionId@kernel32.dll stdcall';

function IsFirstInstall: Boolean;
begin
  Result := not FileExists(ExpandConstant('{app}\current.json'));
end;

// The automated tests pass /testmode=1 so a test install touches no shared state and starts nothing.
function IsTestMode: Boolean;
begin
  Result := ExpandConstant('{param:testmode|0}') = '1';
end;

// A second instance next to another installation's DisplayWeave would fight it for the same windows.
function ShouldLaunch: Boolean;
begin
  Result := not IsTestMode and not UnrelatedRunning;
end;

function ShouldCreateIcons: Boolean;
begin
  Result := not IsTestMode;
end;

function NeedUninstallReg: Boolean;
begin
  Result := not IsTestMode;
end;

// Reads the "version" field from an existing current.json, so an update can record the version it replaces.
function ReadInstalledVersion: String;
var
  text: AnsiString;
  marker: String;
  start, finish: Integer;
begin
  Result := '';
  if not LoadStringFromFile(ExpandConstant('{app}\current.json'), text) then
    exit;
  marker := '"version"';
  start := Pos(marker, text);
  if start = 0 then exit;
  // Move to the opening quote of the value after the colon.
  start := start + Length(marker);
  while (start <= Length(text)) and (text[start] <> '"') do
    start := start + 1;
  start := start + 1;
  finish := start;
  while (finish <= Length(text)) and (text[finish] <> '"') do
    finish := finish + 1;
  if finish <= Length(text) then
    Result := Copy(text, start, finish - start);
end;

// Counts the DisplayWeave.exe processes in this session: owned ones run from under this install root;
// any other, including one whose path cannot be read (such as an elevated one), counts as unrelated.
procedure FindDisplayWeave(var OwnedPath: String; var Owned, Unrelated: Integer);
var
  locator, service, items, item: Variant;
  root, path: String;
  session: Cardinal;
  i: Integer;
begin
  OwnedPath := '';
  Owned := 0;
  Unrelated := 0;
  root := Lowercase(AddBackslash(ExpandConstant('{app}')));
  ProcessIdToSessionId(GetCurrentProcessId, session);
  locator := CreateOleObject('WbemScripting.SWbemLocator');
  service := locator.ConnectServer('.', 'root\CIMV2');
  items := service.ExecQuery(
    'SELECT ExecutablePath FROM Win32_Process WHERE Name = ''DisplayWeave.exe'' AND SessionId = ' + IntToStr(session));
  for i := 0 to items.Count - 1 do
  begin
    item := items.ItemIndex(i);
    path := '';
    if not VarIsNull(item.ExecutablePath) then
      path := item.ExecutablePath;
    if (path <> '') and (Pos(root, Lowercase(path)) = 1) then
    begin
      OwnedPath := path;
      Owned := Owned + 1;
    end
    else
      Unrelated := Unrelated + 1;
  end;
end;

// Asks this install's running DisplayWeave to save and exit, then waits for it; nothing is terminated.
// --exit-for-update reaches every DisplayWeave instance, so it is never sent while an unrelated one runs.
// Returns an error message, or '' when no owned instance is left running.
function CloseOwnedDisplayWeave: String;
var
  ownedPath: String;
  owned, unrelated, code, waited: Integer;
begin
  Result := '';
  try
    FindDisplayWeave(ownedPath, owned, unrelated);
    UnrelatedRunning := unrelated > 0;
    if owned = 0 then
      exit;
    if unrelated > 0 then
    begin
      Result := 'Another DisplayWeave installation is running. Close DisplayWeave yourself and retry; no unrelated instance was stopped.';
      exit;
    end;
    if not Exec(ownedPath, '--exit-for-update', '', SW_HIDE, ewWaitUntilTerminated, code) or (code <> 0) then
    begin
      Result := 'DisplayWeave did not accept the request to close. Close it yourself and retry. No process was terminated.';
      exit;
    end;
    waited := 0;
    repeat
      Sleep(250);
      waited := waited + 250;
      FindDisplayWeave(ownedPath, owned, unrelated);
    until (owned = 0) or (waited >= 15000);
    if owned > 0 then
      Result := 'DisplayWeave is still running, possibly waiting to save settings. Close it and retry. No process was forcibly terminated.';
  except
    Result := 'Could not check whether DisplayWeave is running (' + GetExceptionMessage + '). Close it yourself and retry.';
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // Read before anything is written, so it still tells a first install from an update.
  PreviousVersion := ReadInstalledVersion;
  Result := CloseOwnedDisplayWeave;
end;

function InitializeUninstall: Boolean;
var
  error: String;
begin
  error := CloseOwnedDisplayWeave;
  Result := error = '';
  if not Result and not UninstallSilent then
    MsgBox(error, mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  json: String;
  health: String;
begin
  if CurStep = ssPostInstall then
  begin
    // The stable pointer the launcher reads. Record the replaced version so the launcher can roll back.
    json := '{' + #13#10 +
      '    "version":  "{#AppVersion}",' + #13#10 +
      '    "executable":  "versions/{#AppVersion}/DisplayWeave.exe",' + #13#10 +
      '    "dataDirectory":  "data"';
    if (PreviousVersion <> '') and (PreviousVersion <> '{#AppVersion}') and
       FileExists(ExpandConstant('{app}\versions\' + PreviousVersion + '\DisplayWeave.exe')) then
      json := json + ',' + #13#10 + '    "previousVersion":  "' + PreviousVersion + '"';
    json := json + #13#10 + '}' + #13#10;
    SaveStringToFile(ExpandConstant('{app}\current.json'), json, False);

    // A fresh install of this version starts with no counted starts; a version given up stays skipped.
    health := ExpandConstant('{app}\health\');
    DeleteFile(health + '{#AppVersion}.starts');
    DeleteFile(health + '{#AppVersion}.ok');

    CreateDir(ExpandConstant('{app}\data'));

    // Sign-in entry: written only when asked for on a first install. An update keeps the user's choice, and
    // an unticked box never clears a value that may belong to another DisplayWeave copy. Tests never touch it.
    if (PreviousVersion = '') and not IsTestMode and WizardIsTaskSelected('autostart') then
      RegWriteStringValue(HKCU, RunKey, 'DisplayWeave', '"' + ExpandConstant('{app}\DisplayWeave.Launcher.exe') + '" --minimized');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  value: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // The value name is shared with other DisplayWeave copies; remove it only when it starts this install.
    if RegQueryStringValue(HKCU, RunKey, 'DisplayWeave', value) and
       (Pos(Lowercase(AddBackslash(ExpandConstant('{app}'))), Lowercase(value)) > 0) then
      RegDeleteValue(HKCU, RunKey, 'DisplayWeave');
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    // Settings are kept unless the user asks otherwise; a silent uninstall always keeps them.
    if not UninstallSilent and DirExists(ExpandConstant('{app}\data')) and
       (MsgBox('Also delete your DisplayWeave regions and app rules?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(ExpandConstant('{app}\data'), True, True, True);
    RemoveDir(ExpandConstant('{app}'));
  end;
end;
