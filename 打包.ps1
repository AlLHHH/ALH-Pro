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

# ===== ①c 补帧模型 rife-v4.26:仓库有、发布版缺 → 自动补 =====
# 同一类事故:v4.26 是 2026-09-24 上架的下拉第 3 项,权重只在仓库里(engines\ 是 gitignore)。
# 缺了它引擎会抛「缺少补帧模型:rife-v4.26」——比静默更响,但用户是"选了第 3 项才炸",仍是发布事故。
$repoV426 = Join-Path $root 'engines\rife\rife-v4.26'
$pubV426 = Join-Path $pub 'engines\rife\rife-v4.26'
if ((Test-Path $repoV426) -and !(Test-Path (Join-Path $pubV426 'flownet.bin'))) {
    Say '→ 补帧模型 rife-v4.26 在发布版里缺失,正在从 engines\rife 补过去...'
    New-Item -ItemType Directory -Force $pubV426 | Out-Null
    Copy-Item (Join-Path $repoV426 '*') $pubV426 -Recurse -Force
}

# ===== ①d Real-CUGAN 引擎:仓库有、发布版缺 → 自动补 =====
# 【2026-09-24】Real-CUGAN 重新随包(重编引擎 + models-se 权重)。engines\ 是 gitignore、
# deploy.ps1 也不同步 engines ⇒ 漏了它用户会"文件在、引擎没装",而且 50 系上那份官方 20220728 exe
# 会出坏帧(所以必须带 2026 重编版,不能靠下载兜底)。
$repoRc = Join-Path $root 'engines\realcugan'
$pubRc = Join-Path $pub 'engines\realcugan'
if ((Test-Path $repoRc) -and !(Test-Path (Join-Path $pubRc 'realcugan-ncnn-vulkan-2026.exe'))) {
    Say '→ Real-CUGAN 引擎在发布版里缺失,正在从 engines\realcugan 补过去...'
    New-Item -ItemType Directory -Force $pubRc | Out-Null
    Copy-Item (Join-Path $repoRc '*') $pubRc -Recurse -Force
}

# ===== ①e 许可原文:仓库 licenses\ 有、发布版\licenses 缺 → 自动补 =====
# 【2026-09-24】新增的逐项目许可原文(Real-CUGAN / realcugan-ncnn-vulkan / Practical-RIFE)。
# 这些是"随包分发就要带上"的署名文件,不能只在仓库里有 —— 发布版\licenses 才是真正打进安装包的那份。
$repoLic = Join-Path $root 'licenses'
$pubLic = Join-Path $pub 'licenses'
if (Test-Path $repoLic) {
    New-Item -ItemType Directory -Force $pubLic | Out-Null
    $copied = 0
    Get-ChildItem $repoLic -File -Recurse | ForEach-Object {
        $dest = Join-Path $pubLic $_.Name
        # 【F2 修复 · 2026-09-24】原先是"缺了才补"—— 于是仓库里改过的许可原文永远不会同步到发布版
        # (两份靠人工记得,迟早漂移)。现在**内容比对**:同名即比 SHA256,不一致就覆盖并提示。
        if (!(Test-Path $dest)) { Copy-Item $_.FullName $dest -Force; $copied++ }
        elseif ((Get-FileHash $_.FullName -Algorithm SHA256).Hash -ne (Get-FileHash $dest -Algorithm SHA256).Hash) {
            Copy-Item $_.FullName $dest -Force
            Say "→ 许可原文:$($_.Name) 发布版那份与仓库不一致,已用仓库版覆盖"
            $copied++
        }
    }
    if ($copied -gt 0) { Say "→ 许可原文:同步了 $copied 个文件到 发布版\licenses" }
}

# ===== ①e' 根 LICENSE(MIT)也必须随包 =====
# 【2026-09-24 补】MIT 明文要求"随副本附带版权声明与许可原文";而根 LICENSE 不在 licenses\ 目录里,
# 上面的遍历覆盖不到 ⇒ 用户装完拿不到本软件自己的许可原文(评审 F2 的同一条线)。同样按内容(SHA256)比对。
$rootLicense = Join-Path $root 'LICENSE'
$pubLicense  = Join-Path $pubLic 'ALH-Pro-MIT-LICENSE.txt'
if (!(Test-Path $rootLicense)) { Fail '找不到仓库根 LICENSE —— 它是本软件自己的 MIT 许可原文,必须随包分发' }
$licNeedCopy = $true
if (Test-Path $pubLicense) {
    $licNeedCopy = (Get-FileHash $rootLicense -Algorithm SHA256).Hash -ne (Get-FileHash $pubLicense -Algorithm SHA256).Hash
}
if ($licNeedCopy) {
    Copy-Item $rootLicense $pubLicense -Force
    Say '→ 许可原文:根 LICENSE → 发布版\licenses\ALH-Pro-MIT-LICENSE.txt(缺失或与仓库不一致,已同步)'
}

# ===== ①f 第三方声明(THIRD_PARTY_NOTICES.txt):唯一源 → 仓库根 + 发布版 两份,加关键条目校验 =====
# 【F2 修复 · 2026-09-24】修前的事实:仓库里有两份 notice(仓库根那份 / ImgUpscalerUI 那份=发布版那份),
# 内容已经漂移过,而且两份里各有一处路径笔误(`licenses\:`、`发布版\licenses\`)没人发现。
# 现在收敛成**一个唯一源** + 脚本强制同步 + 关键条目缺失就停下:
#   · 唯一源 = ImgUpscalerUI\THIRD_PARTY_NOTICES.txt(csproj 会把它拷进输出目录);
#   · 仓库根与 发布版\ 两份都从它生成;不一致就覆盖并提示;
#   · 关键条目(ffmpeg / Real-CUGAN 两份许可原文 / rife 许可原文 / 权重来源文件 / 第 16 条)缺任何一条 ⇒ Fail。
$noticeSrc  = Join-Path $root 'ImgUpscalerUI\THIRD_PARTY_NOTICES.txt'
$noticeRoot = Join-Path $root 'THIRD_PARTY_NOTICES.txt'
$noticePub  = Join-Path $pub  'THIRD_PARTY_NOTICES.txt'
if (!(Test-Path $noticeSrc)) { Fail "找不到第三方声明唯一源:ImgUpscalerUI\THIRD_PARTY_NOTICES.txt" }
$noticeSrcHash = (Get-FileHash $noticeSrc -Algorithm SHA256).Hash
foreach ($t in @($noticeRoot, $noticePub)) {
    if (!(Test-Path $t)) { Copy-Item $noticeSrc $t -Force; Say "→ 声明:补了缺失的 $t" ; continue }
    if ((Get-FileHash $t -Algorithm SHA256).Hash -ne $noticeSrcHash) {
        Copy-Item $noticeSrc $t -Force
        Say "→ 声明:$t 与唯一源不一致,已用唯一源覆盖(以后只改 ImgUpscalerUI\THIRD_PARTY_NOTICES.txt)"
    }
}
$noticeText = Get-Content $noticeSrc -Raw -Encoding UTF8
$noticeKeys = @(
    @{ k = 'FFmpeg'; why = '第 8 条 FFmpeg(GPLv3,随包分发必须署名)' },
    @{ k = 'Real-CUGAN-MIT-bilibili-2022.txt'; why = 'Real-CUGAN 算法/权重许可原文的引用' },
    @{ k = 'realcugan-ncnn-vulkan-MIT-nihui-2019.txt'; why = 'Real-CUGAN 引擎移植层许可原文的引用' },
    @{ k = 'Practical-RIFE-MIT-hzwer-2021.txt'; why = 'RIFE 权重许可原文的引用' },
    @{ k = '模型权重来源与校验值.md'; why = '随包权重来源与逐文件 SHA256 记录' },
    @{ k = '16. Real-CUGAN'; why = '第 16 条 Real-CUGAN 声明(2026-09-24 重新随包)' }
)
$noticeMissing = @()
foreach ($k in $noticeKeys) {
    if ($noticeText -notlike "*$($k.k)*") {
        Write-Host ('  ✗   声明缺关键条目 {0}({1})' -f $k.k, $k.why) -ForegroundColor Red
        $noticeMissing += $k.k
    }
}
if ($noticeMissing.Count -gt 0) {
    $msg = "THIRD_PARTY_NOTICES.txt 缺 {0} 个关键条目:{1}。这份文件是「随包分发就要带上」的署名文件,缺了不能发。" -f $noticeMissing.Count, ($noticeMissing -join '、')
    if ($SkipCheck) { Write-Host "⚠ $msg(已 -SkipCheck,继续)" -ForegroundColor Yellow }
    else { Fail $msg }
}
else { Say "  OK  声明:两份与唯一源一致,关键条目 $($noticeKeys.Count)/$($noticeKeys.Count) 齐" }

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
    @{ p = 'engines\rife\rife-ncnn-vulkan-2026.exe'; why = 'RIFE 2026 重编引擎(程序优先用它;缺了就静默回退 2022 旧引擎)' },
    @{ p = 'engines\rife\rife-v4.13'; why = '补帧模型 v4.13(下拉第 1 项)' },
    @{ p = 'engines\rife\rife-v4.6'; why = '补帧模型 v4.6(下拉第 2 项)' },
    @{ p = 'engines\rife\rife-v4.26'; why = '补帧模型 v4.26(2026-09-24 上架的下拉第 3 项;缺了引擎会抛「缺少补帧模型」)' },
    @{ p = 'engines\realcugan\realcugan-ncnn-vulkan-2026.exe'; why = 'Real-CUGAN 重编引擎(官方 20220728 版在 50 系上出坏帧,必须用这一份)' },
    @{ p = 'engines\realcugan\models-se\up2x-conservative.param'; why = 'Real-CUGAN 权重(models-se;缺了引擎 exit=0 只出坏帧)' },
    @{ p = 'engines\realcugan\models-se\up3x-conservative.param'; why = 'Real-CUGAN 权重(3x 档)' },
    @{ p = 'engines\realcugan\models-se\up4x-conservative.param'; why = 'Real-CUGAN 权重(4x 档)' },
    @{ p = 'licenses\Real-CUGAN-MIT-bilibili-2022.txt'; why = 'Real-CUGAN 算法/权重许可原文(MIT © 2022 bilibili;SHA256 必须 = 8CAD8CFD…3CBA)' },
    @{ p = 'licenses\realcugan-ncnn-vulkan-MIT-nihui-2019.txt'; why = 'Real-CUGAN 引擎移植层许可原文(MIT © 2019 nihui)' },
    @{ p = 'licenses\Practical-RIFE-MIT-hzwer-2021.txt'; why = 'RIFE 权重许可原文(MIT © 2021 hzwer)' },
    @{ p = 'licenses\模型权重来源与校验值.md'; why = '随包权重来源与逐文件 SHA256 记录' },
    @{ p = 'THIRD_PARTY_NOTICES.txt'; why = '第三方组件许可声明(与 发布版\ 那份同哈希;缺了用户拿不到署名)' },
    @{ p = 'licenses\ALH-Pro-MIT-LICENSE.txt'; why = '本软件自己的 MIT 许可原文(根 LICENSE 的随包副本;MIT 要求随副本附带)' }
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
# 【2026-09-17】产物名规则改为固定 ALHPro_v{版本}_Setup.exe:① 不带时间戳;② **必须纯 ASCII** ——
# GitHub Release 的附件名会剥掉非 ASCII 字符(实测"本体"被吃掉,上传后变成 ALHPro_v1.4.0_.exe ⇒ 链接 404)。
$latest = Get-ChildItem $root -Filter 'ALHPro_v*_Setup.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (!$latest) { Fail '编译成功但没找到产物 ALHPro_v*_Setup.exe' }
$hash = (Get-FileHash $latest.FullName -Algorithm SHA256).Hash
Say ''
Say '✅ 打包完成'
Say ('   文件: {0}' -f $latest.Name)
Say ('   大小: {0:N1} MB' -f ($latest.Length / 1MB))
Say ('   SHA256: {0}' -f $hash)
Say ('   前缀:   {0}' -f $hash.Substring(0, 16))
Say ''
Say '提示:交付/发版前请把包名与 SHA256 前缀一起报出来;备用 ffmpeg 已随包(清单校验通过)。'
