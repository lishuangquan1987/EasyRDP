; ============================================================================
; EasyRDP Server - Windows XP compatible installer
;
; This script MUST be compiled with Inno Setup 5.6.1 (Unicode), which is the
; LAST Inno Setup release that supports Windows 2000 / XP / Server 2003.
; Inno Setup 6.x installers refuse to run on XP, so the regular server.iss
; (MinVersion=6.1, modern wizard) can never be installed on an XP machine.
;
; Compile:
;   ISCC.exe /DMyAppVersion=1.2.3 ^
;           /DSourceDir=<absolute path to stage\server> ^
;           /DOutputDir=<absolute path to dist> ^
;           installer\server-xp.iss
;
; Output: EasyRDP-Server-<version>-XP-Setup.exe
; Target: Windows XP SP3 or later (also works on Win7 / Win10 / Win11)
; Runtime requirement: Microsoft .NET Framework 4.0 (Full or Client Profile)
;
; NOTE: keep this file ASCII/English only. Inno Setup 5's Unicode compiler
; does not reliably read UTF-8 scripts without a BOM, and non-ASCII literals
; in [Code] would be mojibake on a Chinese XP machine.
;
; IS 5 compatibility notes (do NOT add IS 6-only directives here):
;   - no WizardStyle (IS 6.0+)
;   - no x86compatible / x64compatible architecture identifiers (IS 6.0+)
;   - no skipifsourcedoesntexist flag (IS 6.1+)
;   - Compression=lzma/max instead of lzma2/max
;   - MinVersion uses the simple "major.minor" form
;   - PrivilegesRequired=lowest is fine: it was added in IS 5.3.7
;
; The [Code] section below only warns when .NET Framework 4.0 is missing.
; XP never shipped with it, and an install that cannot start is a bad first
; impression. "Yes" is the default button so silent installs are never blocked.
; ============================================================================

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
; Same AppId / install dir as installer\server.iss, so the modern (IS 6) and
; the XP (IS 5) installers are drop-in replacements for each other.
AppId={{1F7A0C25-5B93-4C88-B0D7-3A61E9C4F872}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
; Per-user install: no admin rights needed, and both Logs/ and the aly
; updater need write access to the program folder. On XP {localappdata}
; resolves to "<profile>\Local Settings\Application Data".
DefaultDirName={localappdata}\EasyRDP\Server
DefaultGroupName=EasyRDP
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=EasyRDP-Server-{#MyAppVersion}-XP-Setup
Compression=lzma/max
SolidCompression=yes
; 5.1 = Windows XP. .NET Framework 4.0 itself requires XP SP3, so there is
; no point in forcing a build number / service pack suffix here.
MinVersion=5.1
; Do not restrict ArchitecturesAllowed: the net40 AnyCPU payload ships both
; x86 and x64 openh264 DLLs and picks one at runtime, so a single 32-bit
; installer works on both 32-bit and 64-bit Windows.
UninstallDisplayIcon={app}\ApplicationFolder\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Start EasyRDP Server with Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
; No "Excludes" parameter here: this script targets IS 5.6.1. The workflow
; strips *.pdb / *.xml from the staging folder before compiling any installer.
Source: "{#SourceDir}\ApplicationFolder\*"; DestDir: "{app}\ApplicationFolder"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\UpdateFolder\*"; DestDir: "{app}\UpdateFolder"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\ApplicationFolder\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\ApplicationFolder\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Optional auto-start (HKCU\Run - same key used by the in-app AutoStart option)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "EasyRDP Server"; ValueData: """{app}\ApplicationFolder\{#MyAppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\ApplicationFolder\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function IsDotNet40Installed(): Boolean;
var
  Installed: Cardinal;
begin
  Result := False;
  if RegQueryDWordValue(HKEY_LOCAL_MACHINE,
       'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Install', Installed) then
  begin
    Result := Installed = 1;
  end
  else if RegQueryDWordValue(HKEY_LOCAL_MACHINE,
       'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Client', 'Install', Installed) then
  begin
    Result := Installed = 1;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsDotNet40Installed then
  begin
    Result := MsgBox(
      'Microsoft .NET Framework 4.0 was not detected on this computer.' + #13#10 + #13#10 +
      'EasyRDP Server needs .NET Framework 4.0 to run on Windows XP. ' +
      'Please install it from Microsoft before starting the server.' + #13#10 + #13#10 +
      'Continue installing anyway?',
      mbConfirmation, MB_YESNO) = IDYES;
  end;
end;

; Uninstalling keeps %APPDATA%\EasyRDP\server (settings) and the audit logs,
; so reinstalling or switching installer variants never loses data.
