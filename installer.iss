; ALH Pro 安装脚本 (Inno Setup 6.3+)
; ⚠ 需要 Inno Setup 6.3 或更高版本(首次版本(2021)起支持 DownloadTemporaryFile / CreateDownloadPage)
;
; 用法:
;   1. 先构建发布版(确保 发布版\ 目录是最新,含软件+引擎,模型可缺省);
;   2. 确认 [Files] 里 发布版\* 没有打包模型(模型 1.38GB 不要进安装包本体);
;   3. 将本文件放入仓库根,用 Inno Setup 编译 → ALHPro_v1.2.0_Setup.exe(约 900MB);
;   4. 模型包(models_v1.0.zip, 1.38GB)单独上传 GitHub Release 附件(与 ModelsUrl 同版本)。
;
; 安装时「选择附加任务」页勾选「下载并安装模型包(来自 GitHub)」:
;   安装完成即从 GitHub 下载模型包并解压到 程序目录\engines\rembg\(扁平结构:6 个 .onnx 直接展开),
;   不勾选 = 之后手动下载模型包,解压到 程序目录\engines\rembg\ 即可。

#define MyAppName "ALH Pro"
#define MyAppVersion "1.3.5"
#define MyAppExeName "ALHPro.exe"
; 【构建时间戳】(ISPP 在编译时求值):用于让用户一眼分辨"同名同版本的不同构建"。
; 起因:同一个 1.3.4 出了多次安装包,名字完全一样、大小只差几十 MB,用户无法确认手上是哪一个。
#define BuildStamp GetDateTimeString('yyyymmdd-hhnn', '', '')
; GitHub Release 模型包直链(与 Release 附件名必须一致;仓库=AlLHHH/ALH-Pro)
; 【为什么指向 v1.3.3 而不是 v1.3.4】models_v1.0.zip 与软件版本无关(内容一直没变),
; 而 v1.3.4 的 Release 尚未建立 → 指向它会让"下载并安装模型包"必然 404。
; 已核实:经 GitHub API 查得 v1.3.3/v1.3.2/…/v1.0 每个 Release 都带 models_v1.0.zip 附件,
; 最新可用 tag 为 v1.3.3(2026-09-08)。
; ⇒ v1.3.4 Release 建好并上传模型附件后,可把本行改回 v1.3.4(不改也能正常工作)。
#define ModelsUrl "https://github.com/AlLHHH/ALH-Pro/releases/download/v1.3.3/models_v1.0.zip"
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
; (覆盖依赖固定 AppId + ignoreversion;AppId 不变,旧版文件/引擎被新版覆盖,不再重装到新目录)
DefaultGroupName=ALH Pro
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
OutputDir=.\
OutputBaseFilename=ALHPro_v{#MyAppVersion}_本体_{#BuildStamp}
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

[Tasks]
Name: "downloadmodels"; Description: "下载并安装模型包(约 1.4GB,来自 GitHub;国内网络易断,建议装完用软件内教程/网盘完整版补装)"; GroupDescription: "模型包:"; Flags: unchecked

[Files]
; 发布版 = 软件 + 引擎(不含模型)。模型不在安装包内,保持体积 ~900MB
; Excludes:排除抠图模型(engines\rembg\*.onnx 1.65GB)、发布版里的解压副本(models_v1.0\)、
; 以及开发残留/调试产物(_ttracks 脚本、旧 exe、pdb/lib/bak、DirectML.Debug)——否则体积膨胀且泄露源码痕迹
Source: "发布版\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "engines\rembg\*.onnx,models_v1.0\*,_ttracks*,bin\*,obj\*,*.pdb,*.lib,*.bak,ALHPro_old*,DirectML.Debug.*,d3dcompiler_47.dll.bak,onnxruntime.lib"

; 【备用 ffmpeg(ffmpeg8)必须随包带上 —— 单独显式列一条,不靠上面那条通配】
; 内置主 ffmpeg 的 NVENC 需要 NVIDIA 驱动 ≥610.00(nvenc API 13.1);驱动较旧的机器(实测 572.83)主 ffmpeg 直接报
;   "Driver does not support the required nvenc API version. Required: 13.1 Found: 13.0",而且会写出 0 字节文件。
; 这类机器【全靠 ffmpeg8 这个备用包】才能用上显卡编码(实测 4K 下 15~19 fps,CPU 软编只有它的几分之一)。
; 少了它,软件会静默退回 CPU 软编 —— 用户只觉得"变慢了",日志里还没有任何线索。
; engines\ 是 gitignore、deploy.ps1 也不同步 engines,最容易漏;显式写一条的收益是:
; **漏拷时 Inno 在编译期就报错(找不到 Source),不会发出一份"悄悄变慢"的包**。
Source: "发布版\engines\ffmpeg8\*"; DestDir: "{app}\engines\ffmpeg8"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; v1.0 升级清理:Real-CUGAN 已从 v1.1.0 起移除(许可不明),旧引擎目录不再需要(约 200MB+),
; 避免升级后留一堆无用文件;其余文件一概不动(设置/记录在 %LOCALAPPDATA%,用户文件不删)。
Type: filesandordirs; Name: "{app}\engines\realcugan"
Type: files; Name: "{app}\d3dcompiler_47.dll"
Type: files; Name: "{app}\D3DCOMPILER_47.dll"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Code]
{==== 下载并解压模型包(勾选「downloadmodels」任务时执行) ====}

function DownloadAndExtractModels(): Boolean;
var
  Page: TDownloadWizardPage;
  ZipPath: String;
  ResultCode: Integer;
  Extracted: Boolean;
begin
  Result := True;
  // 下载前确认(1.4GB 慢/易断,用户知情可跳过 → 安装本身不受影响,避免"卡住停止不了")
  if MsgBox('即将从 GitHub 下载模型包(约 1.4GB,视网络 10~60 分钟,国内直连可能失败)。' + #13#10 + #13#10 +
            '点「是」开始下载(失败或中途取消的话,软件本身已经装好,可稍后用网盘完整版或手动补装);' + #13#10 +
            '点「否」跳过模型(以后想装:软件内「使用教程」有详细步骤)。', mbConfirmation, MB_YESNO) <> IDYES then
  begin
    Result := True;
    Exit;
  end;
  try
    // 进度页
    Page := CreateDownloadPage('下载模型包', '正在从 GitHub 下载模型包(约 1.4GB),请保持网络连接;之后解压约需 3~10 分钟,进度条"不动"是解压中,请耐心等待...', nil);
    try
      Page.Show;
      try
        Page.Clear;
        Page.Add(ExpandConstant('{#ModelsUrl}'), '{#ModelsFile}', '');
        // 第二参数只给文件名:Inno 自动存到 {tmp},带 {tmp}\ 前缀会路径拼错
        Page.Download;
      finally
        Page.Hide;
      end;
    finally
      Page.Free;
    end;

    ZipPath := ExpandConstant('{tmp}\{#ModelsFile}');
    if not FileExists(ZipPath) then
    begin
      MsgBox('模型包下载失败。' + #13#10#13#10 +
        '可能原因:网络不稳定 / GitHub 国内直连慢或被限制。' + #13#10 +
        '建议:1) 用加速器或 GitHub 镜像重试;' + #13#10 +
        '2) 询问社区拿「完整版(含模型)」网盘链接,或下载 models_v1.0.zip 手动解压;' + #13#10 +
        '3) 手动解压到 程序目录\engines\rembg\ 即可(软件内「使用教程」有详细步骤)。', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    // 解压到 {app}\engines\rembg(模型包为扁平结构,6 个 .onnx 直接展开;用系统 tar.exe 解压,无 2GB 限制)
    ForceDirectories(ExpandConstant('{app}\engines\rembg'));
    if not Exec(ExpandConstant('{sys}\tar.exe'),
        '-xf "' + ZipPath + '" -C "' + ExpandConstant('{app}\engines\rembg') + '"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      MsgBox('模型包解压失败/卡住。' + #13#10#13#10 +
        '请手动解压:下载 models_v1.0.zip → 解压到 程序目录\engines\rembg\(' + #13#10 +
        '提示:1.4GB 解压需几分钟,期间进度条看似"卡住"是正常解压中,请耐心等待;' + #13#10 +
        '若 10 分钟无进展,取消后用系统资源管理器解压更快)。', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if ResultCode <> 0 then
    begin
      MsgBox('模型包解压失败(代码 ' + IntToStr(ResultCode) + ')。' + #13#10 +
        '请手动下载 models_v1.0.zip 解压到 程序目录\engines\rembg\。', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    // 删除临时 zip
    DeleteFile(ZipPath);
  except
    MsgBox('模型包下载出错:' + #13#10 + GetExceptionMessage + #13#10#13#10 +
      '建议:使用加速器/镜像,或直接在 GitHub Release 下载 models_v1.0.zip 手动解压。', mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('downloadmodels') then
    DownloadAndExtractModels();
end;

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent
