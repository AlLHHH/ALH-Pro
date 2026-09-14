# ALH Pro 官网(纯静态站)

这个目录是 ALH Pro 的官方网站源码,面向国内用户与国内主机 + ICP 备案场景。

它是**软件的基础设施**,不只是介绍页:承担「介绍 + 下载 + 校验值」「教程 / FAQ」「隐私说明」「开源许可与致谢」「更新日志」,
并为将来可能增加的「更新检查接口 / 安装包镜像」预留位置(**本期只做静态页,不含任何接口与后端**)。

---

## 一、技术约定(请勿随意破坏)

| 约定 | 说明 |
|---|---|
| **纯静态** | 只有 HTML + CSS + 极少量原生 JS。**没有**任何构建工具、框架、包管理器、`node_modules` |
| **零外部依赖** | 不引用任何境外 CDN、在线字体、外部 JS 库、统计脚本。字体全部走系统字体栈(Windows 上是微软雅黑);图标是手写内联 SVG |
| **可离线打开** | 直接双击 `index.html` 就能看;所有资源都是相对路径 |
| **移动端自适应** | 手机 / 平板 / 桌面三档布局;导航在窄屏折叠(无 JS 时自动展开为可换行的列表) |
| **深色模式** | 深色为默认,浅色跟随系统 `prefers-color-scheme` 自动切换,无需手动开关 |
| **减少动态效果** | 所有位移类动效(淡入上移、光斑漂移、步骤条点亮、标题浮现…)都挂在 `prefers-reduced-motion: reduce` 下关闭,只保留颜色变化。**加新动效时不要漏这一条**(见 `style.css` 最后一节) |
| **体积预算** | `style.css` + `site.js` 合计控制在 **100 KB** 以内(当前约 66 KB)。想加效果请手写 CSS/JS,不要引入外部库 |
| **无统计** | 不放任何统计 / 埋点脚本(国内备案网站如需统计要另行申报,先不加) |
| **编码** | 所有文件 **UTF-8**;HTML/CSS/JS 保持**无 BOM**(与仓库习惯一致) |
| **只改这个目录** | 本目录与软件代码互不影响;不要在这里放源码或安装包 |

> ⚠ **不要用 PowerShell 重定向(`>` / `Out-File` / `Set-Content`)写这些文件** —— Windows PowerShell 5.1 会按 ANSI 编码写出,
> 中文会乱码。请用编辑器 / 写文件工具,并确认保存为 UTF-8。

---

## 二、目录结构

```
website/
├── index.html          首页:是什么、给谁用、能做什么、实测速度、隐私摘要、许可摘要
├── download.html       下载:安装包 / 完整版 / 模型包、系统要求、安装步骤、SHA256 校验方法、下载慢怎么办
├── tutorial.html       使用教程:抠图模型安装、图片放大、AI 抠图、视频处理、音频处理、输出格式
├── faq.html            常见问题:下载慢 / 装不上 / 杀软误报 / 显卡兼容对照表 / 结果不对 / 收费与授权 / 隐私
├── privacy.html        隐私说明:本地处理 + 三处联网行为如实列全 + 本站自身说明
├── licenses.html       开源许可与致谢:软件本体许可 + 第三方程组逐条声明 + 致谢名单
├── changelog.html      更新日志:逐版本摘要(含“需要你重新调参”的提示)
├── assets/
│   ├── css/style.css   全部样式。配色 / 圆角 / 阴影 / 缓动都收敛在文件开头的 §1 设计令牌里,
│   │                   改深浅两套主题只改那里;最后一节是 prefers-reduced-motion 的统一关闭规则
│   ├── js/site.js      全部交互与动效,按 §0~§10 分模块的小函数:
│   │                   手机端导航折叠 / 页内章节条与滚动高亮 / 滚动进度条 / 滚动进入动画 /
│   │                   标题逐词浮现 / 数字滚动计数 / 卡片鼠标跟随光晕 / 步骤条点亮 /
│   │                   FAQ 高度过渡展开 / 复制按钮 / 平滑锚点
│   └── img/logo.png    站点图标 / 页眉标志(取自软件图标 ALHPro_icon.png;全站唯一图片)
└── README.md           本文件
```

> 页面里的动效钩子(改 HTML 时会用到):
> `data-reveal` = 该区块滚动进入时淡入上移;`data-reveal-group` = 放在容器上,
> 让子元素按 0/80/160/240ms 依次错开;`data-count="0.25" data-dec="2"` = 进入视口时从 0 滚到该值;
> `main .content` 下的一级 `<ol>` 会自动变成"步骤流"(竖线随滚动点亮)。
> `html.has-js` 由 `<head>` 里的一小段内联脚本加上;万一 `site.js` 没加载成功,3 秒后会自动摘掉它,
> 页面回到"无 JS 也完整可读"的状态(`site.js` 启动完成会给 `<html>` 打 `data-site-ready`)。

---

## 三、本地预览

**最简单:** 直接双击 `website\index.html`,用浏览器打开即可(所有链接都是相对路径)。
注意:`file://` 下浏览器的“一键复制”可能被禁用(站点会自动退回到兼容方式),想完整测试请用下面的本地服务。

**起一个本地静态服务(不装任何东西,PowerShell 原生):**

```powershell
$root = "D:\deep\alh-pro\website"
$l = [System.Net.HttpListener]::new()
$l.Prefixes.Add("http://localhost:8080/")
$l.Start()
Write-Host "http://localhost:8080/ 已启动,Ctrl+C 停止"
while ($true) {
  $c = $l.GetContext()
  $rel = $c.Request.Url.LocalPath.TrimStart('/') -replace '/', '\'
  $p = Join-Path $root $rel
  if ([string]::IsNullOrEmpty($rel) -or (Test-Path $p -PathType Container)) { $p = Join-Path $p 'index.html' }
  if (Test-Path $p -PathType Leaf) {
    $bytes = [IO.File]::ReadAllBytes($p)
    $ext = [IO.Path]::GetExtension($p).ToLower()
    $c.Response.ContentType = switch ($ext) {
      '.html' { 'text/html; charset=utf-8' }
      '.css'  { 'text/css; charset=utf-8' }
      '.js'   { 'application/javascript; charset=utf-8' }
      '.png'  { 'image/png' }
      default { 'application/octet-stream' }
    }
    $c.Response.OutputStream.Write($bytes, 0, $bytes.Length)
  } else {
    $c.Response.StatusCode = 404
    $b = [Text.Encoding]::UTF8.GetBytes('404 Not Found')
    $c.Response.OutputStream.Write($b, 0, $b.Length)
  }
  $c.Response.Close()
}
```

自测清单(改完页面请过一遍):

- [ ] 每一页都能打开,页眉导航 7 个链接都跳得通,页脚链接也对;
- [ ] 手机浏览器(或浏览器开发者工具切窄屏)打开:导航可折叠展开、表格能横向滚动、字不溢出;
- [ ] 系统切到深色模式,文字与背景对比正常;
- [ ] `download.html` 的两个“复制命令”按钮、百度网盘“复制提取码”按钮可用;
- [ ] 全站没有引用任何外部域名资源(只有指向 GitHub / 百度网盘 / 工信部备案系统的**超链接**,那是跳转,不是加载)。

---

## 四、上线前必做清单(逐项打勾)

1. **替换 ICP 备案号(7 个页面都要改)。**
   每个页面页脚都是这一行(占位符):
   ```html
   <p class="icp">©2026 ALH Pro · <a href="https://beian.miit.gov.cn/" rel="nofollow">京ICP备XXXXXXXX号</a></p>
   ```
   把 `京ICP备XXXXXXXX号` 换成工信部下发的真实备案号。**备案号必须可见,并按惯例链接到
   <https://beian.miit.gov.cn/>**。若你所在省份要求同时展示公安联网备案号,按 `index.html` 页脚注释里的写法追加一行。
2. **填 `download.html` 的 SHA256。** 三个「待填入」都要换成真实值(计算命令写在文件里的维护者注释里);
   顺便核对文件名与大小是否与发布页一致(标准版附件名以 Release 页实际显示为准)。
3. **确认下载链接可用:** GitHub Releases 页、百度网盘链接与提取码,以及 `models_v1.0.zip` 附件确实存在于某个已发布的 Release 上。
4. **同步版本号。** 版本号散落在 `index.html`、`download.html`、`changelog.html`、`privacy.html`,以及各页 `<title>` / `meta description` 中 ——
   发新版时统一替换(可用编辑器全文查找,例如搜 `1.3.6`)。
5. **`changelog.html` 补一条新版本。** 格式:标题 `vX.Y.Z + 日期 + 一句话`,并用 `<details>` 展开细节;
   凡是需要用户重新调参、默认值变化、界面迁移的改动,**必须单独标出**。
6. **可选:** 打开 `index.html` 里 `rel="canonical"` 的注释并填上真实域名;
   若域名是 `www` 与裸域并存,记得做 301 跳转统一。
7. **不要在页面里加统计脚本。** 若确实需要,请先确认合规要求并自行申报,再统一加。

---

## 五、部署到国内主机 / OSS

### 方案 A:国内云服务器 / 虚拟主机 + Nginx(最通用)

把 `website/` 整个目录里的文件上传到站点根目录(如 `/www/wwwroot/example.com/`),Nginx 参考配置:

```nginx
server {
    listen 80;
    server_name example.com www.example.com;   # 换成你的域名
    root /www/wwwroot/example.com;
    index index.html;

    charset utf-8;
    default_type text/html;

    # 静态站点:开启 gzip,减少传输量
    gzip on;
    gzip_types text/css application/javascript image/png;

    # 小站点缓存策略:HTML 短缓存,CSS/JS/图片长缓存(改文件名或加版本号再发布)
    location ~* \.(css|js|png|jpg|jpeg|svg|ico|webp)$ {
        expires 7d;
        add_header Cache-Control "public";
    }
    location = /index.html { add_header Cache-Control "no-cache"; }

    # 可选:自定义 404(如需,请先在本目录新建 404.html,再打开下面两行)
    # error_page 404 /404.html;

    location / { try_files $uri $uri/ =404; }
}
```

要点:

- 国内的**虚拟主机 / 云服务器 + 域名**必须已完成备案,否则 80 / 443 会被拦截;
- 上传工具随意(宝塔面板、FTP、`scp`、Git 拉取都行),不要上传 `.git` 之外的临时文件;
- HTTPS:用主机商提供的免费证书(DV 证书),配好后把 HTTP 301 到 HTTPS;
- 如果你想让 `http://example.com` 与 `https://www.example.com` 都可用,记得统一到一个并做跳转。

### 方案 B:阿里云 OSS(静态网站托管,最省运维)

1. 新建 Bucket:地域选国内、读写权限设为**公共读**;
2. 开启「静态页面」:默认首页 `index.html`(可选错误页 `404.html`);
3. 上传 `website/` 下全部文件与目录(保留 `assets/` 层级);
4. 上传时确认 `.html` / `.css` / `.js` 的对象 Content-Type 带 **`charset=utf-8`**,否则中文可能乱码;
5. 绑定**自定义域名**(需该域名已备案),开启 CDN 加速;
6. 建议给 `assets/` 下的文件设置较长的缓存时间,`index.html` 设置较短。

### 方案 C:腾讯云 COS / 其它对象存储

与方案 B 同理:开启静态网站功能、公共读、设置默认首页、绑定已备案的自定义域名。

> 上传/发布前,建议先在本地把「上线前必做清单」过完再传,避免改一次传一次。

---

## 六、ICP 备案与合规注意事项(简短可执行)

> 以下为常见操作要点,不构成法律意见;各地管局要求略有差异,以你所在省管局与接入商的最新指引为准。

1. **先备案再上线。** 域名需在工信部备案系统通过(主体 + 网站),服务器必须在中国大陆境内;备案审核期间网站不能正常访问,
   建议先放一个“备案中”的空白页,通过后再放正式内容。
2. **备案信息要与实际一致。** 主办单位/个人、网站名称、服务内容、域名列表、接入商都要与实际相符;换主机商要办理**接入备案**变更。
3. **页脚展示。** 备案号放在首页(以及各页)底部可见位置,并链接到 <https://beian.miit.gov.cn/>;**不要**把备案号做成图片或隐藏。
4. **公安联网备案。** 网站开通后按当地要求(一般为 30 日内)到全国互联网安全管理服务平台
   <https://beian.mps.gov.cn/> 办理,通过后在页脚展示公安备案号。
5. **网站内容要与备案性质相符。** 个人备案不要出现经营性内容(在线销售、付费会员等)。
   本项目站点是开源软件介绍与下载页,属于非经营性;若后续要接广告招商、付费服务,需按经营性网站重新处理。
6. **不要放第三方统计 / 追踪脚本。** 本项目未放任何统计脚本;若确有需要,先确认合规与申报要求再加。
7. **不要依赖境外资源。** 本站已全部本地化(字体、CSS、图标),不要为了“好看”随手引入境外 CDN 字体或 JS 库 ——
   既影响国内速度,也不利于合规审查。
8. **大文件下载注意带宽。** 安装包动辄几百 MB:不建议直接放在自己的虚拟主机上提供下载(会吃满带宽),
   用 GitHub Releases / 百度网盘,或放 OSS/CDN。**本期没有自建下载服务,页面上也没有承诺“GitHub 打不开也能下载”。**
9. **版权与许可信息的展示是许可证要求。** 站上「开源许可与致谢」页与随软件的 `THIRD_PARTY_NOTICES.txt` 属于合规内容,
   不要为了精简页面把它删掉或藏进深层链接。
10. **保留变更记录。** 备案信息、网站内容(尤其是隐私说明与许可声明)发生变化时,同步更新页面与备案信息。

---

## 七、日常维护约定(让官网跟上软件)

**每次发新版,至少动这几处:**

| 文件 | 要改什么 |
|---|---|
| `changelog.html` | 新增一条版本记录(含“要不要重新调参”的提示) |
| `download.html` | 文件大小、版本号、SHA256 三个值;若有新增/改名附件也要改 |
| `index.html` | 首页版本号、下载按钮文案、实测速度表(有新数据才改) |
| `privacy.html` | 若**联网行为有变化**(新增接口、换域名、改频率),必须同步更新;否则不动 |
| `faq.html` | 按用户反馈持续补充(最常被问到的问题放前面) |

**给将来预留的兜底(本期未实现,别在页面上承诺):**

- **安装包镜像 / 更新检查接口**:将来若自建,可在 `download.html` 增加一个“国内镜像”区块,
  并同步软件内更新检查的端点列表(软件侧在 `ImgUpscalerUI/UpdateChecker.cs`,目前只有 GitHub 官方 API 与一个公开镜像);
- 真要做接口时,记得同步改 `privacy.html` 的「联网行为」章节 —— **这一页的准确性是硬要求**,
  凡是新出现的联网行为都要写进去,并且要写清楚「传什么、不传什么、失败会怎样」。

---

## 八、内容来源(改文案前先看这里)

本目录所有事实性内容都来自仓库内的既有文档,**不要凭印象新增数据或承诺**:

| 站点页面 | 内容来源 |
|---|---|
| 首页功能与显卡说明 | `README.md` |
| 教程页 | `使用教程.md` |
| 下载页(版本 / 大小 / 安装 / 升级) | `RELEASE_NOTES.md`、`installer.iss`(最低系统、默认安装目录、是否要管理员权限) |
| 更新日志 | `RELEASE_NOTES.md`、`release_history.json`、`ImgUpscalerUI/ImgUpscalerUI.csproj`(当前版本号) |
| 隐私说明(三处联网行为与域名) | `ImgUpscalerUI/UpdateChecker.cs`、`AdFetcher.cs`、`TipFetcher.cs` |
| 许可与致谢 | `THIRD_PARTY_NOTICES.txt`、`LICENSE`、`声明.md`、`licenses/`、`ImgUpscalerUI/thanks/` |
| 实测速度数据 | 作者本机实测(RTX 4060 Laptop)—— 页面已注明机器与“不作为承诺” |

作者:AlL.H
