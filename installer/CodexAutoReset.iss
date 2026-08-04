#define AppName "CodexAutoReset"
#define AppExecutable "CodexAutoReset.exe"
#define LegacyAppName "CodexResetGuard"
#define LegacyAppExecutable "CodexResetGuard.exe"

#ifndef AppVersion
  #define AppVersion "0.3.7"
#endif

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

[Setup]
AppId={{8D5D7C2C-6DE7-4B57-A788-4D8E4680B43B}
AppMutex=Local\CodexAutoReset-8D5D7C2C-6DE7-4B57-A788-4D8E4680B43B
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
VersionInfoDescription=Codex 주간·5시간 한도 자동 초기화 도구
VersionInfoCompany={#AppName}
VersionInfoCopyright=Apache-2.0

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
korean.DeleteUserDataPrompt=설정과 안전 기록도 함께 삭제하시겠습니까?%n%n'아니요'를 선택하면 재설치할 때 기존 설정과 중복 사용 방지 기록을 이어서 사용합니다.%n'예'를 선택하면 이 데이터는 복구할 수 없고, 재설치해도 이전 초기화권 처리 기록이 이어지지 않습니다.
korean.DeleteUserDataFailed=설정과 안전 기록을 완전히 삭제하지 못했습니다.%n%n남은 폴더: %1%n%n앱 제거는 완료되었지만 이 폴더는 직접 삭제해야 합니다.
korean.DeleteUserDataPathRejected=안전 확인에 실패하여 설정과 안전 기록을 삭제하지 않았습니다.%n%n확인할 폴더: %1
korean.DeleteRegistryDataFailed=앱이 소유한 일부 레지스트리 정보를 삭제하지 못했습니다.%n%n앱 제거는 완료되었지만 완전 삭제를 위해 레지스트리를 직접 확인해야 합니다.
english.DeleteUserDataPrompt=Also delete settings and safety records?%n%nChoose No to keep your settings and duplicate-use protection for a future reinstall.%nChoose Yes to delete this data permanently; a reinstall will not retain earlier reset-credit handling records.
english.DeleteUserDataFailed=Settings and safety records could not be completely deleted.%n%nRemaining folder: %1%n%nThe app was removed, but you must delete this folder manually.
english.DeleteUserDataPathRejected=Settings and safety records were not deleted because the data folder failed the safety check.%n%nFolder to review: %1
english.DeleteRegistryDataFailed=Some app-owned registry data could not be deleted.%n%nThe app was removed, but the registry must be reviewed manually for a complete removal.

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
var
  DeleteUserDataOnUninstall: Boolean;

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

function GetUserDataDirectory: String;
begin
  Result := PathCombine(
    RemoveBackslashUnlessRoot(ExpandConstant('{localappdata}')),
    '{#LegacyAppName}');
end;

function IsExpectedUserDataDirectory(Path: String): Boolean;
var
  LocalAppData: String;
  ExpectedPath: String;
begin
  LocalAppData :=
    RemoveBackslashUnlessRoot(ExpandConstant('{localappdata}'));
  ExpectedPath := PathCombine(LocalAppData, '{#LegacyAppName}');
  Path := RemoveBackslashUnlessRoot(Path);

  Result := (LocalAppData <> '')
    and PathIsRooted(LocalAppData)
    and PathSame(Path, ExpectedPath)
    and not PathSame(Path, LocalAppData)
    and PathStartsWith(Path, AddBackslash(LocalAppData), True);
end;

function AskToDeleteUserData: Boolean;
begin
  Result := False;
  if UninstallSilent then
  begin
    Log('Silent uninstall: preserving CodexAutoReset user data.');
    Exit;
  end;

  Result := SuppressibleMsgBox(
    CustomMessage('DeleteUserDataPrompt'),
    mbConfirmation,
    MB_YESNO or MB_DEFBUTTON2,
    IDNO) = IDYES;
end;

procedure DeleteUserDataIfRequested;
var
  UserDataDirectory: String;
  Deleted: Boolean;
begin
  if not DeleteUserDataOnUninstall then
  begin
    Exit;
  end;

  UserDataDirectory := GetUserDataDirectory;
  if not IsExpectedUserDataDirectory(UserDataDirectory) then
  begin
    Log('Refusing to delete unexpected user data path: '
      + UserDataDirectory);
    SuppressibleMsgBox(
      FmtMessage(CustomMessage('DeleteUserDataPathRejected'), [UserDataDirectory]),
      mbError,
      MB_OK,
      IDOK);
    Exit;
  end;

  if DirExists(UserDataDirectory) then
  begin
    Deleted := DelTree(UserDataDirectory, True, True, True);
  end
  else if FileExists(UserDataDirectory) then
  begin
    Deleted := DeleteFile(UserDataDirectory);
  end
  else
  begin
    Deleted := True;
  end;

  if Deleted then
  begin
    Log('Deleted CodexAutoReset user data.');
  end
  else
  begin
    Log('Could not completely delete CodexAutoReset user data: '
      + UserDataDirectory);
    SuppressibleMsgBox(
      FmtMessage(CustomMessage('DeleteUserDataFailed'), [UserDataDirectory]),
      mbError,
      MB_OK,
      IDOK);
  end;
end;

procedure RemoveOwnedRegistryKeys;
var
  CurrentDeleted: Boolean;
  LegacyDeleted: Boolean;
begin
  if DeleteUserDataOnUninstall then
  begin
    CurrentDeleted := True;
    if RegValueExists(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      '{#AppName}') then
    begin
      Log('Preserving CodexAutoReset registry ownership because a startup '
        + 'registration from another copy is still present.');
    end
    else if RegKeyExists(HKCU, 'Software\{#AppName}') then
    begin
      CurrentDeleted := RegDeleteKeyIncludingSubkeys(
        HKCU,
        'Software\{#AppName}');
    end;

    LegacyDeleted := True;
    if RegValueExists(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      '{#LegacyAppName}') then
    begin
      Log('Preserving legacy registry ownership because a startup '
        + 'registration from another copy is still present.');
    end
    else if RegKeyExists(HKCU, 'Software\{#LegacyAppName}') then
    begin
      LegacyDeleted := RegDeleteKeyIncludingSubkeys(
        HKCU,
        'Software\{#LegacyAppName}');
    end;

    if (not CurrentDeleted) or (not LegacyDeleted) then
    begin
      Log('Could not completely delete CodexAutoReset registry data.');
      SuppressibleMsgBox(
        CustomMessage('DeleteRegistryDataFailed'),
        mbError,
        MB_OK,
        IDOK);
    end;
    Exit;
  end;

  RegDeleteKeyIfEmpty(
    HKCU,
    'Software\{#AppName}\Startup');
  RegDeleteKeyIfEmpty(
    HKCU,
    'Software\{#AppName}');
  RegDeleteKeyIfEmpty(
    HKCU,
    'Software\{#LegacyAppName}\Startup');
  RegDeleteKeyIfEmpty(
    HKCU,
    'Software\{#LegacyAppName}');
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
    DeleteUserDataOnUninstall := AskToDeleteUserData;
    RemoveOwnedStartupRegistration(
      '{#AppName}',
      'Software\{#AppName}\Startup',
      '{#AppExecutable}');
    RemoveOwnedStartupRegistration(
      '{#LegacyAppName}',
      'Software\{#LegacyAppName}\Startup',
      '{#LegacyAppExecutable}');
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    DeleteUserDataIfRequested;
    RemoveOwnedRegistryKeys;
  end;
end;
