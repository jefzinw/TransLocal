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
Filename: "{app}\{#AppExe}"; Parameters: "--set-autostart on"; Flags: runasoriginaluser runhidden waituntilterminated; Tasks: autostart
Filename: "{app}\{#AppExe}"; Parameters: "--set-autostart off"; Flags: runasoriginaluser runhidden waituntilterminated; Tasks: not autostart
Filename: "{app}\{#AppExe}"; Description: "Abrir o TransLocal agora"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "EncerrarApp"
Filename: "{app}\{#AppExe}"; Parameters: "--cleanup"; Flags: runhidden; RunOnceId: "RemoverInicio"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden; RunOnceId: "RemoverFirewall"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  { Encerra uma versão em execução para permitir a atualização do executável. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
