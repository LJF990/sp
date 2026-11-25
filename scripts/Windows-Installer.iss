#define AppName "安全视频播放器"
#define AppExe "Player.exe"

[Setup]
AppId={{16F9E173-1D06-4A2C-B6EB-5E8A4A35D0B4}
AppName={#AppName}
AppVersion=1.0.0
DefaultDirName={localappdata}\Programs\SecureVideoPlayer
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=SecureVideo-Setup-x64
SetupIconFile=..\Player\Assets\app-icon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}

[Files]
Source: "..\artifacts\windows-installer-payload\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "其他选项："

[Run]
Filename: "{app}\{#AppExe}"; Description: "启动{#AppName}"; Flags: nowait postinstall skipifsilent
