# ALH Pro 发布完整性验证(防篡改 / 透明可验证)

## 目的
证明「某版本的二进制 = 作者发布的、没被改过」。若有人改了你的软件再截图说"软件干了 XXX",用公开的校验和一对即可穿帮——这是类似可信时间戳的发布透明机制。

## 做法
1. 发布时计算发布目录**每个文件**的 SHA-256,生成 `CHECKSUMS.sha256`(格式:`哈希  相对路径`)。
2. 把 `CHECKSUMS.sha256` **发到 GitHub**(Release 附件 / 提交进仓库)——GitHub 的提交时间戳 + git 哈希 = 公开、不可篡改的"可信记录"。
3. 任何人用验证脚本重新算哈希、和清单比对,**相符=未被篡改,不符=文件被改过**。

## 生成校验和(在 打包工具 目录跑;默认扫 ..\发布版,可 -Dir 指定)
```powershell
.\gen_checksums.ps1 -Dir 'D:\deep\alh-pro\发布版'
```
或内联:
```powershell
$root='D:\deep\alh-pro\发布版'; $files=Get-ChildItem $root -Recurse -File | Sort-Object FullName
$lines=foreach($f in $files){ $h=(Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); "$h  $($f.FullName.Substring($root.Length+1))" }
$lines | Set-Content (Join-Path $root 'CHECKSUMS.sha256') -Encoding ASCII
```

## 验证完整性
```powershell
.\验证发布完整性.ps1 -Dir 'D:\deep\alh-pro\发布版'
```
相符显示「✔ 全部 N 个文件一致,未被篡改」;否则列出「不符/缺失」的文件。

## 发布到 GitHub 的建议
- 每次发版把 `CHECKSUMS.sha256` **作为 Release 附件**上传(GitHub 有下载记录+时间戳);
- 也在仓库根提交一份(如 `checksums/v1.3.0.sha256`),Git 历史更可追溯;
- 用户/任何人可对下载的 zip 跑验证,确认没被第三方替换。

> 说明:这**不是**政府级 RFC3161 时间戳服务,但 GitHub 的公开不可篡改 + SHA-256 已足以做到"发布透明、可被任何人验证是否被改"。
