# ALH Pro 打包脚本(在仓库根目录运行)
# 用法:powershell -NoProfile -ExecutionPolicy Bypass -File .\打包.ps1 [-SkipCheck]
#   (pwsh 7 也能跑;两条路都实测过。)
# ⚠ 【本文件必须带 UTF-8 BOM(EF BB BF)—— 这是硬要求,不是洁癖】2026-09-12 实测:
#   本文件一旦丢了 BOM,Windows PowerShell 5.1 会按系统 ANSI(GBK)去读,满屏中文变成乱码
#   (`涓荤▼搴?` 那种),继而在中文串里报 "The string is missing the terminator" 之类的解析错误,
#   看起来像脚本被写坏了 —— 其实只是编码声明没了。修法(重写成 UTF-8 **带 BOM**):
#     $utf8 = New-Object System.Text.UTF8Encoding($true)
#     [System.IO.File]::WriteAllText($p, [System.IO.File]::ReadAllText($p, (New-Object System.Text.UTF8Encoding($false))), $utf8)
#   注意:某些编辑器/补丁工具保存时会**静默去掉 BOM** —— 改完本文件请复检前 3 字节。
#   (历史上"5.1 触发 AMSI 崩溃 AmsiScanBuffer"的现象也出现在 BOM 丢失的长中文脚本上;
#    带 BOM 的版本今天用 5.1 跑完整流程、含 ISCC 编译 245 秒,全程正常。)
#
# 为什么要有这个脚本(而不是每次手敲 ISCC):
#   1. 先校验「必须随包带上」的引擎是否都在 发布版\ 里 —— 缺了直接停下,不发一份「悄悄变慢」的包。
#      典型事故:备用 ffmpeg(engines\ffmpeg8)漏拷 → 驱动较旧的机器上显卡编码静默消失、只能 CPU 软编,
#      用户只觉得「变慢了」,日志里毫无线索。
#   2. 备用 ffmpeg 若仓库里有、发布版缺 → 自动补过去(它是硬编的唯一兜底)。
#   3. 调 Inno Setup 出包,并打印产物名 / 大小 / SHA256(交付时要报前缀)。
param([switch]$SkipCheck)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

function Say($m) { Write-Host $m }
function Fail($m) { Write-Host "❌ $m" -ForegroundColor Red; exit 1 }

$pub = Join-Path $root '发布版'
if (!(Test-Path $pub)) { Fail '找不到 发布版\ 目录 —— 先跑 deploy.ps1' }

# ===== ① 备用 ffmpeg:仓库有、发布版缺 → 自动补 =====
$repoFf8 = Join-Path $root 'engines\ffmpeg8'
$pubFf8 = Join-Path $pub 'engines\ffmpeg8'
if ((Test-Path $repoFf8) -and !(Test-Path (Join-Path $pubFf8 'ffmpeg.exe'))) {
    Say '→ 备用 ffmpeg(ffmpeg8)在发布版里缺失,正在从 engines\ffmpeg8 补过去...'
    New-Item -ItemType Directory -Force $pubFf8 | Out-Null
    Copy-Item (Join-Path $repoFf8 '*') $pubFf8 -Recurse -Force
}

# ===== ①b RIFE 2026 重编引擎:同理,仓库有、发布版缺 → 自动补 =====
# engines\ 是 gitignore、deploy.ps1 也不同步 engines,这份二进制只存在于本机仓库里;
# 漏了它的后果是"程序静默回退到 2022 年那份旧 ncnn 引擎"(RifePath 的兜底),用户只觉得"补帧有时候出问题",
# 日志里毫无线索 —— 正是本清单要拦的那类事故。
$repoRifeNew = Join-Path $root 'engines\rife\rife-ncnn-vulkan-2026.exe'
$pubRifeNew = Join-Path $pub 'engines\rife\rife-ncnn-vulkan-2026.exe'
if ((Test-Path $repoRifeNew) -and !(Test-Path $pubRifeNew)) {
    Say '→ RIFE 2026 重编引擎在发布版里缺失,正在从 engines\rife 补过去...'
    New-Item -ItemType Directory -Force (Split-Path $pubRifeNew) | Out-Null
    Copy-Item $repoRifeNew $pubRifeNew -Force
}

# ===== ② 必带清单校验 =====
$required = @(
    @{ p = 'ALHPro.exe'; why = '主程序' },
    @{ p = 'ALHPro.dll'; why = '主程序' },
    @{ p = 'RELEASE_NOTES.md'; why = '更新公告(更新弹窗读它)' },
    @{ p = 'engines\ffmpeg\ffmpeg.exe'; why = '主 ffmpeg(拆帧/合帧/音频)' },
    @{ p = 'engines\ffmpeg\ffprobe.exe'; why = '探测视频信息' },
    @{ p = 'engines\ffmpeg8\ffmpeg.exe'; why = '备用 ffmpeg:NVIDIA 驱动<610 时显卡编码的唯一兜底' },
    @{ p = 'engines\realesrgan\models\realesr-animevideov3-x2.param'; why = 'Real-ESRGAN 模型' },
    @{ p = 'engines\waifu2x'; why = 'waifu2x 引擎目录' },
    @{ p = 'engines\rife'; why = 'RIFE 补帧引擎目录' },
    @{ p = 'engines\rife\rife-ncnn-vulkan-2026.exe'; why = 'RIFE 2026 重编引擎(程序优先用它;缺了就静默回退 2022 旧引擎)' }
)
$missing = @()
foreach ($r in $required) {
    $full = Join-Path $pub $r.p
    if (Test-Path $full) {
        Say ('  OK  {0,-52} {1}' -f $r.p, $r.why)
    }
    else {
        Write-Host ('  ✗   {0,-52} {1}' -f $r.p, $r.why) -ForegroundColor Red
        $missing += $r.p
    }
}
if ($missing.Count -gt 0 -and !$SkipCheck) {
    Fail ("发布版里缺 {0} 个必带文件(上面标 ✗ 的)。补齐后再打包,或用 -SkipCheck 强行继续。" -f $missing.Count)
}

# ===== ③ 找 ISCC =====
$iscc = $null
foreach ($c in @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe')) {
    if (Test-Path $c) { $iscc = $c; break }
}
if (!$iscc) { $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue; if ($cmd) { $iscc = $cmd.Source } }
if (!$iscc) { Fail '找不到 ISCC.exe(Inno Setup 6)。请安装 Inno Setup 6 或把 ISCC.exe 放进 PATH。' }
Say "→ 使用编译器: $iscc"

# ===== ④ 编译安装包 =====
Say '→ 正在编译 installer.iss(约 3~6 分钟)...'
& $iscc 'installer.iss' | ForEach-Object {
    if ($_ -match 'Successful compile|Error|error |Warning') { Say "   $_" }
}
if ($LASTEXITCODE -ne 0) { Fail "ISCC 编译失败(退出码 $LASTEXITCODE)" }

# ===== ⑤ 产物报告 =====
$latest = Get-ChildItem $root -Filter 'ALHPro_v*_本体_*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (!$latest) { Fail '编译成功但没找到产物 ALHPro_v*_本体_*.exe' }
$hash = (Get-FileHash $latest.FullName -Algorithm SHA256).Hash
Say ''
Say '✅ 打包完成'
Say ('   文件: {0}' -f $latest.Name)
Say ('   大小: {0:N1} MB' -f ($latest.Length / 1MB))
Say ('   SHA256: {0}' -f $hash)
Say ('   前缀:   {0}' -f $hash.Substring(0, 16))
Say ''
Say '提示:交付/发版前请把包名与 SHA256 前缀一起报出来;备用 ffmpeg 已随包(清单校验通过)。'
