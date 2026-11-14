# 发版.ps1 — 一条命令走完一次发版的硬性闸门(2026-11-14)
#
# 【为什么要有它】此前发版靠"记得做哪几步",结果三次出现"用户反馈的问题其实早就修好了":
#   · 修完只留在工作区没提交 → 打包打的是旧代码;
#   · 版本号改了、公告没改 → 用户看不到改了什么;
#   · tag 没打/指错提交 → 事后没法证明这个安装包是哪份源码;
#   · 官网待上传目录忘了生成 → 站点一直挂着上一版。
# 这里把"缺了就不许发"的条件写成闸门,失败就停下并只说一句人话:缺什么、怎么补。
#
# 用法:
#   pwsh -NoProfile -File .\发版.ps1 -Version 1.4.9            # 全流程
#   pwsh -NoProfile -File .\发版.ps1 -Version 1.4.9 -DryRun    # 只查闸门,不动任何东西
#   pwsh -NoProfile -File .\发版.ps1 -Version 1.4.9 -SkipInstaller -SkipUpload   # 只部署+校验
#
# 闸门(任何一条不过就退出,不构建、不上传):
#   1. 工作区干净(不允许"改了没提交"就打包)
#   2. 版本号四处一致(csproj + 三个 iss)
#   3. 公告四载体都写了这一版(RELEASE_NOTES / release_history / 发布公告_vX.md / 站点 changelog)
#   4. 签名 tag 存在且指向本次提交(没有就建)
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$DryRun,
    [switch]$SkipInstaller,
    [switch]$SkipUpload
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

function Fail([string]$msg) { Write-Host "✗ $msg" -ForegroundColor Red; exit 1 }
function Ok([string]$msg) { Write-Host "✓ $msg" -ForegroundColor Green }
function Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# ---------- 闸门 1:工作区干净 ----------
Step '闸门 1/4:工作区干净'
$dirty = @(git status --porcelain)
if ($dirty.Count -gt 0) {
    $dirty | Select-Object -First 10 | ForEach-Object { Write-Host "   $_" }
    Fail "还有 $($dirty.Count) 个改动没提交。先提交,否则打出来的包和源码对不上。"
}
Ok '工作区干净'

# ---------- 闸门 2:版本号四处一致 ----------
Step '闸门 2/4:版本号一致'
$csproj = Join-Path $root 'ImgUpscalerUI\ImgUpscalerUI.csproj'
$m = Select-String -Path $csproj -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
if (-not $m) { Fail "csproj 里找不到 <Version>" }
$csVer = $m.Matches[0].Groups[1].Value
if ($csVer -ne $Version) { Fail "csproj 是 $csVer,不是你传的 $Version。先改版本号再发。" }
foreach ($iss in 'installer.iss', 'installer_full.iss', 'installer_nocut.iss') {
    $p = Join-Path $root $iss
    $im = Select-String -Path $p -Pattern '#define MyAppVersion "([^"]+)"' | Select-Object -First 1
    if (-not $im) { Fail "$iss 里找不到 MyAppVersion" }
    if ($im.Matches[0].Groups[1].Value -ne $Version) { Fail "$iss 是 $($im.Matches[0].Groups[1].Value),和 csproj 的 $Version 不一致。" }
}
Ok "四处都是 $Version"

# ---------- 闸门 3:公告四载体 ----------
Step '闸门 3/4:公告四载体'
$notes = Get-Content (Join-Path $root 'RELEASE_NOTES.md') -Raw
if ($notes -notmatch "(?m)^##\s*v$([regex]::Escape($Version))\b") { Fail "RELEASE_NOTES.md 里没有 「## v$Version」这一节(软件内更新说明会缺这一版)。" }

$hist = Get-Content (Join-Path $root 'release_history.json') -Raw | ConvertFrom-Json
$top = @($hist)[0]
if ("$($top.v)" -ne $Version) { Fail "release_history.json 的第一条是 v$($top.v),不是 v$Version(首页更新提示读它)。" }

$announce = Join-Path $root "发布公告_v$Version.md"
if (-not (Test-Path $announce)) { Fail "缺少 $announce(GitHub Release 正文用它)。" }

$changelog = Join-Path $root 'website\changelog.html'
$anchor = 'v' + ($Version -replace '\.', '')
if (-not (Test-Path $changelog)) { Fail "找不到 website\changelog.html" }
if ((Get-Content $changelog -Raw) -notmatch "id=`"$anchor`"") { Fail "website\changelog.html 里没有 id=`"$anchor`" 这一节(官网更新日志会缺这一版)。" }
Ok '四处公告都在'

# ---------- 闸门 4:签名 tag ----------
Step '闸门 4/4:签名 tag'
$head = (git rev-parse HEAD).Trim()
$tagRef = "refs/tags/v$Version"
$tagCommit = (git rev-parse -q --verify "$tagRef^{commit}" 2>$null)
if (-not $tagCommit) {
    if ($DryRun) { Ok "tag v$Version 还不存在(正式发版时会建签名 tag)" }
    else {
        git tag -s "v$Version" -m "ALH Pro v$Version" | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail "建签名 tag 失败(GPG 没配好?)。" }
        Ok "已建签名 tag v$Version"
    }
}
elseif ($tagCommit.Trim() -ne $head) {
    Fail "tag v$Version 指向 $($tagCommit.Trim().Substring(0,7)),本次提交是 $($head.Substring(0,7))。要么改版本号,要么确认后手动移动 tag。"
}
else { Ok "tag v$Version 指向本次提交" }

if ($DryRun) {
    Step 'DryRun:以上闸门全过。正式发版会依次执行:'
    Write-Host '   1) deploy.ps1            编译 + 同步发布版 + 启动验证'
    Write-Host '   2) 打包.ps1              打标准版安装包'
    Write-Host '   3) 发布到GitHub.ps1      建/更新 Release 并上传安装包与模型包'
    Write-Host '   4) 生成官网待上传目录      8 个页面 + sitemap + style.css'
    exit 0
}

# ---------- 执行 ----------
Step '1/4 部署(deploy.ps1)'
& (Join-Path $root 'deploy.ps1')
if (-not $?) { Fail "deploy.ps1 失败,后面都不用做。" }
Ok '部署完成'

if (-not $SkipInstaller) {
    Step '2/4 打标准版安装包(打包.ps1)'
    & (Join-Path $root '打包.ps1')
    if (-not $?) { Fail "打包.ps1 失败。" }
    Ok '安装包已生成'
}

if (-not $SkipUpload) {
    Step '3/4 发布到 GitHub(发布到GitHub.ps1)'
    # 模型包 1.25 GB 不在免费版仓库里(清仓库时删掉了),只留在专业版目录;两处都找一遍。
    $packs = @((Join-Path $root '打包工具\models_v1.1.zip'), 'D:\deep\ALH Pro Max\打包工具\models_v1.1.zip')
    $pack = $packs | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $pack) { Fail "找不到 models_v1.1.zip(找过:$($packs -join ' / '))。" }
    & (Join-Path $root '发布到GitHub.ps1') -Version $Version `
        -Installer (Join-Path $root "ALHPro_v${Version}_Setup.exe") `
        -WithModelPack -ModelPack $pack `
        -NotesFile (Join-Path $root "发布公告_v$Version.md")
    if (-not $?) { Fail "上传 GitHub 失败(安装包或模型包没传上去)。" }
    Ok 'Release 已更新'
}

Step '4/4 生成官网待上传目录'
$site = Join-Path $root 'website'
$stage = Join-Path ([Environment]::GetFolderPath('Desktop')) ("官网上传_v${Version}_" + (Get-Date -Format 'yyyyMMdd'))
New-Item -ItemType Directory -Force -Path $stage | Out-Null
foreach ($f in 'index.html', 'download.html', 'pro.html', 'github.html', 'changelog.html', 'faq.html', 'privacy.html', 'licenses.html', 'sitemap.xml', 'robots.txt') {
    $src = Join-Path $site $f
    if (Test-Path $src) { Copy-Item $src $stage -Force }
}
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'assets\css') | Out-Null
Copy-Item (Join-Path $site 'assets\css\style.css') (Join-Path $stage 'assets\css\style.css') -Force
$newest = Get-ChildItem 'website\*.html', 'website\sitemap.xml', 'website\assets\css\style.css' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
@(
    "本次要传:$Version 的站点文件(共 $(@(Get-ChildItem $stage -File -Recurse).Count) 个)",
    "最新改动时间:$($newest.LastWriteTime.ToString('yyyy-MM-dd HH:mm'))",
    '',
    '自查:',
    '  1) 下载页版本号与安装包大小/哈希 = 本次实际产物',
    '  2) changelog 最上面一节是 v' + $Version,
    '  3) index.html 里的 style.css?v= 与 assets/css/style.css 一起传',
    '  4) 传完逐条打开确认 200'
) | Set-Content (Join-Path $stage '上传说明.txt') -Encoding UTF8
Ok "官网待上传目录:$stage"

Step "发版完成:v$Version"