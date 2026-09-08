; ALH Pro 安装脚本 — 精简版(不含抠图模型,保留超分模型) (Inno Setup 6.3+)
; ⚠ 需要 Inno Setup 6.3 或更高版本
;
; 用途:打包「不含抠图模型」的本体安装包(用不到 AI 抠图的用户,体积可省 ~1.7GB)。
; 保留 RealESRGAN_x4plus.onnx(在 rembg 目录,但它是超分用)——只排除抠图那 6 个。
; 安装时「选择附加任务」若勾选「下载并安装模型包」仍会补全抠图模型(可跳过)。

#define MyAppName "ALH Pro"
#define MyAppVersion "1.3.3"
#define MyAppExeName "ALHPro.exe"
; GitHub Release 模型包直链(与 Release 附件名必须一致;仓库=AlLHHH/ALH-Pro)
#define ModelsUrl "https://github.com/AlLHHH/ALH-Pro/releases/download/v1.3.3/models_v1.0.zip"
#define ModelsFile "models_v1.0.zip"
; 完整版(含模型,网盘/整包)说明:安装完成后可到软件内「使用教程」或 GitHub 说明页找完整版直链

[Setup]
AppId={{8F3A2C1E-5D4B-4C6A-9E7F-2A3B4C5D6E7F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName=ALH Pro v{#MyAppVersion}
AppPublisher=AlL.H
MinVersion=10.0.17763
DefaultDirName={userpf}\ALH Pro
UsePreviousAppDir=yes
DefaultGroupName=ALH Pro
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
OutputDir=.\
OutputBaseFilename=ALHPro_v{#MyAppVersion}_Setup_NoCut
SetupIconFile=assets\icon.ico
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
ShowLanguageDialog=no
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "downloadmodels"; Description: "下载并安装抠图模型包(约 1.4GB,来自 GitHub;仅需 AI 抠图才勾选)"; GroupDescription: "模型包:"; Flags: unchecked

[Files]
; 精简版 = 软件 + 引擎 + 超分模型(RealESRGAN_x4plus) + 其它模型(waifu2x/rife/ffmpeg/demucs/lavasr)。
; 只排除【抠图那 6 个 onnx】(birefnet/isnet/u2net 系列),保留超分用的 RealESRGAN_x4plus.onnx。
; Excludes:排除抠图 6 个模型、发布版里的解压副本(models_v1.0\)、开发残留/调试产物。
Source: "发布版\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "engines\rembg\birefnet-lite.onnx,engines\rembg\birefnet.onnx,engines\rembg\isnet-anime.onnx,engines\rembg\isnet-general-use.onnx,engines\rembg\u2net.onnx,engines\rembg\u2netp.onnx,models_v1.0\*,_ttracks*,bin\*,obj\*,*.pdb,*.lib,*.bak,ALHPro_old*,DirectML.Debug.*,d3dcompiler_47.dll.bak,onnxruntime.lib"

[InstallDelete]
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
  if MsgBox('即将从 GitHub 下载抠图模型包(约 1.4GB,视网络 10~60 分钟,国内直连可能失败)。' + #13#10 + #13#10 +
            '点「是」开始下载(失败或中途取消的话,软件本身已经装好,可稍后用网盘完整版或手动补装);' + #13#10 +
            '点「否」跳过模型(以后想装:软件内「使用教程」有详细步骤)。', mbConfirmation, MB_YESNO) <> IDYES then
  begin
    Result := True;
    Exit;
  end;
  try
    Page := CreateDownloadPage('下载抠图模型包', '正在从 GitHub 下载抠图模型包(约 1.4GB),请保持网络连接;之后解压约需 3~10 分钟,进度条"不动"是解压中,请耐心等待...', nil);
    try
      Page.Show;
      try
        Page.Clear;
        Page.Add(ExpandConstant('{#ModelsUrl}'), '{#ModelsFile}', '');
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
      MsgBox('抠图模型包下载失败。' + #13#10#13#10 +
        '可能原因:网络不稳定 / GitHub 国内直连慢或被限制。' + #13#10 +
        '建议:1) 用加速器或 GitHub 镜像重试;' + #13#10 +
        '2) 下载 models_v1.0.zip 手动解压到 程序目录\engines\rembg\;' + #13#10 +
        '3) 仅需超分/补帧可先不装(不影响其它功能)。', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    ForceDirectories(ExpandConstant('{app}\engines\rembg'));
    if not Exec(ExpandConstant('{sys}\tar.exe'),
        '-xf "' + ZipPath + '" -C "' + ExpandConstant('{app}\engines\rembg') + '"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      MsgBox('抠图模型包解压失败/卡住。' + #13#10#13#10 +
        '请手动解压:下载 models_v1.0.zip → 解压到 程序目录\engines\rembg\(提示:解压需几分钟,进度条看似"卡住"是正常解压中)。', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if ResultCode <> 0 then
    begin
      MsgBox('抠图模型包解压失败(代码 ' + IntToStr(ResultCode) + ')。' + #13#10 +
        '请手动下载 models_v1.0.zip 解压到 程序目录\engines\rembg\。', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    DeleteFile(ZipPath);
  except
    MsgBox('抠图模型包下载出错:' + #13#10 + GetExceptionMessage + #13#10#13#10 +
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
