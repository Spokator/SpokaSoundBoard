; SpokaSoundBoard installer (Inno Setup 6).
; Build with scripts\build-installer.ps1, which publishes the app and downloads yt-dlp first.

#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif
#define AppName "SpokaSoundBoard"
#define AppExe "SpokaSoundBoard.exe"
#define AppPublisher "Spoka"

[Setup]
AppId={{6F3B2E1C-8A4D-4C2B-9E57-5B1D2C7A9F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppComments=Soundboard, musique et effets de voix pour Discord et les jeux
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} - programme d'installation
; Per-user install by default: no administrator prompt for the application itself.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableWelcomePage=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\src\SpokaSoundBoard\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=wizard-100.bmp,wizard-200.bmp
WizardSmallImageFile=wizard-small-100.bmp,wizard-small-200.bmp
Compression=lzma2/ultra64
SolidCompression=yes
; Windows Restart Manager closes a running instance gracefully before updating (settings are saved on close).
CloseApplications=force
RestartApplications=no

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[CustomMessages]
fr.DesktopIcon=Créer un raccourci sur le Bureau
fr.Autostart=Lancer {#AppName} au démarrage de Windows (réduit)
fr.Options=Options :
fr.LaunchApp=Lancer {#AppName} et ouvrir l'assistant de configuration
fr.RemoveData=Voulez-vous aussi supprimer vos réglages, vos sons et vos musiques téléchargées ?%n%n(Choisissez « Non » pour les conserver en cas de réinstallation.)
fr.DriversKept=Les micros virtuels (VB-CABLE, Virtual Audio Cable) ne sont pas supprimés car d'autres logiciels peuvent les utiliser. Vous pouvez les désinstaller depuis Paramètres Windows > Applications si besoin.

[Messages]
fr.WelcomeLabel2=Cet assistant va installer [name/ver] sur votre ordinateur.%n%nAprès l'installation, l'assistant de configuration de SpokaSoundBoard vérifiera votre micro, installera le micro virtuel en un clic et vous guidera pour Discord et vos jeux.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:Options}"
Name: "autostart"; Description: "{cm:Autostart}"; GroupDescription: "{cm:Options}"; Flags: unchecked

[Files]
Source: "..\dist\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "deps\yt-dlp.exe"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; DestName: "LISEZ-MOI.md"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Soundboard et effets de voix"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon; Comment: "Soundboard et effets de voix"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
  ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
const
  AppMutexName = 'SpokaSoundBoard.SingleInstance';

{ Asks a running SpokaSoundBoard to close (WM_CLOSE, so it saves its settings) before uninstalling. }
function InitializeUninstall(): Boolean;
var
  ResultCode, I: Integer;
begin
  Result := True;
  if CheckForMutexes(AppMutexName) then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    I := 0;
    while CheckForMutexes(AppMutexName) and (I < 50) do
    begin
      Sleep(100);
      I := I + 1;
    end;
    if CheckForMutexes(AppMutexName) then
      Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Startup entries may also have been created from inside the app. }
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\RunOnce', '{#AppName}');
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}\{#AppName}');
    if DirExists(DataDir) and not UninstallSilent then
    begin
      if MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
    if not UninstallSilent then
      MsgBox(CustomMessage('DriversKept'), mbInformation, MB_OK);
  end;
end;
