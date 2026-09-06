# ALH Pro 右侧提示一键上传 (PowerShell 版, UTF-8 可靠)
# 用法: 双击"一键上传提示.bat"即可,它会调用本脚本。
# 上传的是 hint/ 目录(右侧纯文本提示),与 ad/ 广告完全独立。
$ErrorActionPreference = 'Stop'

function Write-Step($msg) { Write-Host $msg -ForegroundColor Cyan }

$proj = 'D:\deep\alh-pro'
if (-not (Test-Path (Join-Path $proj '.git'))) {
    Write-Host '[错误] 找不到项目目录: ' $proj -ForegroundColor Red
    Write-Host '       请确认本脚本放在 D:\deep\alh-pro 内。'
    Read-Host '按回车退出'; exit 1
}
Set-Location $proj

# 让 git 把提交消息按 UTF-8 存,避免中文乱码
git config i18n.commitEncoding utf-8 | Out-Null

Write-Step '== 检测 hint 目录改动 =='
$status = (git status --porcelain hint) -join "`n"
if ([string]::IsNullOrWhiteSpace($status)) {
    Write-Host ''
    Write-Host '当前 hint 目录【没有待上传的改动】。' -ForegroundColor Yellow
    Write-Host '请先去 D:\deep\alh-pro\hint 里修改要上传的提示文案/颜色,再运行本脚本。'
    Read-Host '按回车退出'; exit 0
}
Write-Host $status -ForegroundColor White
Write-Host ''

# 生成日期戳提交消息
$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'
$msg   = "更新提示 $stamp"

# 写入 UTF-8(带BOM)临时文件,用 git commit -F 传消息,彻底避免中文乱码
$tmpMsg = Join-Path $env:TEMP 'alh_tip_msg.txt'
[System.IO.File]::WriteAllText($tmpMsg, $msg, (New-Object System.Text.UTF8Encoding($true)))

Write-Step "== 提交: $msg =="
git add hint/
if ($LASTEXITCODE -ne 0) { Write-Host '[错误] git add 失败' -ForegroundColor Red; Read-Host '按回车退出'; exit 1 }
git commit -F $tmpMsg | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host '[提示] 没有可提交的内容(可能文件内容没变化)。' -ForegroundColor Yellow
    Read-Host '按回车退出'; exit 0
}

Write-Step '== 推送到 GitHub =='
git push origin main
$pushOk = ($LASTEXITCODE -eq 0)

if ($pushOk) {
    Write-Host ''
    Write-Host '==========================================' -ForegroundColor Green
    Write-Host '  ✅ 提示上传成功!消息: ' $msg -ForegroundColor Green
    Write-Host '  用户端最长 10 分钟看到新提示;刚打开软件的用户会立刻看到。' -ForegroundColor Green
    Write-Host '==========================================' -ForegroundColor Green
} else {
    Write-Host ''
    Write-Host '==========================================' -ForegroundColor Red
    Write-Host '  ⚠ 上传失败(见上方报错)。请检查:' -ForegroundColor Red
    Write-Host '   1) 电脑能否上网?'
    Write-Host '   2) GitHub 是否登录/授权?可手动运行: git push' -ForegroundColor Red
    Write-Host '      若弹出登录窗口,登录后重试。'
    Write-Host '   3) 是否与远程冲突?可运行: git pull 后再试。' -ForegroundColor Red
    Write-Host '   把上面的文字发给作者即可快速解决。' -ForegroundColor Red
    Write-Host '==========================================' -ForegroundColor Red
}
Read-Host '按回车退出'
