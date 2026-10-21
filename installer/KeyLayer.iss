; KeyLayer installer, built with Inno Setup 6 by .github/workflows/build.yml:
;   powershell -File installer\make-art.ps1        (draws the artwork into installer\art)
;   ISCC /DAppVersion=1.2.3 /DFileVersion=1.2.3 /DSourceExe=path\to\KeyLayer.exe /Opath\to\output installer\KeyLayer.iss
; Installs for the current user only (no admin rights), into %LOCALAPPDATA%\Programs\KeyLayer.
; KeyLayer-Setup.exe /PREVIEWGOODBYE=1 shows the uninstaller's goodbye window and exits (for checking how it looks).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef FileVersion
  #define FileVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\dist\KeyLayer.exe"
#endif

#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
#define IssuesUrl "https://github.com/Nirajsaud/KeyLayer/issues/new"

[Setup]
AppId={{8F3B6C2E-5A41-4D7B-9E2F-6B1C0D4A7E93}
AppName=KeyLayer
AppVersion={#AppVersion}
AppVerName=KeyLayer {#AppVersion}
AppPublisher=Nirajsaud
AppPublisherURL=https://github.com/Nirajsaud/KeyLayer
AppSupportURL=https://github.com/Nirajsaud/KeyLayer/issues
AppUpdatesURL=https://github.com/Nirajsaud/KeyLayer/releases
VersionInfoVersion={#FileVersion}
DefaultDirName={autopf}\KeyLayer
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
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
WizardImageFile=art\wizard-164.bmp,art\wizard-192.bmp,art\wizard-246.bmp,art\wizard-273.bmp,art\wizard-328.bmp,art\wizard-410.bmp
WizardSmallImageFile=art\small-55.bmp,art\small-64.bmp,art\small-83.bmp,art\small-92.bmp,art\small-110.bmp,art\small-138.bmp
; KeyLayer is closed by the code below instead (it lives in the tray, so there's no window to ask)
CloseApplications=no

[Messages]
WelcomeLabel1=Welcome to KeyLayer
WelcomeLabel2=Let's give your keys new superpowers.%n%nKeyLayer turns any key, including the Fn keys on your keyboard, into shortcuts that open apps, type text, control your music and more. Every keyboard can have its own.%n%nSetup takes about ten seconds.
FinishedHeadingLabel=You're all set!
ConfirmUninstall=Remove %1 from this PC?
UninstalledAll=%1 has been removed from your PC.%n%nThanks for giving it a try!

[Tasks]
Name: startup; Description: "Start KeyLayer when I sign in (it waits quietly in the tray)"
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
; Shown by the goodbye window when uninstalling
Source: "art\goodbye-logo.bmp"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\KeyLayer"; Filename: "{app}\KeyLayer.exe"
Name: "{autodesktop}\KeyLayer"; Filename: "{app}\KeyLayer.exe"; Tasks: desktopicon

[Registry]
; The same entry KeyLayer's own "Start with Windows" setting uses, so the two stay in step
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "KeyLayer"; ValueData: """{app}\KeyLayer.exe"" --tray"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\KeyLayer.exe"; Description: "Open KeyLayer now"; Flags: nowait postinstall skipifsilent

[Code]
var
  FnPage: TInputOptionWizardPage;
  ChosenFnVk: Integer;
  DeleteSettings: Boolean;

// ---------------------------------------------------------------- shared

// Only one KeyLayer can run at a time, and its file can't be replaced or removed while it runs.
procedure CloseKeyLayer();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im KeyLayer.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function ConfigPath(): String;
begin
  Result := ExpandConstant('{userappdata}\KeyLayer\config.json');
end;

procedure OpenFeedback(Sender: TObject);
var
  ErrorCode: Integer;
begin
  ShellExec('open', '{#IssuesUrl}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

// The goodbye window shown when uninstalling. Returns mrNo when the shortcuts should be deleted too.
function ShowGoodbye(LogoPath: String): Integer;
var
  Form: TSetupForm;
  Logo: TBitmapImage;
  Title, Body, Link: TNewStaticText;
  Keep, Delete: TNewButton;
begin
  // Keep this size: by default Inno Setup stretches custom windows to the wizard's size.
  // From 6.6 on, CreateCustomForm scales the size it's given itself, so it gets the unscaled numbers.
#if Ver >= EncodeVer(6, 6, 0)
  Form := CreateCustomForm(460, 214, True, True);
#else
  Form := CreateCustomForm();
  Form.KeepSizeX := True;
  Form.KeepSizeY := True;
  Form.ClientWidth := ScaleX(460);
  Form.ClientHeight := ScaleY(214);
#endif
  try
    Form.Caption := 'Uninstall KeyLayer';

    Logo := TBitmapImage.Create(Form);
    Logo.Parent := Form;
    Logo.SetBounds(ScaleX(24), ScaleY(24), ScaleX(56), ScaleY(56));
    Logo.Stretch := True;
    if FileExists(LogoPath) then Logo.Bitmap.LoadFromFile(LogoPath);

    Title := TNewStaticText.Create(Form);
    Title.Parent := Form;
    Title.SetBounds(ScaleX(96), ScaleY(24), ScaleX(340), ScaleY(26));
    Title.Font.Size := 14;
    Title.Font.Style := [fsBold];
    Title.Caption := 'Sorry to see you go!';

    Body := TNewStaticText.Create(Form);
    Body.Parent := Form;
    Body.AutoSize := False;
    Body.WordWrap := True;
    Body.SetBounds(ScaleX(96), ScaleY(58), ScaleX(340), ScaleY(64));
    Body.Caption := 'Your shortcuts and settings are saved separately from the app. Keep them, and they''ll be ' +
                    'right there if you install KeyLayer again.';

    Link := TNewStaticText.Create(Form);
    Link.Parent := Form;
    Link.SetBounds(ScaleX(96), ScaleY(124), ScaleX(340), ScaleY(18));
    Link.Caption := 'Something didn''t work? Tell me on GitHub';
    Link.Font.Color := clHotLight;
    Link.Font.Style := [fsUnderline];
    Link.Cursor := crHand;
    Link.OnClick := @OpenFeedback;

    Delete := TNewButton.Create(Form);
    Delete.Parent := Form;
    Delete.SetBounds(Form.ClientWidth - ScaleX(24 + 120), ScaleY(166), ScaleX(120), ScaleY(28));
    Delete.Caption := 'Delete them';
    Delete.ModalResult := mrNo;

    Keep := TNewButton.Create(Form);
    Keep.Parent := Form;
    Keep.SetBounds(Delete.Left - ScaleX(8 + 150), ScaleY(166), ScaleX(150), ScaleY(28));
    Keep.Caption := 'Keep my shortcuts';
    Keep.ModalResult := mrYes;
    Keep.Default := True;

    Form.ActiveControl := Keep;
    Form.Position := poScreenCenter;
    Result := Form.ShowModal();
  finally
    Form.Free();
  end;
end;

// ---------------------------------------------------------------- install

function InitializeSetup(): Boolean;
begin
  Result := True;
  if ExpandConstant('{param:PREVIEWGOODBYE|0}') = '1' then
  begin
    ExtractTemporaryFile('goodbye-logo.bmp');
    ShowGoodbye(ExpandConstant('{tmp}\goodbye-logo.bmp'));
    Result := False;
  end;
end;

// The KeyLayer Fn key from an earlier install, or 0.
function ExistingFnVk(): Integer;
var
  Json: AnsiString;
  Text: String;
  Start: Integer;
begin
  Result := 0;
  if not LoadStringFromFile(ConfigPath(), Json) then Exit;
  Text := Json;
  Start := Pos('"CustomFnVk": ', Text);
  if Start = 0 then Exit;
  Text := Copy(Text, Start + 14, 4);
  if Pos(',', Text) > 0 then Text := Copy(Text, 1, Pos(',', Text) - 1);
  Result := StrToIntDef(Trim(Text), 0);
end;

function FnVkFor(Index: Integer): Integer;
begin
  case Index of
    0: Result := 20;   // Caps Lock
    1: Result := 163;  // Right Ctrl
    2: Result := 165;  // Right Alt
    3: Result := 93;   // Menu key
  else
    Result := 0;       // decide later
  end;
end;

function FnNameFor(Vk: Integer): String;
begin
  case Vk of
    20: Result := 'Caps Lock';
    163: Result := 'Right Ctrl';
    165: Result := 'Right Alt';
    93: Result := 'the Menu key';
  else
    Result := '';
  end;
end;

// The Ready to Install page also lists the KeyLayer Fn key that was picked.
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
var
  Fn: String;
begin
  if ChosenFnVk <> 0 then Fn := FnNameFor(ChosenFnVk) else Fn := 'None yet (pick one later in KeyLayer)';
  if Fn = 'the Menu key' then Fn := 'Menu key';
  Result := MemoTasksInfo;
  if Result <> '' then Result := Result + NewLine + NewLine;
  Result := Result + 'KeyLayer Fn key:' + NewLine + Space + Fn;
end;

procedure InitializeWizard();
begin
  FnPage := CreateInputOptionPage(wpSelectTasks,
    'Pick your KeyLayer Fn key',
    'Your keyboard''s own Fn key is invisible to apps, so KeyLayer uses one of its own.',
    'Hold this key and press another key to run a shortcut. Pressed on its own, it still does its normal job. ' +
    'You can change it any time in KeyLayer''s Settings.',
    True, False);
  FnPage.Add('Caps Lock  (recommended)');
  FnPage.Add('Right Ctrl');
  FnPage.Add('Right Alt');
  FnPage.Add('Menu key');
  FnPage.Add('I''ll decide later');
  FnPage.SelectedValueIndex := 0;
  ChosenFnVk := ExistingFnVk();
end;

// Someone updating who already has a KeyLayer Fn key isn't asked again.
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = FnPage.ID) and (ExistingFnVk() <> 0);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  if CurPageID = FnPage.ID then ChosenFnVk := FnVkFor(FnPage.SelectedValueIndex);
  Result := True;
end;

// Writes the chosen Fn key into KeyLayer's settings: into an existing file that has none, or a new one.
procedure SaveFnKey(Vk: Integer);
var
  Json: AnsiString;
  Text, Path: String;
begin
  Path := ConfigPath();
  if LoadStringFromFile(Path, Json) then
  begin
    Text := Json;
    if StringChangeEx(Text, '"CustomFnVk": 0,', '"CustomFnVk": ' + IntToStr(Vk) + ',', True) > 0 then
      SaveStringToFile(Path, Text, False);
  end
  else
  begin
    ForceDirectories(ExtractFileDir(Path));
    SaveStringToFile(Path, '{' + #13#10 + '  "Version": 1,' + #13#10 + '  "CustomFnVk": ' + IntToStr(Vk) + ',' + #13#10 +
                     '  "FnWelcomeDismissed": true' + #13#10 + '}', False);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Vk: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  // /FNKEY=<key code> picks the key without the wizard (silent installs and the build's install test)
  Vk := StrToIntDef(ExpandConstant('{param:FNKEY|-1}'), -1);
  if Vk < 0 then
    if WizardSilent then Vk := 0 else Vk := ChosenFnVk;
  if (Vk > 0) and (ExistingFnVk() = 0) then SaveFnKey(Vk);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID <> wpFinished then Exit;
  if ChosenFnVk <> 0 then
    WizardForm.FinishedLabel.Caption :=
      'KeyLayer lives in your tray, the NS icon near the clock.' + #13#10#13#10 +
      'Try it: click Add shortcut, hold ' + FnNameFor(ChosenFnVk) + ' and press any key. ' +
      'Pick what it should do, and that''s your first shortcut.'
  else
    WizardForm.FinishedLabel.Caption :=
      'KeyLayer lives in your tray, the NS icon near the clock.' + #13#10#13#10 +
      'Open it to make your first shortcut. It will offer to set up a KeyLayer Fn key, since your keyboard''s own ' +
      'Fn key is invisible to apps.';
end;

// ---------------------------------------------------------------- uninstall

function InitializeUninstall(): Boolean;
begin
  DeleteSettings := False;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command, Settings: String;
begin
  Settings := ExpandConstant('{userappdata}\KeyLayer');
  case CurUninstallStep of
    usUninstall:
      begin
        // Asked here, after Windows' "Remove KeyLayer?" and while the logo is still installed.
        if DirExists(Settings) and not UninstallSilent then
          DeleteSettings := ShowGoodbye(ExpandConstant('{app}\goodbye-logo.bmp')) = mrNo;
        CloseKeyLayer();
      end;
    usPostUninstall:
      begin
        // "Start with Windows" may have been turned on inside KeyLayer rather than here: remove it if it points at this copy.
        if RegQueryStringValue(HKCU, '{#RunKey}', 'KeyLayer', Command) and
           (Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Command)) > 0) then
          RegDeleteValue(HKCU, '{#RunKey}', 'KeyLayer');
        if DeleteSettings then DelTree(Settings, True, True, True);
      end;
  end;
end;
