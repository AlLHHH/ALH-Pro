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

# ===== ② 发布说明(取 RELEASE_NOTES.md 里当前版本那一节)=====
$notesPath = Join-Path $root 'RELEASE_NOTES.md'
if (!(Test-Path $notesPath)) { Fail '找不到 RELEASE_NOTES.md' }
$md = [IO.File]::ReadAllText($notesPath, [Text.Encoding]::UTF8)
$mm = [regex]::Match($md, "(?ms)^## v" + [regex]::Escape($Version) + ".*?(?=^## v|\z)")
if (!$mm.Success) { Fail "RELEASE_NOTES.md 里找不到 ## v$Version 那一节(先补更新说明)" }
$body = $mm.Value.Trim()
Say ("   发布说明: {0} 字(取自 RELEASE_NOTES.md 的 ## v{1} 节)" -f $body.Length, $Version)

if ($DryRun) {
    Say ''
    Ok 'DryRun:本地检查都过了(没有联网、没有改任何东西)。去掉 -DryRun 才会真的建 Release 并上传。'
    exit 0
}

# ===== ③ 取 token(git 凭据助手;不打印)=====
$req = Join-Path $env:TEMP 'alh_relreq.txt'
$nl = [string]([char]10)
[IO.File]::WriteAllText($req, "protocol=https" + $nl + "host=github.com" + $nl + $nl, (New-Object System.Text.UTF8Encoding($false)))
$env:GIT_TERMINAL_PROMPT = '0'
$credOut = & cmd /c "git credential fill < `"$req`"" 2>&1
$token = ($credOut | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''
if ([string]::IsNullOrWhiteSpace($token)) { Fail '拿不到 GitHub 凭据(git credential fill 没返回 password)。请先用 git 登录一次 GitHub。' }
Say '   凭据    : 已从 git 凭据助手取到(不打印)'

$apiBase = "https://api.github.com/repos/$Owner/$Repo"
$authHdr = "Authorization: Bearer $token"
$uaHdr = 'User-Agent: alh-pro-release-script'
$jsonHdr = 'Accept: application/vnd.github+json'

function ApiJson($method, $url, $jsonBody) {
    $tmp = Join-Path $env:TEMP 'alh_relbody.json'
    if ($jsonBody) { [IO.File]::WriteAllText($tmp, $jsonBody, (New-Object System.Text.UTF8Encoding($false))) }
    $args = @('-sS', '-X', $method, '-H', $authHdr, '-H', $uaHdr, '-H', $jsonHdr,
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
    $res = & curl.exe -sS -X POST -H $authHdr -H $uaHdr -H 'Content-Type: application/octet-stream' `
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
foreach ($a in $after) {
    $code = (& curl.exe -sS -o NUL -I -L --max-time 30 -w '%{http_code}' $a.browser_download_url 2>&1) | Out-String
    $code = $code.Trim()
    Say ("   {0,-32} {1,10:N0} 字节  HTTP {2}" -f $a.name, $a.size, $code)
}
Say ''
Ok "发布完成:https://github.com/$Owner/$Repo/releases/tag/$tag"
Say '   别忘了:① 官网 download.html 的安装包直链换成这个 tag;② 老版本区加上上一版;③ 推送仓库。'
