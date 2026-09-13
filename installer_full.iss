; ALH Pro 安装脚本 (Inno Setup 6.3+)
; ⚠ 需要 Inno Setup 6.3 或更高版本(首次版本(2021)起支持 DownloadTemporaryFile / CreateDownloadPage)
;
; 【本脚本 = 完整版(内置模型)】与 installer.iss(本体版)的区别只有一处:
;   installer.iss 的 [Files] Excludes 里排除了 engines\rembg\*.onnx(约 1.65GB)→ 装完必须另下模型包;
;   本脚本**不排除**这些模型 → 抠图/去背景装完即用,代价是安装包大 1.4GB(实测 v1.3.5:2285.3MB vs 826MB)。
; 用法:
;   1. 先构建发布版(deploy.ps1),并确认 发布版\engines\rembg\ 里确实有那 6 个 rembg .onnx
;      —— 缺了它们、本脚本编译出来的就和本体版没区别(体积会露馅:掉回 ~826MB);
;   2. 用 Inno Setup 编译本文件 → ALHPro_v{版本}_完整版_{构建时间戳}.exe;
;   3. 完整版体积**超过 GitHub Release 单文件 2GiB 上限**(实测 2285MB > 2147483648 字节),
;      只能网盘/直传分发,不要试图传 Release 附件(会失败)。
; 说明:本脚本没有 [Tasks]/[Code] 段,因此**不提供**"安装时从 GitHub 下载模型包"的任务 ——
;   那套逻辑只存在于 installer.iss;下面的 ModelsUrl/ModelsFile 是本脚本的历史遗留定义,当前未被引用。

#define MyAppName "ALH Pro"
#define MyAppVersion "1.3.6"
#define MyAppExeName "ALHPro.exe"
; 【构建时间戳】(ISPP 在编译时求值):用于让用户一眼分辨"同名同版本的不同构建"。
; 起因:同一个 1.3.4 出了多次安装包,名字完全一样、大小只差几十 MB,用户无法确认手上是哪一个。
#define BuildStamp GetDateTimeString('yyyymmdd-hhnn', '', '')
; GitHub Release 模型包直链(与 Release 附件名必须一致;仓库=AlLHHH/ALH-Pro)
; 【历史遗留,本脚本当前未引用】这两个定义只在 installer.iss 的 DownloadAndExtractModels() 里用到;
; 完整版不下载模型,留着只为与 installer.iss 对照。本体版那边的维护规则同样适用:
;   ModelsUrl **必须指向一个真实存在、且确实挂了该附件的 Release**,否则"下载并安装模型包"必然 404。
; v1.3.5 的 Release 已建好并上传 models_v1.0.zip 附件(2026-09-12),故两边都指向 v1.3.5。
#define ModelsUrl "https://github.com/AlLHHH/ALH-Pro/releases/download/v1.3.6/models_v1.0.zip"
#define ModelsFile "models_v1.0.zip"
; 完整版(含模型,网盘/整包)说明:安装完成后可到软件内「使用教程」或 GitHub 说明页找完整版直链

[Setup]
AppId={{8F3A2C1E-5D4B-4C6A-9E7F-2A3B4C5D6E7F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
; 窗口标题/卸载名:默认"AppName 版本 AppVersion",改为简洁的 "ALH Pro v{#MyAppVersion}"
AppVerName=ALH Pro v{#MyAppVersion}
AppPublisher=AlL.H
; 【安装包自身的版本信息 —— 此前缺失导致的真问题】原先只设了 AppVersion/AppVerName,没设
; VersionInfoVersion → 资源管理器里"文件版本"是空的、显示成 0.0.0.0,叠加"名字一样",
; 用户根本无法分辨装的是哪一次构建(同一版本多次出包时尤其致命)。
; 现在:文件版本=1.3.4,文件说明带构建时间戳 → 悬停安装包即可看到"构建 20260911-0003"。
VersionInfoVersion={#MyAppVersion}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoCompany=AlL.H
VersionInfoDescription={#MyAppName} 安装程序 v{#MyAppVersion} · 构建 {#BuildStamp}
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
OutputBaseFilename=ALHPro_v{#MyAppVersion}_完整版_{#BuildStamp}
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
; 完整版 = 软件 + 引擎 + **抠图模型**(engines\rembg\*.onnx 约 1.65GB 一并打包,装完即用)
; Excludes:排除发布版里的解压副本(models_v1.0\)、开发残留/调试产物
;   (_ttracks 脚本、旧 exe、pdb/lib/bak、DirectML.Debug)——否则体积膨胀且泄露源码痕迹
; ⚠ 与 installer.iss 的差异就在这一点:那边 Excludes 里多一条 engines\rembg\*.onnx(所以本体版 ~826MB),
;   本脚本**故意不排除** —— 这就是"完整版"三个字的全部含义(实测 v1.3.5:2285.3MB)。改这行之前想清楚。
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
