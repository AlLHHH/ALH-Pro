# ALH Pro 官网(纯静态站)

这个目录是 ALH Pro 的官方网站源码,面向国内用户与国内主机 + ICP 备案场景。

它是**软件的基础设施**,不只是介绍页:承担「介绍 + 下载」「教程 / FAQ」「隐私说明」「开源许可与致谢」「更新日志」,
并为将来可能增加的「更新检查接口 / 安装包镜像」预留位置(**本期只做静态页,不含任何接口与后端**)。

---

## 一、技术约定(请勿随意破坏)

| 约定 | 说明 |
|---|---|
| **纯静态** | 只有 HTML + CSS + 极少量原生 JS。**没有**任何构建工具、框架、包管理器、`node_modules` |
| **零外部依赖** | 不引用任何境外 CDN、在线字体、外部 JS 库、统计脚本。字体全部走系统字体栈(Windows 上是微软雅黑);图标是手写内联 SVG |
| **可离线打开** | 直接双击 `index.html` 就能看;所有资源都是相对路径 |
| **移动端自适应** | 手机 / 平板 / 桌面三档布局;导航在窄屏折叠(无 JS 时自动展开为可换行的列表) |
| **深色模式** | **深色是唯一主题**,不跟随系统:不随 `prefers-color-scheme` 变化,手机(浅色模式)上同样是黑底(见 `style.css` §1.2 的说明与 §1 的令牌) |
| **减少动态效果** | 所有位移类动效都挂在 `prefers-reduced-motion: reduce` 下关闭(含光斑、逐词、视差、进度条)。**加新动效时不要漏这一条**(见 `style.css` §20) |
| **视觉基调** | 底色只用中性灰阶,只有一个强调色;边框统一 1px;阴影只做"提升"不做"发光"。动效可以丰富,但必须守住下面三条滚动性能铁律 |
| **滚动性能铁律** | ① `backdrop-filter`(液态玻璃)**受预算管制,不是禁用**:只给少数固定条/大块面板(顶栏、章节条、卡片、提示块、表格容器、折叠块),半径 ≤14px(≤720px 为 8px),**禁止**写进 animation/transition,**禁止玻璃套玻璃**,不支持时给不透明底、≤720px 关掉次级面板的模糊(全部细则与预算见 `style.css` §23);② **禁止在滚动相关代码里调用任何 scroll API**(`scrollTo`/`scrollIntoView`/`scrollTop=`)—— 这条是"滚动时被拽着走"的根因,详见 `site.js` 顶部说明;③ 滚动路径上只动 `transform`/`opacity`,滚动联动优先用 CSS 滚动驱动动画 |
| **动态背景** | 整站流动背景由 **4 层 fixed 伪元素**(`html::before/::after` + `body::before/::after`)承担,每层 2 个大径向/斜向渐变,周期 **24/32/40/52s** 错开漂移,只动 `transform`/`opacity`;`will-change: transform` 只给这 4 层。铺满整页(不只是首屏),所以卡片滑到哪都透着流动的底。≤720px 减到 3 层并把周期拉长(48/36/72s);`prefers-reduced-motion: reduce` 下全部停掉(§23.6) |
| **体积预算** | `style.css` + `site.js` 合计控制在 **106 KB(106,000 B)** 以内 —— 2026-09 先后由 100 KB 提到 **104 KB**(原因:新增「卡片内跟随光」+「可点卡片 hover」+「背景细节层(噪点/点阵/极光带)」三类效果),**2026-09-16 再由 104 KB 提到 106 KB**(原因:下载页与主页新增「点下载后的提示弹窗」= `site.js` §13 + `.dl-modal` 样式 + 一条全局 `[hidden]` 兜底规则)。**每次提额都先把我们自己写的中文注释压缩**(§0 基线表、文件头铁律、各 § 说明块);当前 **104,985 B(余量约 1 KB)**。想加效果请手写 CSS/JS,不要引入外部库;唯一的 `<img>` 是本地 logo(4 KB),噪点是内联 `data:` URI(≈330 B) |
| **首屏四个入口** | `Windows 直链(点击即下载)→ 百度网盘(同款 .btn-primary,点了直接进网盘)→ 手机版(灰·disabled)→ GitHub 图标`。网盘按钮里印着**提取码 `6eym`**(`.btn-code` 小标签),`target="_blank" rel="noopener"`,**是网页不是文件直链 → 不加 `download`**;链接实测 **HTTP 200**(`curl -L --ssl-no-revoke`)。桌面 1280 四者**同排同高 47px、间距 12px**;≤720px 各自占行(等宽,不比 Windows 宽)、图标居中。模型包直链/系统要求仍在 `download.html`,首屏提示行里有「下载页」链接可达 |
| **动态背景** | **四层结构**(全部自包含,零外部资源):① **底层** = `html` 的 `--body-bg` 纯深黑;② **柔光层** = 4 层 fixed 伪元素(`html/body` 的 `::before/::after`,低饱和冷色 alpha **0.05~0.08**,周期 24/32/40/52s,只动 `transform`/`opacity`),整体套一条 `--bg-mask` 径向蒙版**向四周淡出** → 中心有光、四角纯黑;③ **细节层** = `.bg-detail`(由 `site.js` §7b 创建,静态、不参与动画):内联 **data URI 的 SVG `feTurbulence`** 细颗粒噪点(`stitchTiles` 无缝平铺,`opacity .03`)+ **1px 点阵**(两条 `repeating-linear-gradient`,44px 一格,`--grid-line: rgba(255,255,255,.022)`),用 `--detail-mask` 淡出;**不引任何图片/字体/CDN**;④ **极光带** = `html::after` 里一条很宽的斜向 `linear-gradient`(`--aurora-band`,跟着该层的 40s 漂移走)。**实测**(同机同参数、动画停到初始帧,`bg-pretty-before-*` vs `bg-pretty-after-*`):四角 rgb `(12,14,19)/(13,12,16)/(10,10,11)/(18,19,24)` → **`(9,9,11)/(9,9,11)/(10,10,11)/(10,10,11)`**(= 底色,收黑);中心 rgb(20,21,24) → **rgb(23,24,28)**(中心柔光);整页平均亮度 0.039749 → **0.039722(-0.2%,没有变亮)**;噪点高频能量 0.031 → **0.089**(可见但不刺眼),点阵实测沿格线 **+1.9/255**(before −0.005);最坏背景像素 rgb(34,36,41) 上文字对比度 `--fg` 14.1:1 / `--fg-soft` 6.7:1 / `--muted` 4.8:1 / 链接 6.3:1。**手机 390 视口滚动实测:237 帧 / 3.96s = 59.9fps,`longtask` 0 次**;≤720px 减一层(`html::after` 隐藏)。`prefers-reduced-motion` 下 4 层全部 `animation-name: none`,噪点/点阵本来就静态 |
| **液态玻璃** | 做法:**半透明底 + `backdrop-filter: blur() saturate()` + 1px 极淡边框(`--glass-line`)**,就这三样。2026-09 按用户「卡片不要那么多边缘高光」**删掉了全部点缀**:顶边 1px 亮线、四周 1px 内高光、静态对角高光(`--glass-sheen`)、面板外圈投影**全部移除**,令牌 `--glass-edge` / `--glass-edge-soft` / `--glass-inner` / `--glass-sheen` 一并删除(**不要**再加回来,也不用新阴影/描边替代);顶栏与页内章节条是浮层,只保留**一条 1px 底边**(未吸顶 `transparent`,吸顶 `--glass-line`)以防滚动时与内容糊在一起。顶栏 0.52 / 吸顶 0.68、卡片 0.44、次级面板 0.58、`saturate 1.45`(顶栏 170%),半径 12px。**实测**(修复前后同机同参数、动画停到初始帧):卡片顶边剖面 rgb(80,81,87) → **rgb(14,16,20)**(无亮线),左边剖面 rgb(50,52,57) → **rgb(43,44,48)**(只剩 1px 边框);`getComputedStyle` 上 `.card` 的 `box-shadow: none`、`background-image: none`。<br>**通透靠面板 + 留白**(不是靠提亮底色):卡片 0.52 → 0.44,`.card` padding 20 → 26,区块间距 +8~10。<br>**次要文字**:`--muted` 从 `#8a8a93` 提到 `#8f8f98`(提亮,不是调灰),黑底上 5.4:1。<br>**卡片无 hover 效果(可验)**:`.card:hover` 规则 **0 条**;CDP `CSS.forcePseudoState` 强制 `:hover` 后 10 项计算属性差值为 **0**,真实鼠标移入同一张卡片、卡片整块及四条 6px 边缘带的像素差 **全为 0**(同状态噪声底也是 0)。<br>**硬预算**:blur ≤14px(桌面 10 / 顶栏 12 / ≤720px 8~10);**绝不写进 animation/transition**;**禁止玻璃套玻璃**(`.demo` 在 `.card` 里 → 只上"玻璃皮");磨砂只给 顶栏/章节条/提示块/表格容器/三列大卡 —— 折叠块、目录卡、许可条、小卡只上玻璃皮;降级见 `style.css` §23.5~§23.8。验证数据(截图/边缘像素剖面/对比度/10 档宽度 × 7 页溢出)在 `D:\deep\_verify\` |
| **指针跟随光** | 一道很淡的冷色光跟着鼠标走,做法是**独立固定层** `.pointer-glow`(`position: fixed; inset 由 transform 控制; z-index: -1; pointer-events: none`),`background: radial-gradient(circle, var(--glow), transparent 72%)`,`--glow: rgba(125,162,255,.07)`。位置由 `site.js` §8 的 **rAF 节流 pointermove** 每帧只改 `transform: translate3d(x,y,0)`(**不改 `background-position`**,不写进会碰玻璃合成的属性;仅 `opacity` 有 260ms 过渡)。**只给 `(hover:hover) and (pointer:fine)` 且未开减小动效的设备创建**(CSS 再兜一层 `display:none`)。**关键:光在玻璃后面** —— 不做成卡片伪元素,那样移入/移出会让卡片重新合成、顶部高光闪一下(用户报的"闪白")。**实测**(screencast 逐帧,96 帧,`--disable-gpu` 无头,背景动画停住):卡片**顶部 16px 带逐帧 Δ 全为 0**、顶栏带最大帧均值 0.014(与"光关闭"控制组同值=本底噪声)、卡片中部 16px 带随光接近从 0 平滑升到 0.673/255 再对称回落(**0 次单帧跳变**,单像素最大 5);页面空白处光标点 rgb(15,14,19) → **rgb(21,24,34)**(+6/+10/+15,看得见但不抢内容);`longtask` 计数 **0**。截图 `_verify\flash-after-0*.png` / `glow-visible-1280.png` |
| **卡片内跟随光 + 可点卡片** | **卡片内**另有一道光 `.card::after`(`background: radial-gradient(260px 260px at var(--mx) var(--my), var(--glow-card), transparent 62%)`,`--glow-card: rgba(125,162,255,.10)`;`pointer-events:none`,`opacity` 由 `:hover` 控制 220ms 过渡);`--mx/--my` 由 `site.js` §8b 的 **rAF 节流 pointermove 代理**(document 上一个监听,只写指针所在的那张卡)每帧更新。背景层那道光相应**调淡一档**(`--glow .07 → .035`),免得与卡片内的光打架。**可点卡片**(DOM 实测:没有任何卡片被 `<a>` 整卡包裹,只有下载页两张渠道卡内有 `.btn`)用 `.card:has(.btn):hover` 给反馈:`translate: 0 -2px` + `border-color` 提亮 + 标题转强调色,`:focus-within` 同样提亮边框;**其余卡片保持静态**。**闪白复测**(screencast 无损逐帧,`--disable-gpu` 无头,含控制组):非可点卡(183 帧)顶部 16px 带**最大帧均值 0.008**(仅 1 帧单像素 9),控制组(光隐藏)为 **0**;顶栏带 0.018(= 控制组同值);可点卡(67 帧)顶部带 0.148 **仅出现在进出场各 1 帧且控制组同值**,把 2px 上浮关掉后降到 0.053(纯边框色过渡)—— 即**这道光对顶部带零贡献**,那两帧是用户要的"整卡上浮";顶栏带 **0**;`longtask` 全部 **0** |
| **可点元素 hover 反馈** | 统一只用便宜属性(`color` / `background-color` / `border-color` / `text-decoration` / `opacity` / `translate`),**不扫光、不加发光、不动 `backdrop-filter`、不加阴影**;可选底 `--hover-fill: rgba(255,255,255,.07)`。清单:**导航链接**(非当前页)色变 + 底色(当前页本身即高亮态);**品牌 logo** 文字转强调色 + logo 上移 1px;**正文/提示行链接**下划线与提亮(`--accent-top`);**页脚/备案链接**转强调色 + 下划线;`.btn` 主/幽灵按钮上移 2px,幽灵/图标/复制按钮再加底色 + 边框色;**折叠 `summary`** 转强调色;**章节条 / 页内目录**底色或颜色变化;**禁用按钮 hover 完全无反应**(只留 `cursor: not-allowed`);**不可点的卡片也保持静态**。键盘:`:focus-visible` 全局 2px `--accent` 轮廓(所有可点元素实测均生效)。⚠️ 关于"`.card:hover` 规则 0 条":该约束是 2026-09 为修闪白定的,现在是 **`.card:hover::after`(卡片内那道光)与 `.card:has(.btn):hover`(仅可点卡片)** 两条,卡片**仍然没有** edge/inset 高光、没有扫光、没有阴影变化;强制 `:hover` 实测:普通卡片元素本身 **0 项**变化、`::after` 只 `opacity 0→1`、可点卡片 `translate none→0px -2px` + `border-color .12→.17` + 标题转强调色 |
| **顶栏 logo 动效** | 品牌图标(`assets/img/logo.png`,白底黑标)**不改图片文件**,用 CSS 做「黑↔白来回翻」:`.brand-logo { animation: logoFlip 4.6s var(--ease) infinite }`,`@keyframes logoFlip` 只在 `filter: invert(0↔1)` 与 `transform: perspective(160px) rotateY(0/360/720deg)` 之间切换 —— 4.6s 一轮**只翻两次**、每次约 320ms,其余时间静止(不是高频闪烁;对光敏人群友好);rotateY 停在 360° 的整数倍,**静止时不镜像**。用 `filter` 而非内联 SVG 改 fill 的原因:logo 是**位图 `<img>`**(4 KB PNG),改 fill 需要重做图片资源;`filter: invert()` 零新资源、一行搞定,28px 的元素上 filter+transform 成本可忽略。**实测**:7 个相位(`currentTime` 精确定位)计算样式从 `invert(0)` → `invert(1)` → `invert(0)` 来回,截图 `_verify\logo-anim-*.png`;`prefers-reduced-motion: reduce` 下计算样式 `animation-name: none / filter: none / transform: none`(静止在原色);`longtask` 0;顶栏模糊与滚动不受影响(见 `flash-with-logo.json`:不含 logo 的顶栏带逐帧 Δ 0.019=噪声底)。**注意**:`.brand-logo` 的 hover 提示因此从"上移 1px"改成**边框提亮**为主(动画占着 transform;reduce 下上移依旧生效) |
| **无统计** | 不放任何统计 / 埋点脚本(国内备案网站如需统计要另行申报,先不加) |
| **编码** | 所有文件 **UTF-8**;HTML/CSS/JS 保持**无 BOM**(与仓库习惯一致) |
| **只改这个目录** | 本目录与软件代码互不影响;不要在这里放源码或安装包 |

> ⚠ **不要用 PowerShell 重定向(`>` / `Out-File` / `Set-Content`)写这些文件** —— Windows PowerShell 5.1 会按 ANSI 编码写出,
> 中文会乱码。请用编辑器 / 写文件工具,并确认保存为 UTF-8。

---

## 二、目录结构

```
website/
├── index.html          首页:是什么、给谁用、能做什么、三步开始、隐私摘要、许可摘要、电脑要求
├── download.html       下载:安装包 / 完整版 / 模型包、系统要求、安装步骤、下载慢怎么办、问题反馈入口
├── tutorial.html       使用教程:抠图模型安装、图片放大、AI 抠图、视频处理、音频处理、输出格式
├── faq.html            常见问题:下载慢 / 装不上 / 杀软误报 / 显卡兼容对照表 / 结果不对 / 收费与授权 / 隐私
├── privacy.html        隐私说明:本地处理 + 三处联网行为如实列全 + 本站自身说明
├── licenses.html       开源许可与致谢:软件本体许可 + 第三方程组逐条声明 + 致谢名单
├── changelog.html      更新日志:逐版本摘要(含“需要你重新调参”的提示)
├── assets/
│   ├── css/style.css   全部样式。开头是 §0 实测性能基线与"滚动性能铁律"、§1 设计令牌,
│   │                   改深浅两套主题只改 §1;§20 是 prefers-reduced-motion 的统一关闭规则
│   ├── js/site.js      全部交互,按 §0~§13 分模块的小函数:
│   │                   光斑(进视口才播)/ 章节条高亮与内部跟随 / 吸顶哨兵 / 滚动进入 /
│   │                   标题逐词 / 数字计数 / 卡片跟随高光 / 步骤流点亮 / FAQ 高度过渡 /
│   │                   复制按钮 / 锚点点击跳转。**文件里没有任何 scroll 监听器**
│   └── img/logo.png    站点图标 / 页眉标志(取自软件图标 ALHPro_icon.png;全站唯一图片)
└── README.md           本文件
```

> 页面里的动效钩子(改 HTML 时会用到):
> `data-reveal` = 该区块滚动进入时淡入上移 12px;`data-reveal-group` = 放在容器上,
> 让子元素按 60ms 依次错开(最多 4 级);
> `data-count="0.25" data-dec="2"` = 进入视口时从 0 滚到该值(JS 会按最终文本长度给出固定宽度,
> 避免每帧改数字把整页拖去重排)。**首页已按用户要求不再出现任何速度数字,目前没有元素用它**,
> 机制留着,以后要加数字卡直接用;
> `.demo` = 首页「功能概览」里的循环演示:默认 `animation-play-state: paused`,只有进入视口
> (JS 加 `.is-live`)才播,离开视口立刻暂停;`prefers-reduced-motion: reduce` 下冻结在"前后对比"那一帧。
> **每个演示都必须带文字标签**(`.demo-tag` / `.demo-cap`)—— 纯图形动画用户看不懂,这是实测反馈;
> `main .content` 下的一级 `<ol>` 会自动变成"步骤流"(竖线随滚动点亮),不想如此请加 `class="no-flow"`;
> `.page-progress` 与 `.aurora` 分别由 CSS `animation-timeline: scroll()/view()` 驱动,零 JS。
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
2. **核对 `download.html` 的文件名与大小**是否与发布页一致(标准版附件名以 Release 页实际显示为准)。
3. **确认下载链接可用:** GitHub Releases 页、百度网盘链接与提取码,以及 `models_v1.0.zip` 附件确实存在于某个已发布的 Release 上。
4. **同步版本号。** 版本号散落在 `index.html`、`download.html`、`changelog.html`、`privacy.html`,以及各页 `<title>` / `meta description` 中 ——
   发新版时统一替换(可用编辑器全文查找,例如搜 `1.4.0`)。
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
| `download.html` | 文件大小、版本号;若有新增/改名附件也要改 |
| `index.html` | 首页版本号、下载按钮文案、功能演示的说明文字 |
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
| 首屏数字卡 / FAQ 速度量级 | 作者本机实测(RTX 4060 Laptop)—— 页面已注明机器与“不作为承诺”;首页的“实测速度表”段落按用户要求已删除,不要再加回来 |

作者:AlL.H
