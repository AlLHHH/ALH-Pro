; ALH Pro 安装脚本 (Inno Setup 6.3+)
; ⚠ 需要 Inno Setup 6.3 或更高版本(首次版本(2021)起支持 DownloadTemporaryFile / CreateDownloadPage)
;
; 用法:
;   1. 先构建发布版(确保 发布版\ 目录是最新,含软件+引擎,模型可缺省);
;   2. 确认 [Files] 里 发布版\* 没有打包模型(模型 1.65GB 不要进安装包本体);
;   3. 将本文件放入仓库根,用 Inno Setup 编译 → ALHPro_v1.0_Setup.exe(约 900MB);
;   4. 模型包(models_v1.0.zip, 1.65GB)单独上传 GitHub Release 附件。
;
; 安装时「选择附加任务」页勾选「下载并安装模型包(来自 GitHub)」:
;   安装完成即从 GitHub 下载模型包并解压到 engines\rembg\models\
;   不勾选 = 之后手动下载模型包,解压到 程序目录\engines\rembg\models\ 即可。

#define MyAppName "ALH Pro"
#define MyAppVersion "1.0"
#define MyAppExeName "ALHPro.exe"
; GitHub Release 模型包直链(发布时替换为真实地址;必须与 Release 附件名一致)
#define ModelsUrl "https://github.com/AlL666/ALHPro/releases/download/v1.0/models_v1.0.zip"
#define ModelsFile "models_v1.0.zip"

[Setup]
AppId={{8F3A2C1E-5D4B-4C6A-9E7F-2A3B4C5D6E7F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=AlL.H
DefaultDirName={autopf}\ALH Pro
DefaultGroupName=ALH Pro
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
OutputDir=.\
OutputBaseFilename=ALHPro_v{#MyAppVersion}_Setup
SetupIconFile=assets\icon.ico
PrivilegesRequired=lowest
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
Source: "发布版\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 注意:发布版\engines\rembg 下的模型文件会被上面的通配符一并打进去!
; 若发布版含模型,请在编译前把模型移出 发布版\engines\rembg\(保留目录结构,只移 .onnx),
; 或改用下面的排除方案:把模型目录先移到 发布版_models\ 单独放。

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
    Page := CreateDownloadPage('下载模型包', '正在从 GitHub 下载模型包(约 1.6GB),请保持网络连接...', nil);
    try
      Page.Show;
      try
        Page.Clear;
        Page.Add(ExpandConstant('{#ModelsUrl}'), ExpandConstant('{tmp}\{#ModelsFile}'), '');
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
      MsgBox('模型包下载失败,请检查网络后重新安装,或稍后手动下载解压。', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    // 解压到 {app}\engines\rembg\models\(用系统 tar.exe 解压 zip,无 2GB 限制)
    ForceDirectories(ExpandConstant('{app}\engines\rembg\models'));
    if not Exec(ExpandConstant('{sys}\tar.exe'),
        '-xf "' + ZipPath + '" -C "' + ExpandConstant('{app}\engines\rembg') + '"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      MsgBox('模型包解压失败(请手动解压 models_v1.0.zip 到程序目录 engines\rembg\models\)。', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if ResultCode <> 0 then
    begin
      MsgBox('模型包解压失败(代码 ' + IntToStr(ResultCode) + ')。', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    // 删除临时 zip
    DeleteFile(ZipPath);
  except
    MsgBox('模型包下载出错:' + #13#10 + GetExceptionMessage, mbError, MB_OK);
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
