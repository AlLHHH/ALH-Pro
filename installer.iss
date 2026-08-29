; ALH Pro 安装脚本 (Inno Setup)
; 用法:把本文件放到仓库根,先构建发布版(发布版\ 目录),再在此目录运行 Inno Setup 编译。
#define MyAppName "ALH Pro"
#define MyAppVersion "1.0"
#define MyAppExeName "ALHPro.exe"

[Setup]
AppId={{8F3A2C1E-5D4B-4C6A-9E7F-2A3B4C5D6E7F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=AlL.H
DefaultDirName={autopf}\ALH Pro
DefaultGroupName=ALH Pro
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=zip
SolidCompression=no
; 输出与图标路径相对本脚本所在目录(仓库根),不再写死 D:\
OutputDir=.\
OutputBaseFilename=ALHPro_v{#MyAppVersion}_Setup
SetupIconFile=assets\icon.ico
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern

[Files]
; 发布版目录相对仓库根;若不在请先部署发布版
Source: "发布版\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 ALH Pro"; Flags: nowait postinstall skipifsilent
