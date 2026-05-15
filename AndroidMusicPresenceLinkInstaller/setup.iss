#define MyAppExeName "AndroidMusicPresenceLink.exe"
#define BinDir "..\Crimson AndroidMusicPresence\bin\Release\net8.0-windows10.0.19041.0"
#define MyAppVersion GetVersionNumbersString(SourcePath + "\" + BinDir + "\" + MyAppExeName)
#define MyAppName "AndroidMusicPresenceLink"
#define MyAppPublisher "Snail"

[Setup]
AppId={{e5fefbf4-6207-4d9c-9e5e-6db2c2eaa8a1}
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

; Resources -> %AppData%\Snail\Resources
Source: "{#BinDir}\Resources\adb.exe";                  DestDir: "{userappdata}\Snail\Resources"; Flags: ignoreversion
Source: "{#BinDir}\Resources\AdbWinApi.dll";            DestDir: "{userappdata}\Snail\Resources"; Flags: ignoreversion
Source: "{#BinDir}\Resources\AdbWinUsbApi.dll";         DestDir: "{userappdata}\Snail\Resources"; Flags: ignoreversion
Source: "{#BinDir}\Resources\avcodec-62.dll";           DestDir: "{userappdata}\Snail\Resources"; Flags: ignoreversion
Source: "{#BinDir}\Resources\avformat-62.dll";          DestDir: "{userappdata}\Snail\Resources"; Flags: ignoreversion
Source: "{#BinDir}\Resources\avutil-60.dll";            DestDir: "{userappdata}\Snail\Resources"; Flags: ignoreversion
Source: "{#BinDir}\Resources\disconnected.png";         DestDir: "{userappdata}\Snail\Resources"; Flags: ignoreversion
Source: "{#BinDir}\Resources\ffmpeg.exe";               DestDir: "{userappdata}\Snail\Resources"; Flags: ignoreversion
Source: "{#BinDir}\Resources\libusb-1.0.dll";           DestDir: "{userappdata}\Snail\Resources"; Flags: ignoreversion
Source: "{#BinDir}\Resources\Musiclogo.png";            DestDir: "{userappdata}\Snail\Resources"; Flags: ignoreversion
Source: "{#BinDir}\Resources\open_a_terminal_here.bat"; DestDir: "{userappdata}\Snail\Resources"; Flags: ignoreversion
Source: "{#BinDir}\Resources\scrcpy-noconsole.vbs";     DestDir: "{userappdata}\Snail\Resources"; Flags: ignoreversion
Source: "{#BinDir}\Resources\scrcpy-server";            DestDir: "{userappdata}\Snail\Resources"; Flags: ignoreversion
Source: "{#BinDir}\Resources\scrcpy.exe";               DestDir: "{userappdata}\Snail\Resources"; Flags: ignoreversion
Source: "{#BinDir}\Resources\SDL3.dll";                 DestDir: "{userappdata}\Snail\Resources"; Flags: ignoreversion
Source: "{#BinDir}\Resources\swresample-6.dll";         DestDir: "{userappdata}\Snail\Resources"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}";         Filename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[InstallDelete]
; Remove stale resource files left behind by older versions
Type: files; Name: "{userappdata}\Snail\Resources\avcodec-61.dll"
Type: files; Name: "{userappdata}\Snail\Resources\avformat-61.dll"
Type: files; Name: "{userappdata}\Snail\Resources\avutil-59.dll"
Type: files; Name: "{userappdata}\Snail\Resources\swresample-5.dll"
Type: files; Name: "{userappdata}\Snail\Resources\SDL2.dll"
Type: files; Name: "{userappdata}\Snail\Resources\scrcpy-console.bat"
Type: files; Name: "{userappdata}\Snail\Resources\icon.png"

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\Snail\AndroidMusicPresenceLink"
Type: filesandordirs; Name: "{userappdata}\Snail\Resources"
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
