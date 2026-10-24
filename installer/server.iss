; EasyRDP Server 安装包（Windows 7+；Windows XP 请使用 portable zip）
; 构建：ISCC.exe /DMyAppVersion=1.2.3 /DSourceDir=<stage/server> /DOutputDir=<dist> installer/server.iss

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\stage\server"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define MyAppName "EasyRDP Server"
#define MyAppExeName "EasyRDP.Server.Wpf.exe"
#define MyAppPublisher "EasyRDP"
#define MyAppURL "https://github.com/lishuangquan1987/EasyRDP"

[Setup]
AppId={{1F7A0C25-5B93-4C88-B0D7-3A61E9C4F872}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
; 装到用户目录：程序日志（Logs/）与 aly 更新器都需要写权限
DefaultDirName={localappdata}\EasyRDP\Server
DefaultGroupName=EasyRDP
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=EasyRDP-Server-{#MyAppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x86compatible
MinVersion=6.1
UninstallDisplayIcon={app}\ApplicationFolder\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Start EasyRDP Server with Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\ApplicationFolder\*"; DestDir: "{app}\ApplicationFolder"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\UpdateFolder\*"; DestDir: "{app}\UpdateFolder"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\ApplicationFolder\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\ApplicationFolder\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 可选的开机自启（HKCU\Run，与服务端界面的 AutoStart 使用同一位置）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "EasyRDP Server"; ValueData: """{app}\ApplicationFolder\{#MyAppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\ApplicationFolder\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

; 卸载不删除配置（%APPDATA%\EasyRDP\server）与审计日志，便于重装后继续使用
