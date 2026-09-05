# Generate SHA-256 checksum manifest for a release folder (anti-tamper / verifiable transparency)
# Usage: .\gen_checksums.ps1  (default scans ..\发布版; pass -Dir to override)
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
Write-Output ("checksum manifest written: " + $out)
Write-Output ("files: " + $files.Count)
