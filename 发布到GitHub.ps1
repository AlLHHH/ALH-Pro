# ALH Pro 一键发版到 GitHub Releases(在仓库根目录运行)
# 用法(在仓库根目录):
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\发布到GitHub.ps1
#   powershell ... -File .\发布到GitHub.ps1 -Version 1.4.1 -WithModelPack
#   powershell ... -File .\发布到GitHub.ps1 -DryRun            # 只做本地检查,不联网
#
# 它做四件事:
#   ① 从 csproj 读版本号(可用 -Version 覆盖),找到 ALHPro_v{版本}_Setup.exe 并核 SHA256;
#   ② 从 RELEASE_NOTES.md 抽出**当前版本那一节**当发布说明(body);
#   ③ 调 GitHub API 建 tag v{版本} 的 Release(已存在就复用,可重复运行);
#   ④ 用 curl 上传安装包(可选 -WithModelPack 连模型包一起传),再用 GET 校验附件可下载。
#
# 【凭据从哪来】走 git 自己的凭据助手(`git credential fill`,Windows 上是凭据管理器里的 GitHub 登录态),
#   脚本**不会**把 token 打印出来,也不会写进任何文件;token 需要对该仓库有写权限。
#
# ⚠ 【本文件必须带 UTF-8 BOM】—— 与 打包.ps1 同一条硬要求:丢了 BOM,Windows PowerShell 5.1 会按 GBK
#   读本文件,满屏中文变乱码并在中文串上抛 "string is missing the terminator"。
#   复检:前 3 字节应为 239,187,191。
param(
    [string]$Version,
    [string]$Installer,
    [string]$ModelPack,
    [switch]$WithModelPack,
    [string]$NotesFile,
    [switch]$NoBodyUpdate,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

$Owner = 'AlLHHH'
$Repo  = 'ALH-Pro'

function Say($m) { Write-Host $m }
function Fail($m) { Write-Host "❌ $m" -ForegroundColor Red; exit 1 }
function Ok($m) { Write-Host "✅ $m" -ForegroundColor Green }

# ===== ① 版本号与产物 =====
if (-not $Version) {
    $csproj = Join-Path $root 'ImgUpscalerUI\ImgUpscalerUI.csproj'
    if (!(Test-Path $csproj)) { Fail "找不到 $csproj" }
    $m = [regex]::Match([IO.File]::ReadAllText($csproj, [Text.Encoding]::UTF8), '<Version>([^<]+)</Version>')
    if (!$m.Success) { Fail 'csproj 里读不到 <Version>' }
    $Version = $m.Groups[1].Value.Trim()
}
$tag = "v$Version"
if (-not $Installer) { $Installer = Join-Path $root "ALHPro_v${Version}_Setup.exe" }
if (!(Test-Path $Installer)) { Fail "找不到安装包:$Installer —— 先跑 打包.ps1" }
$installerItem = Get-Item $Installer
$sha = (Get-FileHash $installerItem.FullName -Algorithm SHA256).Hash
Say "== 待发布 =="
Say ("   版本    : {0}" -f $Version)
Say ("   安装包  : {0}" -f $installerItem.Name)
Say ("   字节    : {0:N0}" -f $installerItem.Length)
Say ("   SHA256  : {0}(前缀 {1})" -f $sha, $sha.Substring(0, 16))
# 附件名必须纯 ASCII:GitHub 会剥掉非 ASCII 字符(实测"本体"被吃掉 → 链接 404)
if ($installerItem.Name -notmatch '^[\x20-\x7E]+$') { Fail "安装包文件名含非 ASCII 字符,GitHub 会改坏附件名:$($installerItem.Name)" }

# ===== ② 发布公告(body)=====
# 【为什么要允许"手写公告"】GitHub 上那份"更新公告"是给用户看的第一眼东西,RELEASE_NOTES.md 那节是
# 给软件内「更新说明」用的**流水账文体**(【新增】【修复】一堆段落),贴到 Release 页上不好读。
# 所以取文顺序改成:① -NotesFile 指定 → ② 仓库里的「发布公告_v{版本}.md」(根目录或 _qa\)→ ③ RELEASE_NOTES 那一节。
# 手写公告建议按这次 v1.4.1 的排法:一行加粗标题 + <sub>日期</sub> + 一句"这一版主要做什么"引用块
# + 分小节的要点(每节 3~7 条,条目用加粗开头)+ 末尾「下载」表格 + 一句"完整日志见 RELEASE_NOTES.md"。
$notesFrom = ''
if (-not $NotesFile) {
    foreach ($cand in @((Join-Path $root "发布公告_v$Version.md"),
                        (Join-Path $root "_qa\发布公告_v$Version.md"))) {
        if (Test-Path $cand) { $NotesFile = $cand; break }
    }
}
if ($NotesFile) {
    if (!(Test-Path $NotesFile)) { Fail "找不到公告文件:$NotesFile" }
    $body = ([IO.File]::ReadAllText($NotesFile, [Text.Encoding]::UTF8)).Trim()
    $notesFrom = (Split-Path -Leaf $NotesFile)
} else {
    $notesPath = Join-Path $root 'RELEASE_NOTES.md'
    if (!(Test-Path $notesPath)) { Fail '找不到 RELEASE_NOTES.md' }
    $md = [IO.File]::ReadAllText($notesPath, [Text.Encoding]::UTF8)
    $mm = [regex]::Match($md, "(?ms)^## v" + [regex]::Escape($Version) + ".*?(?=^## v|\z)")
    if (!$mm.Success) { Fail "RELEASE_NOTES.md 里找不到 ## v$Version 那一节(先补更新说明)" }
    $body = $mm.Value.Trim()
    $notesFrom = "RELEASE_NOTES.md 的 ## v$Version 节"
}
Say ("   发布公告: {0} 字(来自 {1})" -f $body.Length, $notesFrom)

if ($DryRun) {
    Say ''
    Ok 'DryRun:本地检查都过了(没有联网、没有改任何东西)。去掉 -DryRun 才会真的建 Release 并上传。'
    exit 0
}

# ===== ③ 取 token(git 凭据助手;不打印)=====
$req = Join-Path $env:TEMP 'alh_relreq.txt'
$nl = [string]([char]10)
# 【凭据来源优先级】环境变量 > git 凭据助手。
# 为什么要留环境变量这条:本机凭据管理器里那份 GitHub 登录态是**另一个账号**、对本仓库只有读权限
# (实测 GET /repos/AlLHHH/ALH-Pro 返回 permissions.push=false)⇒ 建 Release / 上传附件会 403。
# 用写权限账号的 PAT 时,最省事的做法是:先 `$env:GITHUB_TOKEN='ghp_...'` 再跑本脚本(或存进凭据管理器)。
$token = $env:GITHUB_TOKEN
if ([string]::IsNullOrWhiteSpace($token)) { $token = $env:GH_TOKEN }
$tokenFrom = '环境变量'
if ([string]::IsNullOrWhiteSpace($token)) {
    $tokenFrom = 'git 凭据助手'
    [IO.File]::WriteAllText($req, "protocol=https" + $nl + "host=github.com" + $nl + $nl, (New-Object System.Text.UTF8Encoding($false)))
    $env:GIT_TERMINAL_PROMPT = '0'
    $credOut = & cmd /c "git credential fill < `"$req`"" 2>&1
    $token = ($credOut | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''
}
if ([string]::IsNullOrWhiteSpace($token)) {
    Fail '拿不到 GitHub 凭据:既没有 $env:GITHUB_TOKEN / $env:GH_TOKEN,git credential fill 也没返回 password。'
}
Say "   凭据    : 来自 $tokenFrom(不打印)"

$apiBase = "https://api.github.com/repos/$Owner/$Repo"
$authHdr = "Authorization: Bearer $token"
$uaHdr = 'User-Agent: alh-pro-release-script'
$jsonHdr = 'Accept: application/vnd.github+json'

function ApiJson($method, $url, $jsonBody) {
    $tmp = Join-Path $env:TEMP 'alh_relbody.json'
    if ($jsonBody) { [IO.File]::WriteAllText($tmp, $jsonBody, (New-Object System.Text.UTF8Encoding($false))) }
    $args = @('-sS', '--ssl-no-revoke', '-X', $method, '-H', $authHdr, '-H', $uaHdr, '-H', $jsonHdr,
              '-H', 'Content-Type: application/json', '--max-time', '60', '-w', "`n%{http_code}", $url)
    if ($jsonBody) { $args += @('--data-binary', "@$tmp") }
    # 【为什么包一层】$ErrorActionPreference='Stop' 时,curl 连不上会在 stderr 上抛终止错误,
    # 脚本会带着一堆 PowerShell 红字直接退出,而不是给出"网络不通"这句人话。这里自己兜住。
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = & curl.exe @args 2>&1 } catch { $out = @(("curl: " + $_.Exception.Message), '000') }
    finally { $ErrorActionPreference = $prev }
    $lines = @($out | ForEach-Object { [string]$_ })
    $code = if ($lines.Count -gt 0) { $lines[-1].Trim() } else { '000' }
    if ($code -notmatch '^\d{3}$') { $code = '000' }
    $json = if ($lines.Count -gt 1) { ($lines[0..($lines.Count - 2)] -join "`n") } else { '' }
    return @{ code = $code; json = $json }
}

# ===== ④ 建(或复用)Release =====
Say '== 建 Release =='
$payload = @{
    tag_name         = $tag
    target_commitish = 'main'
    name             = "ALH Pro v$Version"
    body             = $body
    draft            = $false
    prerelease       = $false
} | ConvertTo-Json -Depth 4
$r = ApiJson 'POST' "$apiBase/releases" $payload
$releaseId = $null
if ($r.code -eq '201') {
    $o = $r.json | ConvertFrom-Json
    $releaseId = $o.id
    Ok "已创建 Release $tag(id $releaseId)"
} elseif ($r.code -eq '422') {
    Say "   $tag 已存在(422),改为复用它"
    $g = ApiJson 'GET' "$apiBase/releases/tags/$tag" $null
    if ($g.code -ne '200') { Fail "取已有 Release 失败(HTTP $($g.code)):$($g.json)" }
    $o = $g.json | ConvertFrom-Json
    $releaseId = $o.id
    Ok "复用 Release $tag(id $releaseId)"
    # 【顺手把公告也更新掉】用户 2026-09-22 明确要求"GitHub 的更新公告要规整一点"⇒ 改完公告文件
    # 重跑本脚本就该生效,不必去网页上手工改。只在**确实不一样**时才 PATCH;-NoBodyUpdate 可关掉。
    if (-not $NoBodyUpdate) {
        $oldBody = [string]$o.body
        if ($oldBody.Trim() -ne $body.Trim()) {
            $patch = @{ body = $body } | ConvertTo-Json -Depth 3
            $pr = ApiJson 'PATCH' "$apiBase/releases/$releaseId" $patch
            if ($pr.code -eq '200') { Ok ("公告已更新(旧 {0} 字 → 新 {1} 字)" -f $oldBody.Length, $body.Length) }
            else { Say ("   ⚠ 公告更新失败(HTTP $($pr.code)):$($pr.json)") }
        } else {
            Say '   公告与线上一致,不动'
        }
    }
} else {
    Fail "建 Release 失败(HTTP $($r.code)):$($r.json)"
}

# ===== ⑤ 上传附件(幂等:同名已存在则改传它的上传地址会 422,先查)=====
$existing = ((ApiJson 'GET' "$apiBase/releases/$releaseId/assets" $null).json | ConvertFrom-Json)
function UploadAsset($path) {
    $item = Get-Item $path
    $name = $item.Name
    if ($existing -and ($existing | Where-Object { $_.name -eq $name })) {
        Say "   $name 已在该 Release 上,跳过"
        return
    }
    Say ("   上传 {0}({1:N1} MB)…" -f $name, ($item.Length / 1MB))
    $url = "https://uploads.github.com/repos/$Owner/$Repo/releases/$releaseId/assets?name=$name"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $res = & curl.exe -sS --ssl-no-revoke -X POST -H $authHdr -H $uaHdr -H 'Content-Type: application/octet-stream' `
        --retry 3 --retry-delay 5 --max-time 0 --data-binary "@$($item.FullName)" `
        -w "`n%{http_code}" $url 2>&1
    $sw.Stop()
    $t = ($res | Out-String).TrimEnd()
    $code = ($t -split "`n")[-1].Trim()
    if ($code -eq '201') {
        Ok ("{0} 上传完成(用时 {1:N0} 秒,{2:N1} MB/s)" -f $name, $sw.Elapsed.TotalSeconds, ($item.Length / 1MB / [Math]::Max(0.001, $sw.Elapsed.TotalSeconds)))
    } else {
        Fail "上传 $name 失败(HTTP $code):$t"
    }
}
UploadAsset $installerItem.FullName
if ($WithModelPack) {
    if (-not $ModelPack) {
        foreach ($c in @((Join-Path $root '打包工具\models_v1.0.zip'),
                         (Join-Path $env:USERPROFILE 'Desktop\models_v1.0.zip'))) {
            if (Test-Path $c) { $ModelPack = $c; break }
        }
    }
    if (!$ModelPack -or !(Test-Path $ModelPack)) { Fail '要一起传模型包,但找不到 models_v1.0.zip(用 -ModelPack <路径> 指定)' }
    UploadAsset $ModelPack
}

# ===== ⑥ 校验:附件真的能下载吗 =====
Say '== 校验附件 =='
$after = ((ApiJson 'GET' "$apiBase/releases/$releaseId/assets" $null).json | ConvertFrom-Json)
if (-not $after) { Fail 'Release 上一个附件都没有(上传没成功 —— 常见原因:上传中途断开,占位附件会被 GitHub 清掉)' }
foreach ($a in $after) {
    # 【必须 --ssl-no-revoke】本机(国内直连)证书吊销列表查不到 ⇒ 不带这个开关 curl 会 (35) CRYPT_E_NO_REVOCATION_CHECK
    $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $code = (& curl.exe -sS --ssl-no-revoke -o NUL -I -L --max-time 60 -w '%{http_code}' $a.browser_download_url 2>&1 | Out-String).Trim() }
    catch { $code = 'ERR' }
    finally { $ErrorActionPreference = $prevEap }
    Say ("   {0,-32} {1,13:N0} 字节  state={2}  HTTP {3}" -f $a.name, $a.size, $a.state, $code)
}
Say ''
Ok "发布完成:https://github.com/$Owner/$Repo/releases/tag/$tag"
Say '   收尾清单(官网/仓库/OSS 这几步脚本不代做):'
Say ("     ① website\download.html:安装包与模型包的直链 tag 换成 v{0}(老版本区把上一版加进去)" -f $Version)
Say '     ② git add -A && git commit && git push origin main'
Say '     ③ 把 website\ 整个目录传到主机/OSS(桌面「上传到OSS_v{版本}_<日期>」那种暂存目录是我这边刷好的)'
Say '     ④ 网盘里的「完整版」要不要同步更新,由你决定'
