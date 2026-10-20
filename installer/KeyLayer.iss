; KeyLayer installer, built with Inno Setup 6 by .github/workflows/build.yml:
;   ISCC /DAppVersion=1.2.1 /DSourceExe=path\to\KeyLayer.exe /Opath\to\output installer\KeyLayer.iss
; Installs for the current user only (no admin rights), into %LOCALAPPDATA%\Programs\KeyLayer.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\dist\KeyLayer.exe"
#endif

#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
AppId={{8F3B6C2E-5A41-4D7B-9E2F-6B1C0D4A7E93}
AppName=KeyLayer
AppVersion={#AppVersion}
AppVerName=KeyLayer {#AppVersion}
AppPublisher=Nirajsaud
AppPublisherURL=https://github.com/Nirajsaud/KeyLayer
AppSupportURL=https://github.com/Nirajsaud/KeyLayer/issues
AppUpdatesURL=https://github.com/Nirajsaud/KeyLayer/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\KeyLayer
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\release
OutputBaseFilename=KeyLayer-Setup
SetupIconFile=..\src\KeyLayer\app.ico
UninstallDisplayIcon={app}\KeyLayer.exe
UninstallDisplayName=KeyLayer
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; KeyLayer is closed by the code below instead (it lives in the tray, so there's no window to ask)
CloseApplications=no

[Tasks]
Name: startup; Description: "Start KeyLayer when I sign in (it waits quietly in the tray)"
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\KeyLayer"; Filename: "{app}\KeyLayer.exe"
Name: "{autodesktop}\KeyLayer"; Filename: "{app}\KeyLayer.exe"; Tasks: desktopicon

[Registry]
; The same entry KeyLayer's own "Start with Windows" setting uses, so the two stay in step
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "KeyLayer"; ValueData: """{app}\KeyLayer.exe"" --tray"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\KeyLayer.exe"; Description: "Open KeyLayer now"; Flags: nowait postinstall skipifsilent

[Code]
// Only one KeyLayer can run at a time, and its file can't be replaced or removed while it runs.
procedure CloseKeyLayer();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im KeyLayer.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseKeyLayer();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  CloseKeyLayer();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command, Settings: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;

  // "Start with Windows" may have been turned on inside KeyLayer rather than here: remove it if it points at this copy.
  if RegQueryStringValue(HKCU, '{#RunKey}', 'KeyLayer', Command) and
     (Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Command)) > 0) then
    RegDeleteValue(HKCU, '{#RunKey}', 'KeyLayer');

  // Shortcuts and settings live in %APPDATA%\KeyLayer. Keep them unless the user says otherwise.
  Settings := ExpandConstant('{userappdata}\KeyLayer');
  if DirExists(Settings) and not UninstallSilent and
     (MsgBox('Also delete your KeyLayer shortcuts and settings?' + #13#10#13#10 +
             'Keep them if you might install KeyLayer again.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    DelTree(Settings, True, True, True);
end;
