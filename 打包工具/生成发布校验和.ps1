# 生成发布目录的 SHA-256 校验和清单(防篡改 / 透明可验证)
# 用法: .\生成发布校验和.ps1  (默认扫 发布版 目录;可传参 -Dir 指定)
param(
    [string]$Dir = (Join-Path $PSScriptRoot '..\发布版')
)
$root = (Resolve-Path $Dir).Path
$files = Get-ChildItem $root -Recurse -File | Sort-Object FullName
$lines = foreach ($f in $files) {
    $rel = $f.FullName.Substring($root.Length + 1)
    $hash = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $rel"
}
$out = Join-Path $root 'CHECKSUMS.sha256'
$lines | Set-Content $out -Encoding ASCII
Write-Output "已生成校验和清单: $out"
Write-Output "共 $($files.Count) 个文件"
Write-Output "清单开头(前5行):"
$lines | Select-Object -First 5
