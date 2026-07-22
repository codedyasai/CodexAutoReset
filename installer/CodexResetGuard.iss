#define AppName "CodexResetGuard"
#define AppExecutable "CodexResetGuard.exe"

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

[Setup]
AppId={{8D5D7C2C-6DE7-4B57-A788-4D8E4680B43B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=CodexResetGuard-Setup-x64
SetupIconFile=..\src\CodexResetGuard.Desktop\Assets\CodexResetGuard.ico
UninstallDisplayIcon={app}\{#AppExecutable}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
CloseApplicationsFilter={#AppExecutable}
RestartApplications=no
SignedUninstaller=no
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription=Codex 주간 한도 초기화권 보호 도구
VersionInfoCompany={#AppName}
VersionInfoCopyright=Apache-2.0

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면 바로가기 만들기"; GroupDescription: "추가 작업:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExecutable}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExecutable}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExecutable}"; Description: "{#AppName} 실행"; Flags: nowait postinstall skipifsilent

[Code]
procedure RemoveOwnedStartupRegistration;
var
  RunValue: String;
  OwnerValue: String;
  ExpectedValue: String;
begin
  if not RegQueryStringValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Run',
    'CodexResetGuard',
    RunValue) then
  begin
    Exit;
  end;

  if not RegQueryStringValue(
    HKCU,
    'Software\CodexResetGuard\Startup',
    'OwnerId',
    OwnerValue) then
  begin
    Exit;
  end;

  ExpectedValue := '"' + ExpandConstant('{app}\CodexResetGuard.exe')
    + '" --background --startup-owner=' + OwnerValue;
  if CompareText(RunValue, ExpectedValue) <> 0 then
  begin
    Exit;
  end;

  RegDeleteValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Run',
    'CodexResetGuard');
  RegDeleteValue(
    HKCU,
    'Software\CodexResetGuard\Startup',
    'OwnerId');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveOwnedStartupRegistration;
  end;
end;
