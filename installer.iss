; ALH Pro 安装脚本 (Inno Setup)
#define MyAppName "ALH Pro"
#define MyAppVersion "0.3"
#define MyAppExeName "ALHPro.exe"

[Setup]
AppId={{8F3A2C1E-5D4B-4C6A-9E7F-ALHPRO03}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=AlL.H
DefaultDirName={autopf}\ALH Pro
DefaultGroupName=ALH Pro
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=zip
SolidCompression=no
OutputDir=D:\deep\alh-pro
OutputBaseFilename=ALHPro_v0.3_Setup
SetupIconFile=D:\deep\alh-pro\assets\icon.ico
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern

[Files]
Source: "D:\deep\alh-pro\发布版\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 ALH Pro"; Flags: nowait postinstall skipifsilent