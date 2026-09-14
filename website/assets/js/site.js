/* ============================================================================
   ALH Pro 官网 · 交互与动效脚本
   纯原生 JS,零外部依赖,无统计、无 Cookie、不写本地存储。

   设计约束
     · 页面在禁用 JS 时依然完整可读可导航:所有动效都是"增强",不是"前提"
     · 所有位移类动效都先问一遍 prefers-reduced-motion,用户开了"减少动态效果"就只留颜色变化
     · 滚动相关的高频逻辑统一 coalesce 到一个 requestAnimationFrame 里(先批量读、再批量写)

   模块索引
     §0  环境与工具
     §1  手机端导航折叠
     §2  页内章节条(依据 h2[id] 生成)+ 吸顶状态 + 顶部滚动进度条 + 当前章节高亮
     §3  滚动进入动画(IntersectionObserver,淡入 + 上移,组内 0/80/160/240ms 错开,只触发一次)
     §4  章节标题逐词浮现(拆词 + 加 class,动画交给 CSS 过渡)
     §5  数字滚动计数(进入视口时从 0 滚到目标值)
     §6  卡片鼠标跟随光晕(只写 CSS 变量 --mx/--my;触屏设备不安装)
     §7  步骤流:竖线随滚动逐条点亮
     §8  FAQ / 更新日志 的高度过渡展开收起
     §9  复制按钮(带旧浏览器兜底)
     §10 平滑锚点跳转 + 焦点管理
   ============================================================================ */
(function () {
  'use strict';

  /* ==========================================================================
     §0 环境与工具
     ========================================================================== */

  var root = document.documentElement;
  var hasIO = 'IntersectionObserver' in window;

  // 标记「JS 可用」。页内 <head> 已同步加过一次,这里兜底防漏。
  root.classList.add('has-js');

  var mqReduce = window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : null;
  /** 用户是否要求"减少动态效果"：每次调用都重新判断，设置改动即时生效 */
  function reduced() { return !!(mqReduce && mqReduce.matches); }

  function $(sel, ctx) { return Array.prototype.slice.call((ctx || document).querySelectorAll(sel)); }
  function $one(sel, ctx) { return (ctx || document).querySelector(sel); }
  function on(el, type, fn, opt) { if (el) { el.addEventListener(type, fn, opt || false); } }

  /* ==========================================================================
     §1 手机端导航折叠(窄屏把主导航收进汉堡按钮)
     ========================================================================== */
  function initNav() {
    var toggle = $one('.nav-toggle');
    var nav = document.getElementById('site-nav');
    if (!toggle || !nav) { return; }

    function setOpen(open) {
      nav.classList.toggle('is-open', open);
      toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
    }

    on(toggle, 'click', function () { setOpen(!nav.classList.contains('is-open')); });
    on(document, 'keydown', function (e) { if (e.key === 'Escape') { setOpen(false); } });
    // 点了导航里的链接就收起(同页锚点跳转时更整洁)
    on(nav, 'click', function (e) {
      if (e.target && e.target.tagName === 'A') { setOpen(false); }
    });
    // 窗口变宽回到桌面布局时清掉折叠状态，避免状态残留
    on(window, 'resize', function () { if (window.innerWidth > 900) { setOpen(false); } });
  }

  /* ==========================================================================
     §2 页内章节条 + 吸顶导航 + 顶部滚动进度条 + 当前章节高亮
     ========================================================================== */

  /** 取 h2 的可读短标签(去掉副标题/日期等次要节点,过长截断) */
  function sectionLabel(h2) {
    var clone = h2.cloneNode(true);
    // 章节条只放主标题：把版本日期之类的辅助节点摘掉
    Array.prototype.forEach.call(clone.querySelectorAll('.small, .muted, .when'), function (n) {
      if (n.parentNode) { n.parentNode.removeChild(n); }
    });
    var text = (clone.textContent || '').replace(/\s+/g, ' ').trim();
    return text.length > 22 ? text.slice(0, 21) + '…' : text;
  }

  /** 依据 <main> 里的 h2 生成吸顶章节条;没有 h2 或只有一个时整条不出现 */
  function buildToc() {
    var tocEl = document.getElementById('page-toc');
    if (!tocEl) { return []; }

    var heads = $('main h2');
    if (heads.length < 2) { tocEl.hidden = true; return []; }

    // 给尚未带 id 的章节补一个稳定 id，锚点才能跳
    var seq = 0;
    heads.forEach(function (h) {
      if (h.id) { return; }
      var id;
      do { seq += 1; id = 'sec-' + seq; } while (document.getElementById(id));
      h.id = id;
    });

    var track = document.createElement('div');
    track.className = 'page-toc-track';

    var items = heads.map(function (h) {
      var link = document.createElement('a');
      link.href = '#' + h.id;
      link.textContent = sectionLabel(h);
      track.appendChild(link);
      return { el: h, link: link };
    });

    tocEl.appendChild(track);
    tocEl.hidden = false;
    // 让 CSS 知道"有章节条"，从而把锚点跳转的让位高度算上
    root.classList.add('has-toc');
    return items;
  }

  /** 吸顶状态 + 进度条 + 章节高亮：全部收在一个 rAF 里，先读完再写 */
  function initScrollUI(items) {
    var header = $one('.site-header');
    var tocEl = document.getElementById('page-toc');
    var bar = $one('.scroll-progress > i');
    var ticking = false;
    var activeLink = null;

    function paint() {
      ticking = false;

      var y = window.pageYOffset || root.scrollTop || 0;
      var vh = window.innerHeight;
      var docH = Math.max(root.scrollHeight, document.body ? document.body.scrollHeight : 0);

      /* ---- 读阶段(集中读取，避免读写交错造成的布局抖动) ---- */
      var headerH = header ? header.offsetHeight : 0;
      var tocH = (tocEl && !tocEl.hidden) ? tocEl.offsetHeight : 0;

      var active = null;
      var off = headerH + tocH + 26;
      for (var i = 0; i < items.length; i += 1) {
        if (items[i].el.getBoundingClientRect().top <= off) { active = items[i]; } else { break; }
      }
      // 已经贴到底部时锁定最后一节，避免最后一段永远高亮不到
      if (items.length && y + vh >= docH - 6) { active = items[items.length - 1]; }

      /* ---- 写阶段 ---- */
      if (header) { header.classList.toggle('is-stuck', y > 6); }
      // 章节条紧跟在吸顶栏下方，滚过一点点就算"贴住"了(给它加一层细阴影)
      if (tocEl && !tocEl.hidden) { tocEl.classList.toggle('is-stuck', y > headerH + 4); }

      if (bar) {
        var max = docH - vh;
        var p = max > 0 ? Math.min(1, Math.max(0, y / max)) : 0;
        bar.style.transform = 'scaleX(' + p + ')';
      }

      if (active && active.link !== activeLink) {
        if (activeLink) { activeLink.removeAttribute('aria-current'); }
        active.link.setAttribute('aria-current', 'true');
        activeLink = active.link;
        // 让当前章节在当前可见范围内居中（block 用 nearest，不会带动整页纵向滚动）
        try {
          activeLink.scrollIntoView({ block: 'nearest', inline: 'center', behavior: reduced() ? 'auto' : 'smooth' });
        } catch (err) { /* 老浏览器不支持对象参数，忽略即可 */ }
      }
    }

    function request() {
      if (ticking) { return; }
      ticking = true;
      window.requestAnimationFrame(paint);
    }

    on(window, 'scroll', request, { passive: true });
    on(window, 'resize', request);
    // 折叠块展开会改变后续章节的位置，重新算一次即可(每次都实时读取，无需缓存)
    on(document, 'toggle', request, true);
    request();
  }

  /* ==========================================================================
     §3 滚动进入动画
     区块淡入 + 上移 22px；带 data-reveal-group 的容器让子元素按 0/80/160/240ms 依次错开
     每个元素只触发一次，进入视口后即取消观察
     ========================================================================== */
  function initReveal() {
    var targets = $('[data-reveal]');

    $('[data-reveal-group]').forEach(function (group) {
      Array.prototype.forEach.call(group.children, function (kid, i) {
        kid.style.setProperty('--d', String(i % 4));  // 0 / 80 / 160 / 240ms
        targets.push(kid);
      });
    });

    if (!targets.length) { return; }

    if (!hasIO || reduced()) {
      targets.forEach(function (t) { t.classList.add('is-in'); });
      return;
    }

    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) { return; }
        entry.target.classList.add('is-in');
        io.unobserve(entry.target);
      });
    }, { rootMargin: '0px 0px -8% 0px', threshold: 0.05 });

    targets.forEach(function (t) { io.observe(t); });
  }

  /* ==========================================================================
     §4 章节标题逐词浮现
     把标题的纯文本拆成一个个 <span class="wtok">（中文按字、拉丁按词），
     进入视口时给标题加 .is-in，位移与透明度交给 CSS 过渡，每词 26ms 依次亮起
     ========================================================================== */
  var LATIN = /[A-Za-z0-9]/;

  function splitTitle(h2) {
    // 标题里若含子元素(例如更新日志的日期徽标)，不做拆词，退回整块淡入
    if (h2.querySelector('*')) { h2.classList.add('t-title-plain'); return; }

    var text = h2.textContent;
    if (!text || !text.trim()) { return; }

    var frag = document.createDocumentFragment();
    var idx = 0;
    var buf = '';

    function push(token) {
      var s = document.createElement('span');
      s.className = 'wtok';
      s.style.setProperty('--wi', String(idx));
      idx += 1;
      s.textContent = token;
      frag.appendChild(s);
    }

    for (var n = 0; n < text.length; n += 1) {
      var ch = text.charAt(n);
      if (ch === ' ' || ch === '\t' || ch === '\n' || ch === '\u3000') {
        if (buf) { push(buf); buf = ''; }
        frag.appendChild(document.createTextNode(' '));   // 空格不参与动画，保持原排版
        continue;
      }
      if (LATIN.test(ch)) { buf += ch; continue; }        // 连续的字母/数字合成一个词
      if (buf) { push(buf); buf = ''; }
      push(ch);                                           // 中文与标点逐字
    }
    if (buf) { push(buf); }

    h2.textContent = '';
    h2.appendChild(frag);
  }

  function initTitles() {
    var titles = $('main h2');
    if (!titles.length) { return; }

    titles.forEach(splitTitle);

    if (!hasIO || reduced()) {
      titles.forEach(function (h) { h.classList.add('is-in'); });
      return;
    }

    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) { return; }
        entry.target.classList.add('is-in');
        io.unobserve(entry.target);
      });
    }, { rootMargin: '0px 0px -10% 0px', threshold: 0 });

    titles.forEach(function (h) { io.observe(h); });
  }

  /* ==========================================================================
     §5 数字滚动计数
     <span class="count" data-count="0.25" data-dec="2">0.25</span>
     进入视口时从 0 滚到目标值；动画结束后写回 HTML 里的原始文本，保证文案一字不差
     ========================================================================== */
  function initCounters() {
    var els = $('[data-count]');
    if (!els.length) { return; }

    function run(el) {
      var finalText = el.getAttribute('data-final-text') || el.textContent;
      el.setAttribute('data-final-text', finalText);

      var to = parseFloat(el.getAttribute('data-count'));
      var dec = parseInt(el.getAttribute('data-dec') || '0', 10);
      if (isNaN(to)) { return; }

      if (reduced()) { el.textContent = finalText; return; }

      var dur = 1200;
      var started = 0;
      el.textContent = (0).toFixed(dec);

      window.requestAnimationFrame(function step(now) {
        if (reduced()) { el.textContent = finalText; return; }   // 中途切成"减少动态"就直接给结果
        if (!started) { started = now; }
        var p = Math.min(1, (now - started) / dur);
        var eased = 1 - Math.pow(1 - p, 3);                       // easeOutCubic
        if (p < 1) {
          el.textContent = (to * eased).toFixed(dec);
          window.requestAnimationFrame(step);
        } else {
          el.textContent = finalText;
        }
      });
    }

    if (!hasIO) { els.forEach(run); return; }

    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) { return; }
        run(entry.target);
        io.unobserve(entry.target);
      });
    }, { threshold: 0.35 });

    els.forEach(function (el) { io.observe(el); });
  }

  /* ==========================================================================
     §6 卡片鼠标跟随光晕
     只在"有真实指针且能悬停"的设备上安装(触屏不生效)；
     每帧最多算一次，只往卡片写 --mx / --my 两个 CSS 变量，实际高光由 CSS 画
     ========================================================================== */
  function initCardGlow() {
    var mqFine = window.matchMedia ? window.matchMedia('(hover: hover) and (pointer: fine)') : null;
    if (!mqFine || !mqFine.matches || reduced()) { return; }

    $('.card').forEach(function (card) {
      var pending = false;
      var last = null;

      function draw() {
        pending = false;
        if (!last) { return; }
        var r = card.getBoundingClientRect();
        if (!r.width || !r.height) { return; }
        card.style.setProperty('--mx', (((last.clientX - r.left) / r.width) * 100).toFixed(1) + '%');
        card.style.setProperty('--my', (((last.clientY - r.top) / r.height) * 100).toFixed(1) + '%');
      }

      on(card, 'pointermove', function (e) {
        last = e;
        if (pending) { return; }
        pending = true;
        window.requestAnimationFrame(draw);
      }, { passive: true });

      on(card, 'pointerleave', function () {
        card.style.removeProperty('--mx');
        card.style.removeProperty('--my');
      });
    });
  }

  /* ==========================================================================
     §7 步骤流：竖线随滚动逐条点亮
     给正文里的一级有序列表加 .flow，每条 li 进入视口时加 .is-lit，
     序号节点变成渐变圆点、连接线从上往下生长
     ========================================================================== */
  function initFlow() {
    var lists = $('main .content > ol');
    if (!lists.length) { return; }

    var steps = [];
    lists.forEach(function (ol) {
      if (ol.classList.contains('no-flow')) { return; }
      ol.classList.add('flow');
      Array.prototype.forEach.call(ol.children, function (li) {
        if (li.tagName === 'LI') { steps.push(li); }
      });
    });

    if (!steps.length) { return; }

    if (!hasIO || reduced()) {
      steps.forEach(function (li) { li.classList.add('is-lit'); });
      return;
    }

    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) { return; }
        entry.target.classList.add('is-lit');
        io.unobserve(entry.target);
      });
    }, { rootMargin: '0px 0px -14% 0px', threshold: 0 });

    steps.forEach(function (li) { io.observe(li); });
  }

  /* ==========================================================================
     §8 FAQ / 更新日志 的高度过渡展开收起
     原生 <details> 是"啪"一下切换，这里拦下点击改成 height 0 → 实测高度 的过渡；
     动画结束把内联样式清干净，回到浏览器原生行为(不会影响打印与无障碍)
     ========================================================================== */
  var EASE = 'cubic-bezier(.22,.61,.36,1)';

  function clearBodyStyle(body) {
    body.classList.remove('is-animating');
    body.style.height = '';
    body.style.opacity = '';
    body.style.transition = '';
  }

  function afterHeight(body, done) {
    var finished = false;
    function end() {
      if (finished) { return; }
      finished = true;
      body.removeEventListener('transitionend', onEnd);
      done();
    }
    function onEnd(e) { if (!e || e.propertyName === 'height') { end(); } }
    body.addEventListener('transitionend', onEnd);
    window.setTimeout(end, 620);   // transitionend 万一没来(被中断/不支持)也要收尾
  }

  function initDetails() {
    $('details').forEach(function (details) {
      var summary = $one('summary', details);
      var body = $one('.details-body', details);
      if (!summary || !body) { return; }

      on(summary, 'click', function (ev) {
        if (reduced()) { return; }        // 减少动态效果：交回浏览器原生切换(瞬间完成，无位移)
        ev.preventDefault();

        if (!details.open) {
          // —— 展开 ——
          details.open = true;
          var target = body.scrollHeight;
          body.classList.add('is-animating');
          body.style.transition = 'none';
          body.style.height = '0px';
          body.style.opacity = '0';
          void body.offsetHeight;                                  // 强制先应用一次样式，过渡才会发生
          body.style.transition = 'height 380ms ' + EASE + ', opacity 300ms ' + EASE;
          body.style.height = target + 'px';
          body.style.opacity = '1';
          afterHeight(body, function () { clearBodyStyle(body); });
        } else {
          // —— 收起 ——
          var from = body.offsetHeight;
          body.classList.add('is-animating');
          body.style.transition = 'none';
          body.style.height = from + 'px';
          body.style.opacity = '1';
          void body.offsetHeight;
          body.style.transition = 'height 320ms ' + EASE + ', opacity 260ms ' + EASE;
          body.style.height = '0px';
          body.style.opacity = '0';
          afterHeight(body, function () { clearBodyStyle(body); details.open = false; });
        }
      });
    });
  }

  /* ==========================================================================
     §9 复制按钮(含旧内核兜底)
     ========================================================================== */
  function legacyCopy(text) {
    try {
      var ta = document.createElement('textarea');
      ta.value = text;
      ta.setAttribute('readonly', 'readonly');
      ta.style.position = 'fixed';
      ta.style.top = '-1000px';
      document.body.appendChild(ta);
      ta.select();
      var ok = document.execCommand('copy');
      document.body.removeChild(ta);
      return ok;
    } catch (err) {
      return false;
    }
  }

  function initCopy() {
    $('[data-copy]').forEach(function (btn) {
      on(btn, 'click', function () {
        var text = '';
        var id = btn.getAttribute('data-copy');

        if (id) {
          var el = document.getElementById(id);
          if (el) { text = (el.innerText || el.textContent || '').trim(); }
        } else if (btn.hasAttribute('data-copy-text')) {
          text = btn.getAttribute('data-copy-text');
        }
        if (!text) { return; }

        function done(ok) {
          var old = btn.getAttribute('data-label') || btn.textContent;
          btn.setAttribute('data-label', old);
          btn.textContent = ok ? '已复制 ✓' : '请手动选中复制';
          btn.classList.toggle('is-done', ok);
          window.setTimeout(function () {
            btn.textContent = old;
            btn.classList.remove('is-done');
          }, 1800);
        }

        if (navigator.clipboard && navigator.clipboard.writeText) {
          navigator.clipboard.writeText(text).then(
            function () { done(true); },
            function () { done(legacyCopy(text)); }
          );
        } else {
          done(legacyCopy(text));
        }
      });
    });
  }

  /* ==========================================================================
     §10 平滑锚点跳转 + 焦点管理
     滚动本身交给 CSS 的 scroll-behavior + scroll-padding-top(自动让开吸顶栏)，
     这里只补两件事：把地址栏同步成 #hash(不新增历史记录)，以及把焦点移到目标章节，
     让键盘 / 读屏用户不会"跳过去了但焦点还留在导航上"
     ========================================================================== */
  function initAnchors() {
    on(document, 'click', function (ev) {
      var target = ev.target;
      if (!target || !target.closest) { return; }
      var a = target.closest('a[href^="#"]');
      if (!a) { return; }

      var href = a.getAttribute('href');
      if (!href || href === '#' || href.length < 2) { return; }

      var id;
      try { id = decodeURIComponent(href.slice(1)); } catch (err) { id = href.slice(1); }
      var el = document.getElementById(id);
      if (!el) { return; }

      ev.preventDefault();
      el.scrollIntoView({ behavior: reduced() ? 'auto' : 'smooth', block: 'start' });

      if (!el.hasAttribute('tabindex')) { el.setAttribute('tabindex', '-1'); }
      try { el.focus({ preventScroll: true }); } catch (err) { el.focus(); }

      if (window.history && window.history.replaceState) {
        window.history.replaceState(null, '', href);
      }
    });
  }

  /* ==========================================================================
     启动
     ========================================================================== */
  function boot() {
    // 每个模块独立 try/catch:某一块出问题也不会连累其余动效,
    // 更不会让「滚动进入动画」没跑起来而把正文永久留在 opacity:0
    function safe(name, fn) {
      try { fn(); } catch (err) { /* 静默降级：内容照常可读 */ }
    }

    safe('nav', initNav);

    // 章节条必须先建(要读 h2 原文)，再做标题拆词(拆完文字就变成一个个 span 了)
    var items = [];
    safe('toc', function () { items = buildToc(); });
    safe('scrollUI', function () { initScrollUI(items); });
    safe('titles', initTitles);

    safe('reveal', initReveal);
    safe('counters', initCounters);
    safe('cardGlow', initCardGlow);
    safe('flow', initFlow);
    safe('details', initDetails);
    safe('copy', initCopy);
    safe('anchors', initAnchors);

    // 当前页导航高亮兜底(HTML 里已经写了 aria-current，这里防漏)
    safe('navCurrent', function () {
      var here = location.pathname.split('/').pop() || 'index.html';
      $('.site-nav a[href]').forEach(function (a) {
        if (a.getAttribute('href') === here && !a.hasAttribute('aria-current')) {
          a.setAttribute('aria-current', 'page');
        }
      });
    });

    // 告诉 <head> 里的兜底计时器：脚本已经跑起来了，保留 html.has-js
    root.setAttribute('data-site-ready', '');
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();
