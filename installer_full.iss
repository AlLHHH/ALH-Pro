; ALH Pro 安装脚本 (Inno Setup 6.3+)
; ⚠ 需要 Inno Setup 6.3 或更高版本(首次版本(2021)起支持 DownloadTemporaryFile / CreateDownloadPage)
;
; 用法:
;   1. 先构建发布版(确保 发布版\ 目录是最新,含软件+引擎,模型可缺省);
;   2. 确认 [Files] 里 发布版\* 没有打包模型(模型 1.38GB 不要进安装包本体);
;   3. 将本文件放入仓库根,用 Inno Setup 编译 → ALHPro_v1.2.0_Full_Setup.exe(约 900MB);
;   4. 模型包(models_v1.0.zip, 1.38GB)单独上传 GitHub Release 附件(与 ModelsUrl 同版本)。
;
; 安装时「选择附加任务」页勾选「下载并安装模型包(来自 GitHub)」:
;   安装完成即从 GitHub 下载模型包并解压到 程序目录\engines\rembg\(扁平结构:6 个 .onnx 直接展开),
;   不勾选 = 之后手动下载模型包,解压到 程序目录\engines\rembg\ 即可。

#define MyAppName "ALH Pro"
#define MyAppVersion "1.2.0"
#define MyAppExeName "ALHPro.exe"
; GitHub Release 模型包直链(与 Release 附件名必须一致;仓库=AlLHHH/ALH-Pro)
#define ModelsUrl "https://github.com/AlLHHH/ALH-Pro/releases/download/v1.2.0/models_v1.0.zip"
#define ModelsFile "models_v1.0.zip"
; 完整版(含模型,网盘/整包)说明:安装完成后可到软件内「使用教程」或 GitHub 说明页找完整版直链

[Setup]
AppId={{8F3A2C1E-5D4B-4C6A-9E7F-2A3B4C5D6E7F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
; 窗口标题/卸载名:默认"AppName 版本 AppVersion",改为简洁的 "ALH Pro v{#MyAppVersion}"
AppVerName=ALH Pro v{#MyAppVersion}
AppPublisher=AlL.H
; 最低系统:Win10 1809(与 TargetPlatformMinVersion 一致);比这更旧的装完必崩,直接拦下
MinVersion=10.0.17763
; 默认安装到【用户程序目录】(C:\Users\用户名\AppData\Local\Programs\ALH Pro):
; 理由:①PrivilegesRequired=lowest(不请求管理员权限)却装到 Program Files 会写不进去/失败——
;         普通用户的 Program Files 是只读的(实测坑);②用户目录天然可写,模型包/缓存/引擎都无忧;
;         ③卸载/升级无需管理员。若用户主动选择其它目录(如 Program Files)则按需请求权限。
DefaultDirName={userpf}\ALH Pro
; 记住上次安装位置(默认值本就是 yes,这里显式声明):固定 AppId 时再次安装会自动用上次目录,
; 无需用户重新选择——配合覆盖安装,升级/重装更顺畅
UsePreviousAppDir=yes
; 允许覆盖安装/升级:固定 AppId 识别为同一应用;文件用 ignoreversion 无条件覆盖旧版本;
; 已有安装时 Inno 自动复用该 AppId 指向的目录并执行升级(同 AppId 即覆盖)
DefaultGroupName=ALH Pro
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
OutputDir=.\
OutputBaseFilename=ALHPro_v{#MyAppVersion}_Full_Setup
SetupIconFile=assets\icon.ico
; 不用管理员权限(普通用户直接装;默认用户目录,无需提权)——配合 {userpf} 无权限冲突
PrivilegesRequired=lowest
; 允许用户在安装向导里选择「仅当前用户 / 所有用户(需管理员)」:想装到 Program Files 的用户可自行切换(会提示输入管理员密码)
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
; 显示更友好的对话(下载进度页面)
ShowLanguageDialog=no
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"


[Files]
; 发布版 = 软件 + 引擎(不含模型)。模型不在安装包内,保持体积 ~900MB
; Excludes:排除抠图模型(engines\rembg\*.onnx 1.65GB)、发布版里的解压副本(models_v1.0\)、
; 以及开发残留/调试产物(_ttracks 脚本、旧 exe、pdb/lib/bak、DirectML.Debug)——否则体积膨胀且泄露源码痕迹
Source: "发布版\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "models_v1.0\*,_ttracks*,bin\*,obj\*,*.pdb,*.lib,*.bak,ALHPro_old*,DirectML.Debug.*,d3dcompiler_47.dll.bak,onnxruntime.lib"

[InstallDelete]
; v1.0 升级清理:Real-CUGAN 已从 v1.1.0 起移除(许可不明),旧引擎目录不再需要(约 200MB+),
; 避免升级后留一堆无用文件;其余文件一概不动(设置/记录在 %LOCALAPPDATA%,用户文件不删)。
Type: filesandordirs; Name: "{app}\engines\realcugan"
Type: files; Name: "{app}\d3dcompiler_47.dll"
Type: files; Name: "{app}\D3DCOMPILER_47.dll"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent
