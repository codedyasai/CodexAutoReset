#define AppName "CodexAutoReset"
#define AppExecutable "CodexAutoReset.exe"
#define LegacyAppName "CodexResetGuard"
#define LegacyAppExecutable "CodexResetGuard.exe"

#ifndef AppVersion
  #define AppVersion "0.2.2"
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
UsePreviousAppDir=yes
UsePreviousGroup=no
OutputDir=..\artifacts\installer
OutputBaseFilename=CodexAutoReset-Setup-x64
SetupIconFile=..\src\CodexAutoReset.Desktop\Assets\CodexAutoReset.ico
UninstallDisplayIcon={app}\{#AppExecutable}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
CloseApplicationsFilter={#AppExecutable},{#LegacyAppExecutable}
RestartApplications=no
SignedUninstaller=no
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription=Codex 주간 한도 자동 초기화 도구
VersionInfoCompany={#AppName}
VersionInfoCopyright=Apache-2.0

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면 바로가기 만들기"; GroupDescription: "추가 작업:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\CodexResetGuard.exe"
Type: files; Name: "{app}\CodexResetGuard.dll"
Type: files; Name: "{app}\CodexResetGuard.deps.json"
Type: files; Name: "{app}\CodexResetGuard.runtimeconfig.json"
Type: files; Name: "{app}\CodexResetGuard.AppServer.dll"
Type: files; Name: "{app}\CodexResetGuard.Core.dll"
Type: files; Name: "{app}\CodexResetGuard.Runtime.dll"
Type: files; Name: "{app}\CodexResetGuard-LICENSE.txt"
Type: files; Name: "{userprograms}\CodexResetGuard\CodexResetGuard.lnk"
Type: dirifempty; Name: "{userprograms}\CodexResetGuard"
Type: files; Name: "{autodesktop}\CodexResetGuard.lnk"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExecutable}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExecutable}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExecutable}"; Description: "{#AppName} 실행"; Flags: nowait postinstall skipifsilent

[Code]
function IsHexDigit(Value: Char): Boolean;
begin
  Result := ((Value >= '0') and (Value <= '9'))
    or ((Value >= 'a') and (Value <= 'f'))
    or ((Value >= 'A') and (Value <= 'F'));
end;

function IsOwnerId(Value: String): Boolean;
var
  Index: Integer;
begin
  Result := False;
  if Length(Value) <> 36 then
  begin
    Exit;
  end;

  for Index := 1 to Length(Value) do
  begin
    if (Index = 9) or (Index = 14) or (Index = 19) or (Index = 24) then
    begin
      if Value[Index] <> '-' then
      begin
        Exit;
      end;
    end
    else if not IsHexDigit(Value[Index]) then
    begin
      Exit;
    end;
  end;

  Result := CompareText(Value, '00000000-0000-0000-0000-000000000000') <> 0;
end;

procedure DeleteStringValueIfUnchanged(
  RootKey: Integer;
  Subkey: String;
  ValueName: String;
  ExpectedValue: String);
var
  CurrentValue: String;
begin
  if RegQueryStringValue(RootKey, Subkey, ValueName, CurrentValue)
    and (CompareText(CurrentValue, ExpectedValue) = 0) then
  begin
    RegDeleteValue(RootKey, Subkey, ValueName);
  end;
end;

function TryBuildMigratedStartupValue(
  RunValue: String;
  OwnerValue: String;
  var NewValue: String): Boolean;
var
  ExpectedSuffix: String;
  LegacyPath: String;
  SuffixStart: Integer;
begin
  Result := False;
  if (Length(RunValue) > 2048) or not IsOwnerId(OwnerValue) then
  begin
    Exit;
  end;

  ExpectedSuffix := '" --background --startup-owner=' + OwnerValue;
  if (Length(RunValue) <= Length(ExpectedSuffix) + 1)
    or (RunValue[1] <> '"') then
  begin
    Exit;
  end;

  SuffixStart := Length(RunValue) - Length(ExpectedSuffix) + 1;
  if CompareText(
    Copy(RunValue, SuffixStart, Length(ExpectedSuffix)),
    ExpectedSuffix) <> 0 then
  begin
    Exit;
  end;

  LegacyPath := Copy(RunValue, 2, SuffixStart - 2);
  if (LegacyPath = '')
    or (Pos('"', LegacyPath) <> 0)
    or (ExtractFileDrive(LegacyPath) = '')
    or (CompareText(ExpandFileName(LegacyPath), LegacyPath) <> 0)
    or (CompareText(ExtractFileName(LegacyPath), '{#LegacyAppExecutable}') <> 0) then
  begin
    Exit;
  end;

  NewValue := '"' + ExpandConstant('{app}\{#AppExecutable}')
    + '" --background --startup-owner=' + OwnerValue;
  Result := True;
end;

procedure MigrateOwnedLegacyStartupRegistration;
var
  RunValue: String;
  OwnerValue: String;
  NewValue: String;
  CurrentNewRunValue: String;
  CurrentNewOwnerValue: String;
  VerifiedLegacyRunValue: String;
  VerifiedLegacyOwnerValue: String;
  NewRunExists: Boolean;
  NewOwnerExists: Boolean;
  CreatedNewValues: Boolean;
begin
  if not RegQueryStringValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Run',
    '{#LegacyAppName}',
    RunValue) then
  begin
    Exit;
  end;

  if not RegQueryStringValue(
    HKCU,
    'Software\{#LegacyAppName}\Startup',
    'OwnerId',
    OwnerValue) then
  begin
    Exit;
  end;

  if not TryBuildMigratedStartupValue(RunValue, OwnerValue, NewValue) then
  begin
    Exit;
  end;

  NewRunExists := RegQueryStringValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Run',
    '{#AppName}',
    CurrentNewRunValue);
  NewOwnerExists := RegQueryStringValue(
    HKCU,
    'Software\{#AppName}\Startup',
    'OwnerId',
    CurrentNewOwnerValue);
  if NewRunExists <> NewOwnerExists then
  begin
    Exit;
  end;

  CreatedNewValues := not NewRunExists;
  if NewRunExists then
  begin
    if (CompareText(CurrentNewRunValue, NewValue) <> 0)
      or (CompareText(CurrentNewOwnerValue, OwnerValue) <> 0) then
    begin
      Exit;
    end;
  end
  else
  begin
    if not RegWriteStringValue(
      HKCU,
      'Software\{#AppName}\Startup',
      'OwnerId',
      OwnerValue) then
    begin
      Exit;
    end;

    if not RegWriteStringValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      '{#AppName}',
      NewValue) then
    begin
      DeleteStringValueIfUnchanged(
        HKCU,
        'Software\{#AppName}\Startup',
        'OwnerId',
        OwnerValue);
      Exit;
    end;
  end;

  if not RegQueryStringValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Run',
    '{#AppName}',
    CurrentNewRunValue)
    or not RegQueryStringValue(
      HKCU,
      'Software\{#AppName}\Startup',
      'OwnerId',
      CurrentNewOwnerValue)
    or (CompareText(CurrentNewRunValue, NewValue) <> 0)
    or (CompareText(CurrentNewOwnerValue, OwnerValue) <> 0) then
  begin
    if CreatedNewValues then
    begin
      DeleteStringValueIfUnchanged(
        HKCU,
        'Software\Microsoft\Windows\CurrentVersion\Run',
        '{#AppName}',
        NewValue);
      DeleteStringValueIfUnchanged(
        HKCU,
        'Software\{#AppName}\Startup',
        'OwnerId',
        OwnerValue);
    end;
    Exit;
  end;

  if RegQueryStringValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Run',
    '{#LegacyAppName}',
    VerifiedLegacyRunValue)
    and RegQueryStringValue(
      HKCU,
      'Software\{#LegacyAppName}\Startup',
      'OwnerId',
      VerifiedLegacyOwnerValue)
    and (CompareText(VerifiedLegacyRunValue, RunValue) = 0)
    and (CompareText(VerifiedLegacyOwnerValue, OwnerValue) = 0) then
  begin
    RegDeleteValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      '{#LegacyAppName}');
    RegDeleteValue(
      HKCU,
      'Software\{#LegacyAppName}\Startup',
      'OwnerId');
  end;
end;

procedure RemoveOwnedStartupRegistration(
  RunValueName: String;
  OwnerSubkey: String;
  ExecutableName: String);
var
  RunValue: String;
  OwnerValue: String;
  ExpectedValue: String;
begin
  if not RegQueryStringValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Run',
    RunValueName,
    RunValue) then
  begin
    Exit;
  end;

  if not RegQueryStringValue(
    HKCU,
    OwnerSubkey,
    'OwnerId',
    OwnerValue) then
  begin
    Exit;
  end;

  ExpectedValue := '"' + ExpandConstant('{app}\') + ExecutableName
    + '" --background --startup-owner=' + OwnerValue;
  if CompareText(RunValue, ExpectedValue) <> 0 then
  begin
    Exit;
  end;

  RegDeleteValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Run',
    RunValueName);
  RegDeleteValue(
    HKCU,
    OwnerSubkey,
    'OwnerId');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    MigrateOwnedLegacyStartupRegistration;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveOwnedStartupRegistration(
      '{#AppName}',
      'Software\{#AppName}\Startup',
      '{#AppExecutable}');
    RemoveOwnedStartupRegistration(
      '{#LegacyAppName}',
      'Software\{#LegacyAppName}\Startup',
      '{#LegacyAppExecutable}');
  end;
end;
