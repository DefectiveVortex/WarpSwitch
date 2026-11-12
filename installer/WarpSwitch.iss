; WARP Switch installer (Inno Setup 6). Built by build.ps1:
;   ISCC /DAppVersion=2.1.0 /DSourceExe=..\bin\Release\net48\WarpSwitch.exe /O..\dist installer\WarpSwitch.iss
;
; Installs to Program Files (admin-only, which is what lets "Start with Windows" run elevated safely), upgrades
; in place, closes a running copy first (it disconnects and puts the network back), and is what the in-app
; updater runs silently:  WarpSwitch-Setup-x.y.z.exe /SILENT /relaunch=connect|show

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\bin\Release\net48\WarpSwitch.exe"
#endif
#define AppName "WARP Switch"
#define AppExe "WarpSwitch.exe"
#define RepoUrl "https://github.com/DefectiveVortex/WarpSwitch"

[Setup]
AppId={{6F3B2A1E-9C4D-4E7A-B5F1-2D8C0A7E4B93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Vortex
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
AppCopyright=Copyright (C) 2026 Vortex
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} installer
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableWelcomePage=no
UsePreviousTasks=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
OutputBaseFilename=WarpSwitch-Setup-{#AppVersion}
SetupIconFile=..\res\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=no
RestartApplications=no
SetupMutex=WarpSwitchSetup
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "autostart"; Description: "&Start WARP Switch in the tray when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "{#AppExe}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "System-wide Cloudflare WARP"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--set-autostart"; Tasks: autostart; Flags: runhidden waituntilterminated; StatusMsg: "Setting up Start with Windows..."
Filename: "{app}\{#AppExe}"; Description: "Open {#AppName}"; Flags: nowait postinstall skipifsilent
; After an in-app update (silent): start the new version, reconnecting if WARP was on.
Filename: "{app}\{#AppExe}"; Parameters: "{code:RelaunchArgs}"; Flags: nowait; Check: IsUpdateRelaunch

[UninstallRun]
; Quits a running copy (it disconnects), removes the sign-in task, undoes any leftover network changes and the
; admin-only helper folder.
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "WarpSwitchCleanup"

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\WARP Switch"

[Code]
const
  CMD_QUIT = 5;                                   { Program.Command.Quit }
  APP_MUTEX = 'Local\WarpSwitch.Instance.v1';
{ HWND_BROADCAST, PostMessage and RegisterWindowMessage are Inno Setup built-ins. }

function AppRunning: Boolean;
begin
  Result := CheckForMutexes(APP_MUTEX);
end;

{ Ask a running copy (any version, including a portable one) to disconnect and quit, then wait for it. }
function CloseRunningApp: Boolean;
var
  I: Integer;
begin
  Result := True;
  if not AppRunning then Exit;
  Log('WARP Switch is running: asking it to disconnect and quit.');
  PostMessage(HWND_BROADCAST, RegisterWindowMessage('WarpSwitch.Show.v1'), CMD_QUIT, 0);
  for I := 1 to 160 do
  begin
    Sleep(250);
    if not AppRunning then Exit;
  end;
  Result := not AppRunning;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if CloseRunningApp then
    Result := ''
  else
    Result := 'WARP Switch is still running. Quit it from its tray icon (right-click, Quit), then try again.';
end;

function IsUpdateRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|}') <> '';
end;

function RelaunchArgs(Param: String): String;
begin
  if CompareText(ExpandConstant('{param:relaunch|}'), 'connect') = 0 then
    Result := '--updated --connect'
  else
    Result := '--updated';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    if MsgBox('Also remove your WARP registration, settings and logs?' + #13#10#13#10 +
              'Keep them if you might reinstall: WARP Switch would reuse the same registration.',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\WarpSwitch'), True, True, True);
end;
