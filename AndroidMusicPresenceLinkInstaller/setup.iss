#define MyAppExeName "AndroidMusicPresenceLink.exe"
#define BinDir "..\Crimson AndroidMusicPresence\bin\Release\net10.0-windows10.0.19041.0"
#define MyAppVersion GetVersionNumbersString(SourcePath + "\" + BinDir + "\" + MyAppExeName)
#define MyAppName "AndroidMusicPresenceLink"
#define MyAppPublisher "Snail"

[Setup]
AppId={{e5fefbf4-6207-4d9c-9e5e-6db2c2eaa8a1}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputBaseFilename=AndroidMusicPresenceLink_Setup
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon";   Description: "Create a &desktop shortcut";    GroupDescription: "Additional shortcuts:"
Name: "startmenuicon"; Description: "Create a &Start Menu shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; Main app
Source: "{#BinDir}\AndroidMusicPresenceLink.exe";                DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\AndroidMusicPresenceLink.dll";                DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\AndroidMusicPresenceLink.deps.json";          DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\AndroidMusicPresenceLink.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\WinRT.Runtime.dll";                           DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\Microsoft.Windows.SDK.NET.dll";               DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\QRCoder.dll";                                 DestDir: "{app}"; Flags: ignoreversion

; Tray icons
Source: "{#BinDir}\Tray_Icons\Tray_USB.ico";          DestDir: "{app}\Tray_Icons"; Flags: ignoreversion
Source: "{#BinDir}\Tray_Icons\Tray_TCP.ico";          DestDir: "{app}\Tray_Icons"; Flags: ignoreversion
Source: "{#BinDir}\Tray_Icons\Tray_WD.ico";           DestDir: "{app}\Tray_Icons"; Flags: ignoreversion
Source: "{#BinDir}\Tray_Icons\Tray_NoConnection.ico"; DestDir: "{app}\Tray_Icons"; Flags: ignoreversion
Source: "{#BinDir}\Tray_Icons\Tray_Scrcpy_USB.ico";   DestDir: "{app}\Tray_Icons"; Flags: ignoreversion
Source: "{#BinDir}\Tray_Icons\Tray_Scrcpy_TCP.ico";   DestDir: "{app}\Tray_Icons"; Flags: ignoreversion
Source: "{#BinDir}\Tray_Icons\Tray_Scrcpy_WD.ico";    DestDir: "{app}\Tray_Icons"; Flags: ignoreversion

; Assets -> %AppData%\Snail\Assets
Source: "{#BinDir}\Assets\adb.exe";                  DestDir: "{userappdata}\Snail\Assets"; Flags: ignoreversion
Source: "{#BinDir}\Assets\AdbWinApi.dll";            DestDir: "{userappdata}\Snail\Assets"; Flags: ignoreversion
Source: "{#BinDir}\Assets\AdbWinUsbApi.dll";         DestDir: "{userappdata}\Snail\Assets"; Flags: ignoreversion
Source: "{#BinDir}\Assets\avcodec-62.dll";           DestDir: "{userappdata}\Snail\Assets"; Flags: ignoreversion
Source: "{#BinDir}\Assets\avformat-62.dll";          DestDir: "{userappdata}\Snail\Assets"; Flags: ignoreversion
Source: "{#BinDir}\Assets\avutil-60.dll";            DestDir: "{userappdata}\Snail\Assets"; Flags: ignoreversion
Source: "{#BinDir}\Assets\disconnected.png";         DestDir: "{userappdata}\Snail\Assets"; Flags: ignoreversion
Source: "{#BinDir}\Assets\ffmpeg.exe";               DestDir: "{userappdata}\Snail\Assets"; Flags: ignoreversion
Source: "{#BinDir}\Assets\libusb-1.0.dll";           DestDir: "{userappdata}\Snail\Assets"; Flags: ignoreversion
Source: "{#BinDir}\Assets\AMPLLOGO.png";             DestDir: "{userappdata}\Snail\Assets"; Flags: ignoreversion
Source: "{#BinDir}\Assets\scrcpy-noconsole.vbs";     DestDir: "{userappdata}\Snail\Assets"; Flags: ignoreversion
Source: "{#BinDir}\Assets\scrcpy-server";            DestDir: "{userappdata}\Snail\Assets"; Flags: ignoreversion
Source: "{#BinDir}\Assets\scrcpy.exe";               DestDir: "{userappdata}\Snail\Assets"; Flags: ignoreversion
Source: "{#BinDir}\Assets\SDL3.dll";                 DestDir: "{userappdata}\Snail\Assets"; Flags: ignoreversion
Source: "{#BinDir}\Assets\swresample-6.dll";         DestDir: "{userappdata}\Snail\Assets"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}";         Filename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[InstallDelete]
; The resource folder was renamed from Resources to Assets; remove the old one wholesale on upgrade.
Type: filesandordirs; Name: "{userappdata}\Snail\Resources"

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\Snail\AndroidMusicPresenceLink"
Type: filesandordirs; Name: "{userappdata}\Snail\Assets"
Type: dirifempty;     Name: "{userappdata}\Snail"

[Registry]
Root: HKCU; Subkey: "Software\AndroidMusicPresenceLink"; ValueType: dword; ValueName: "Installed"; ValueData: "1"; Flags: uninsdeletekey

[Code]
procedure CleanLegacyWixKeys();
begin
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\AndroidMusicPresenceLink\Resources');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    CleanLegacyWixKeys();
end;
