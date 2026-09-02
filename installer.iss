; ALH Pro 安装脚本 (Inno Setup 6.3+)
; ⚠ 需要 Inno Setup 6.3 或更高版本(首次版本(2021)起支持 DownloadTemporaryFile / CreateDownloadPage)
;
; 用法:
;   1. 先构建发布版(确保 发布版\ 目录是最新,含软件+引擎,模型可缺省);
;   2. 确认 [Files] 里 发布版\* 没有打包模型(模型 1.38GB 不要进安装包本体);
;   3. 将本文件放入仓库根,用 Inno Setup 编译 → ALHPro_v1.1.0_Setup.exe(约 1.2GB);
;   4. 模型包(models_v1.0.zip, 1.38GB)单独上传 GitHub Release 附件(与 ModelsUrl 同版本)。
;
; 安装时「选择附加任务」页勾选「下载并安装模型包(来自 GitHub)」:
;   安装完成即从 GitHub 下载模型包并解压到 程序目录\engines\rembg\(扁平结构:6 个 .onnx 直接展开),
;   不勾选 = 之后手动下载模型包,解压到 程序目录\engines\rembg\ 即可。

#define MyAppName "ALH Pro"
#define MyAppVersion "1.1.1"
#define MyAppExeName "ALHPro.exe"
; GitHub Release 模型包直链(与 Release 附件名必须一致;仓库=AlLHHH/ALH-Pro)
#define ModelsUrl "https://github.com/AlLHHH/ALH-Pro/releases/download/v1.1.0/models_v1.0.zip"
#define ModelsFile "models_v1.0.zip"

[Setup]
AppId={{8F3A2C1E-5D4B-4C6A-9E7F-2A3B4C5D6E7F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
; 窗口标题/卸载名:默认"AppName 版本 AppVersion",改为简洁的 "ALH Pro v1.1.0"
AppVerName=ALH Pro v{#MyAppVersion}
AppPublisher=AlL.H
; 默认安装到【用户程序目录】(C:\Users\用户名\AppData\Local\Programs\ALH Pro):
; 理由:①PrivilegesRequired=lowest(不请求管理员权限)却装到 Program Files 会写不进去/失败——
;         普通用户的 Program Files 是只读的(实测坑);②用户目录天然可写,模型包/缓存/引擎都无忧;
;         ③卸载/升级无需管理员。若用户主动选择其它目录(如 Program Files)则按需请求权限。
DefaultDirName={userpf}\ALH Pro
DefaultGroupName=ALH Pro
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
OutputDir=.\
OutputBaseFilename=ALHPro_v{#MyAppVersion}_Setup
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
Name: "downloadmodels"; Description: "下载并安装模型包(约 1.6GB,来自 GitHub)"; GroupDescription: "模型包:"; Flags: unchecked

[Files]
; 发布版 = 软件 + 引擎(不含模型)。模型不在安装包内,保持体积 ~900MB
; Excludes:排除抠图模型(engines\rembg\*.onnx 1.65GB)与发布版里的解压副本(models_v1.0\),
; 否则安装包 ~4.2GB 超过 GitHub Release 单文件 2GB 上限
Source: "发布版\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "engines\rembg\*.onnx,models_v1.0\*"

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
  try
    // 进度页
    Page := CreateDownloadPage('下载模型包', '正在从 GitHub 下载模型包(约 1.6GB),请保持网络连接;之后解压约需几分钟,进度条"不动"是解压中,请耐心等待...', nil);
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
        '建议:1) 使用加速器或 GitHub 镜像(如 gh.ddlc.top / hf-mirror)后重试;' + #13#10 +
        '2) 直接在 GitHub Release 页面下载 models_v1.0.zip(浏览器或下载工具更稳);' + #13#10 +
        '3) 解压到 程序目录\engines\rembg\ 即可(软件自动识别)。', mbError, MB_OK);
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
        '提示:1.6GB 解压需几分钟,期间进度条看似"卡住"是正常解压中,请耐心等待;' + #13#10 +
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
