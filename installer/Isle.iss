; Установщик Isle (Inno Setup 6). Ставится без прав администратора в %LocalAppData%\Programs\Isle.
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

[Setup]
AppId={{8F1C2B7A-3E4D-4C5B-9A1F-7E2D6C3B1A90}
AppName=Isle
AppVersion={#MyAppVersion}
AppVerName=Isle {#MyAppVersion}
AppPublisher=Jeredpoi
DefaultDirName={localappdata}\Programs\Isle
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\out
OutputBaseFilename=Isle-Setup
SetupIconFile=..\src\Isle\app.ico
UninstallDisplayIcon={app}\Isle.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Запускать Isle вместе с Windows"
Name: "desktop"; Description: "Ярлык Isle на рабочем столе"

[Files]
Source: "..\out\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Isle"; Filename: "{app}\Isle.exe"; IconFilename: "{app}\Isle.exe"; Comment: "Isle — чёлка для Windows"
Name: "{autodesktop}\Isle"; Filename: "{app}\Isle.exe"; IconFilename: "{app}\Isle.exe"; Comment: "Isle — чёлка для Windows"; Tasks: desktop

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Isle"; ValueData: """{app}\Isle.exe"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Isle.exe"; Description: "Запустить Isle"; Flags: nowait postinstall

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im Isle.exe"; Flags: runhidden; RunOnceId: "KillIsle"

[Code]
// Перед установкой закрываем все запущенные копии Isle (в том числе «невидимые» старые).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Isle.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(400);
  Result := '';
end;
