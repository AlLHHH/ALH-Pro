# ALH Pro 一键上传(广告 + 提示) (PowerShell 版, UTF-8 可靠)
# 用法: 双击"一键上传.bat"(或桌面快捷方式)即可,它会调用本脚本。
# 一次把 ad/ 广告目录 和 hint/ 提示目录 一起上传到 GitHub;任一有改动就一起提交+推送。
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

# 统计 ad/ 与 hint/ 两个目录的改动
Write-Step '== 检测改动(广告 ad/ + 提示 hint/) =='
$adStatus   = (git status --porcelain ad)   -join "`n"
$hintStatus = (git status --porcelain hint) -join "`n"
$adHas   = -not [string]::IsNullOrWhiteSpace($adStatus)
$hintHas = -not [string]::IsNullOrWhiteSpace($hintStatus)

Write-Host '广告(ad/)改动:' -ForegroundColor White
if ($adHas) { Write-Host $adStatus -ForegroundColor White } else { Write-Host '  (无)' -ForegroundColor DarkGray }
Write-Host '提示(hint/)改动:' -ForegroundColor White
if ($hintHas) { Write-Host $hintStatus -ForegroundColor White } else { Write-Host '  (无)' -ForegroundColor DarkGray }
Write-Host ''

if (-not $adHas -and -not $hintHas) {
    Write-Host ''
    Write-Host '当前 ad/ 和 hint/ 都没有待上传的改动。' -ForegroundColor Yellow
    Write-Host '请先去 D:\deep\alh-pro\ad 里改广告、或 D:\deep\alh-pro\hint 里改提示,再运行本脚本。'
    Read-Host '按回车退出'; exit 0
}

# 生成日期戳提交消息(说明是广告/提示,便于回溯)
$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'
$parts = New-Object System.Collections.Generic.List[string]
if ($adHas)   { $parts.Add('广告') }
if ($hintHas) { $parts.Add('提示') }
$msg = "更新 $($parts -join ' + ') $stamp"

# 写入 UTF-8(带BOM)临时文件,用 git commit -F 传消息,彻底避免中文乱码
$tmpMsg = Join-Path $env:TEMP 'alh_all_msg.txt'
[System.IO.File]::WriteAllText($tmpMsg, $msg, (New-Object System.Text.UTF8Encoding($true)))

Write-Step "== 提交: $msg =="
# 只 add 这两个目录,不误带其它改动
git add ad/ hint/
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
    Write-Host '  ✅ 上传成功!消息: ' $msg -ForegroundColor Green
    Write-Host '  广告+提示已一起传到 GitHub;用户端最长 10 分钟看到,刚打开的用户立刻看到。' -ForegroundColor Green
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
