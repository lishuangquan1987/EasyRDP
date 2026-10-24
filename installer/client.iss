; EasyRDP Client 安装包（Windows 10/11，x64）
; 构建：ISCC.exe /DMyAppVersion=1.2.3 /DSourceDir=<stage/client> /DOutputDir=<dist> installer/client.iss

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\stage\client"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define MyAppName "EasyRDP Client"
#define MyAppExeName "EasyRDP.Client.Wpf.exe"
#define MyAppPublisher "EasyRDP"
#define MyAppURL "https://github.com/lishuangquan1987/EasyRDP"

[Setup]
; 每个产品独立 AppId，卸载信息互不干扰
AppId={{6D2C1B7A-8E44-4F6B-9A31-7C5E0B9D2A61}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName={localappdata}\EasyRDP\Client
DefaultGroupName=EasyRDP
DisableProgramGroupPage=yes
; 安装到用户目录（无需管理员）：程序与 aly 更新器都需要写权限
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=EasyRDP-Client-{#MyAppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=6.1
UninstallDisplayIcon={app}\ApplicationFolder\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; ApplicationFolder：主程序与依赖；UpdateFolder：aly 自动更新器（可选）
Source: "{#SourceDir}\ApplicationFolder\*"; DestDir: "{app}\ApplicationFolder"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\UpdateFolder\*"; DestDir: "{app}\UpdateFolder"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\ApplicationFolder\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\ApplicationFolder\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\ApplicationFolder\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

; 卸载不删除用户配置（%APPDATA%\EasyRDP\client），便于重装后保留连接与分组
