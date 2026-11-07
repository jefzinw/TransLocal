; Instalador do TransLocal (Inno Setup 6)
; Gerado por build.ps1:  ISCC.exe /DAppVersion=1.0.0 installer\TransLocal.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "TransLocal"
#define AppExe "TransLocal.exe"
#define SourceExe "..\dist\win-x64\TransLocal.exe"

[Setup]
AppId={{8C3B2E57-7A0F-4F6E-9E58-2D4B1C1A7F31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=TransLocal
AppComments=Transmissão de tela entre computadores por cabo ou placa de captura HDMI
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Instalação para todos os usuários (permite criar a regra do firewall).
; Pode-se escolher instalar só para o usuário atual, sem administrador.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=TransLocal-Setup-{#AppVersion}
SetupIconFile=..\assets\TransLocal.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; O TransLocal fica na área de notificação; é encerrado em PrepareToInstall.
CloseApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "autostart"; Description: "Iniciar o TransLocal junto com o Windows (fica pronto para receber transmissões)"; GroupDescription: "Opções:"
Name: "gamepad"; Description: "Instalar suporte a controles de jogo (driver ViGEmBus) — necessário só no computador que transmite e será controlado"; GroupDescription: "Opções:"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Transmitir ou receber a tela de outro computador"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Ligações diretas por cabo são classificadas como rede pública: a regra vale para todos os perfis.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden; Check: IsAdminInstallMode
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=any description=""Permite receber transmissões de tela do TransLocal"""; Flags: runhidden; Check: IsAdminInstallMode; StatusMsg: "Liberando o TransLocal no Firewall do Windows..."
; O winget roda como o usuário e pede, ele mesmo, a permissão de administrador para o driver.
Filename: "{cmd}"; Parameters: "/c winget install --id ViGEm.ViGEmBus -e --silent --accept-package-agreements --accept-source-agreements"; Flags: runasoriginaluser runhidden waituntilterminated; Tasks: gamepad; Check: not WizardSilent; StatusMsg: "Instalando o suporte a controles de jogo (ViGEmBus)..."
Filename: "{app}\{#AppExe}"; Parameters: "--set-autostart on"; Flags: runasoriginaluser runhidden waituntilterminated; Tasks: autostart
Filename: "{app}\{#AppExe}"; Parameters: "--set-autostart off"; Flags: runasoriginaluser runhidden waituntilterminated; Tasks: not autostart
Filename: "{app}\{#AppExe}"; Description: "Abrir o TransLocal agora"; Flags: nowait postinstall skipifsilent runasoriginaluser
; Atualização automática (instalador silencioso com /RELAUNCH=1): reabre o TransLocal ao terminar.
; Serviço do TransLocal (se o usuário ativou): volta a rodar com os arquivos novos.
Filename: "{sys}\sc.exe"; Parameters: "start TransLocalService"; Flags: runhidden; Check: ServiceExists
Filename: "{app}\{#AppExe}"; Parameters: "--updated --minimized"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop TransLocalService"; Flags: runhidden; RunOnceId: "PararServico"
Filename: "{sys}\sc.exe"; Parameters: "delete TransLocalService"; Flags: runhidden; RunOnceId: "RemoverServico"
Filename: "{app}\{#AppExe}"; Parameters: "--cleanup"; Flags: runhidden; RunOnceId: "RemoverInicio"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden; RunOnceId: "RemoverFirewall"
; Tarefa do controle de programas de administrador (se o usuário ativou). O auxiliar é fechado junto com o app.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""TransLocal Controle Elevado"" /F"; Flags: runhidden; RunOnceId: "RemoverControleElevado"

[Code]
function SupportsQuit(const FileName: String): Boolean;
var
  MS, LS: Cardinal;
begin
  { O comando --quit existe a partir da versão 1.2. }
  Result := False;
  if GetVersionNumbers(FileName, MS, LS) then
    Result := MS >= ((1 shl 16) or 2);
end;

function ServiceExists(): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\TransLocalService');
end;

procedure CloseRunningApp();
var
  ResultCode: Integer;
  Exe: String;
begin
  { O serviço mantém o agente aberto: para antes de fechar os processos e trocar os arquivos. }
  if ServiceExists() then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop TransLocalService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2000);
  end;
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) and SupportsQuit(Exe) then
  begin
    { Pede para fechar normalmente (remove o ícone da bandeja, encerra transmissões). }
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2000);
  end;
  { Garante o fechamento apenas da cópia instalada nesta pasta; outras cópias continuam abertas. }
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command "Get-Process TransLocal -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -eq ''' + Exe + ''' } | Stop-Process -Force"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseRunningApp();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then CloseRunningApp();
end;

function ShouldRelaunch(): Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;
