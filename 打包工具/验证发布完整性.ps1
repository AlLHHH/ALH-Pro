# 验证发布目录与 CHECKSUMS.sha256 是否一致(防篡改检查)
# 用法: .\验证发布完整性.ps1  (默认扫 发布版;可传参 -Dir)
param(
    [string]$Dir = (Join-Path $PSScriptRoot '..\发布版')
)
$mf = Join-Path $Dir 'CHECKSUMS.sha256'
if (!(Test-Path $mf)) { Write-Output "找不到校验和清单: $mf"; exit 1 }
$bad = 0; $total = 0
Get-Content $mf | ForEach-Object {
    if ($_ -match '^([0-9a-fA-F]{64})\s+(.+)$') {
        $h = $Matches[1].ToUpperInvariant(); $rel = $Matches[2]
        $p = Join-Path $Dir $rel
        if (!(Test-Path $p)) { Write-Output "缺失: $rel"; $bad++ }
        else {
            $total++
            if ((Get-FileHash $p -Algorithm SHA256).Hash -ne $h) { Write-Output "不符: $rel"; $bad++ }
        }
    }
}
if ($bad -eq 0) { Write-Output "✔ 全部 $total 个文件与校验和一致,未被篡改" }
else { Write-Output "✘ $bad 个文件缺失/不符(可能被修改过)" }
