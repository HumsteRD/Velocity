; Установщик VELOCITY
;
; Ставим в папку пользователя, а не в Program Files: установка тогда проходит
; вообще без запроса прав администратора. Сама программа запрашивает повышение
; отдельно и только в тот момент, когда пользователь применяет настройки,
; которым права действительно нужны.

#define AppName "VELOCITY"
; Версию можно передать при сборке: ISCC /DAppVersion=0.4.0 velocity.iss
#ifndef AppVersion
  #define AppVersion "0.4.0"
#endif
#define AppPublisher "VELOCITY"
#define AppExeName "Velocity.exe"

[Setup]
AppId={{8F3A5C21-7E4D-4B96-9A18-2C6D5E0F7B43}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Оптимизация компьютера для игр

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
AllowNoIcons=yes

; lowest = установка без UAC, в профиль пользователя.
; Пользователь при желании может выбрать установку для всех через диалог.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

OutputDir=..\release
OutputBaseFilename=velocity-setup-{#AppVersion}
SetupIconFile=..\src\Velocity.App\velocity.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Windows 10 версии 2004 — минимум, ниже нет части используемых API.
MinVersion=10.0.19041

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist-app\Velocity.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Журнал сбоев и кэш удаляем, а точки отката — НЕТ.
; Пользователь может удалить программу и потом захотеть вернуть настройки назад.
Type: files; Name: "{localappdata}\Velocity\crash.log"

[Messages]
russian.WelcomeLabel2=Будет установлена программа [name/ver].%n%nVELOCITY находит настройки Windows, драйвера и игр, которые снижают производительность, и умеет их исправлять. Все изменения обратимы: перед каждым применением создаётся точка отката.%n%nПрограмма не внедряется в игры и не устанавливает драйверы в ядро системы.

[Code]
// Не даём установить поверх запущенной программы: файл будет занят,
// и установка оборвётся на середине с невнятной ошибкой.
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;

  if CheckForMutexes('VelocityAppRunning') then
  begin
    if MsgBox('VELOCITY сейчас запущена. Закрыть её и продолжить установку?',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      Exec('taskkill.exe', '/F /IM Velocity.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(1200);
    end
    else
      Result := False;
  end;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;

  if CheckForMutexes('VelocityAppRunning') then
  begin
    if MsgBox('VELOCITY сейчас запущена. Закрыть её и продолжить удаление?',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      Exec('taskkill.exe', '/F /IM Velocity.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(1200);
    end
    else
      Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if DirExists(ExpandConstant('{localappdata}\Velocity\snapshots')) then
      MsgBox('Точки отката остались в папке:' + #13#10 +
             ExpandConstant('{localappdata}\Velocity\snapshots') + #13#10 + #13#10 +
             'Они не удалены намеренно — через них можно вернуть системные настройки, ' +
             'даже если программа больше не установлена.',
             mbInformation, MB_OK);
  end;
end;
