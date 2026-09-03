# ALH Pro 发布版部署脚本(唯一正确姿势,请勿手动 robocopy Debug 输出!)
# 用法(在仓库根目录):powershell -NoProfile -ExecutionPolicy Bypass -File deploy.ps1
# 作用:
#   1. dotnet publish -c Release -p:Platform=x64  → bin\Release\...\win-x64\publish
#      (win-x64.pubxml 里 SelfContained=true = 独立打包,自带 .NET 运行时)
#   2. 同步到 发布版\ (排除 engines:引擎/模型已有单独维护,不覆盖)
#   3. 启动验证(检查窗口标题 = ALH Pro 版本号)
# ⚠ 千万不要从 bin\Debug\... 同步:Debug 是非独立版,缺 .NET 运行时,
#   拷进 发布版 后双击就报 "You must install or update .NET"。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pub = Join-Path $root 'ImgUpscalerUI\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish'
$dst = Join-Path $root '发布版'

Write-Host "==> 1/3 发布(Release 独立版)..."
Push-Location (Join-Path $root 'ImgUpscalerUI')
try { dotnet publish ImgUpscalerUI.csproj -c Release -p:Platform=x64 -v q --nologo | Out-String | Write-Host } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败(exit $LASTEXITCODE)——已中止,未同步(请检查编译错误)" }
if (-not (Test-Path (Join-Path $pub 'coreclr.dll'))) { throw "发布输出缺少 coreclr.dll(非独立版)——已中止,未同步" }
if (-not (Test-Path (Join-Path $pub 'hostfxr.dll'))) { throw "发布输出缺少 hostfxr.dll(非独立版)——已中止,未同步" }

Write-Host "==> 2/3 同步到 发布版\(排除 engines)..."
# 先结束正在运行的 ALHPro(否则 ALHPro.exe 被锁,robocopy 无限重试等待→卡死)
Get-Process ALHPro -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
robocopy $pub $dst /E /XD engines /XF DirectML.Debug.dll DirectML.Debug.pdb DirectML.pdb ALHPro.pdb onnxruntime.lib "*.obj" /NFL /NDL /NJH /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy 失败(exit $LASTEXITCODE)" }
# 清理历史残留(开发脚本/旧 exe/备份),确保发布目录干净
$junk = @('_ttracks.cs','_ttracks.csproj','ALHPro_old_20260901_2127.exe','d3dcompiler_47.dll.bak')
foreach ($j in $junk) { $p = Join-Path $dst $j; if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue } }
foreach ($d in @('bin','obj')) { $p = Join-Path $dst $d; if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue } }

Write-Host "==> 3/3 启动验证..."
$p = Start-Process (Join-Path $dst 'ALHPro.exe') -PassThru
Start-Sleep -Seconds 10
$pr = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
if ($pr -and $pr.MainWindowTitle -like 'ALH Pro*') {
    Write-Host "✅ 启动正常,窗口标题: $($pr.MainWindowTitle)"
} elseif ($pr) {
    throw "进程活着但窗口标题异常:'$($pr.MainWindowTitle)'——可能弹了错误对话框,请检查"
} else {
    throw "进程未存活——启动失败"
}
# 收尾:无论哪种情况,清掉本次验证启动的实例(按名杀,防残留锁文件)
Get-Process ALHPro -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "完成。发布版已可双击运行。"
