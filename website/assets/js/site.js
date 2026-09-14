/* ============================================================================
   ALH Pro 官网 · 交互脚本
   纯原生 JS,零外部依赖:无统计、无 Cookie、不写本地存储、不发任何请求。

   设计约束(与样式表 §1 的八条硬规则对应)
     · 页面在禁用 JS 时依然完整可读可导航:动效只是增强,不是前提
     · 只做"内容在自己位置上出场"这一类克制动效,不做元素跳舞
     · 高频滚动逻辑合并到一个 requestAnimationFrame 里(先批量读、再批量写)

   模块索引
     §0  环境与工具
     §1  手机端导航折叠
     §2  页内章节条(依据 h2[id] 生成)+ 吸顶状态 + 当前章节高亮
     §3  滚动进入动画:淡入 + 位移 8px、240ms、组内 40ms/级错开,只触发一次
     §4  FAQ / 更新日志 的高度过渡展开收起
     §5  复制按钮(带旧内核兜底)
     §6  平滑锚点跳转 + 焦点管理
   ============================================================================ */
(function () {
  'use strict';

  /* ==========================================================================
     §0 环境与工具
     ========================================================================== */
  var root = document.documentElement;
  var hasIO = 'IntersectionObserver' in window;

  // 标记「JS 可用」。页面 <head> 里已同步加过一次,这里兜底防漏。
  root.classList.add('has-js');

  var mqReduce = window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : null;
  /** 用户是否要求"减少动态效果":每次调用都重新判断,设置改动即时生效 */
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
    on(nav, 'click', function (e) { if (e.target && e.target.tagName === 'A') { setOpen(false); } });
    on(window, 'resize', function () { if (window.innerWidth > 900) { setOpen(false); } });
  }

  /* ==========================================================================
     §2 页内章节条 + 吸顶状态 + 当前章节高亮
     ========================================================================== */

  /** 取 h2 的可读短标签(去掉副标题/日期等次要节点,过长截断) */
  function sectionLabel(h2) {
    var clone = h2.cloneNode(true);
    Array.prototype.forEach.call(clone.querySelectorAll('.small, .muted, .when'), function (n) {
      if (n.parentNode) { n.parentNode.removeChild(n); }
    });
    var text = (clone.textContent || '').replace(/\s+/g, ' ').trim();
    return text.length > 22 ? text.slice(0, 21) + '…' : text;
  }

  /** 依据 <main> 里的 h2 生成吸顶章节条;章节少于 2 个时整条不出现 */
  function buildToc() {
    var tocEl = document.getElementById('page-toc');
    if (!tocEl) { return []; }

    var heads = $('main h2');
    if (heads.length < 2) { tocEl.hidden = true; return []; }

    // 给尚未带 id 的章节补一个稳定 id,锚点才能跳
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
    // 让 CSS 知道"有章节条",从而把锚点跳转的让位高度算上
    root.classList.add('has-toc');
    return items;
  }

  /** 吸顶状态 + 章节高亮:全部收在一个 rAF 里,先读完再写 */
  function initScrollUI(items) {
    var header = $one('.site-header');
    var tocEl = document.getElementById('page-toc');
    var ticking = false;
    var activeLink = null;

    function paint() {
      ticking = false;

      var y = window.pageYOffset || root.scrollTop || 0;
      var vh = window.innerHeight;
      var docH = Math.max(root.scrollHeight, document.body ? document.body.scrollHeight : 0);

      /* ---- 读阶段 ---- */
      var headerH = header ? header.offsetHeight : 0;
      var tocH = (tocEl && !tocEl.hidden) ? tocEl.offsetHeight : 0;

      var active = null;
      var off = headerH + tocH + 26;
      for (var i = 0; i < items.length; i += 1) {
        if (items[i].el.getBoundingClientRect().top <= off) { active = items[i]; } else { break; }
      }
      // 已经贴到底部时锁定最后一节,避免最后一段永远高亮不到
      if (items.length && y + vh >= docH - 6) { active = items[items.length - 1]; }

      /* ---- 写阶段 ---- */
      // 吸顶时只改下边框颜色(见样式表 §4),不做位移与阴影跳变
      if (header) { header.classList.toggle('is-stuck', y > 4); }

      if (active && active.link !== activeLink) {
        if (activeLink) { activeLink.removeAttribute('aria-current'); }
        active.link.setAttribute('aria-current', 'true');
        activeLink = active.link;
        try {
          activeLink.scrollIntoView({ block: 'nearest', inline: 'center', behavior: reduced() ? 'auto' : 'smooth' });
        } catch (err) { /* 老浏览器不支持对象参数,忽略 */ }
      }
    }

    function request() {
      if (ticking) { return; }
      ticking = true;
      window.requestAnimationFrame(paint);
    }

    on(window, 'scroll', request, { passive: true });
    on(window, 'resize', request);
    on(document, 'toggle', request, true);   // 折叠块展开会改变后续章节位置
    request();
  }

  /* ==========================================================================
     §3 滚动进入动画
     淡入 + 上移 8px,240ms;带 data-reveal-group 的容器让子元素按 40ms 依次错开(最多 4 级)。
     每个元素只触发一次,进入视口后即取消观察。
     ========================================================================== */
  function initReveal() {
    var targets = $('[data-reveal]');

    $('[data-reveal-group]').forEach(function (group) {
      Array.prototype.forEach.call(group.children, function (kid, i) {
        kid.style.setProperty('--d', String(i % 4));   // 0 / 40 / 80 / 120ms
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
    }, { rootMargin: '0px 0px -6% 0px', threshold: 0.05 });

    targets.forEach(function (t) { io.observe(t); });
  }

  /* ==========================================================================
     §4 FAQ / 更新日志 的高度过渡展开收起
     原生 <details> 是"啪"一下切换,这里拦下点击改成 height 0 → 实测高度 的过渡;
     动画结束把内联样式清干净,回到浏览器原生行为(不影响打印与无障碍)。
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
    window.setTimeout(end, 560);   // transitionend 万一没来(被中断/不支持)也要收尾
  }

  function initDetails() {
    $('details').forEach(function (details) {
      var summary = $one('summary', details);
      var body = $one('.details-body', details);
      if (!summary || !body) { return; }

      on(summary, 'click', function (ev) {
        if (reduced()) { return; }     // 减少动态效果:交回浏览器原生切换(瞬间完成,无位移)
        ev.preventDefault();

        if (!details.open) {
          details.open = true;
          var target = body.scrollHeight;
          body.classList.add('is-animating');
          body.style.transition = 'none';
          body.style.height = '0px';
          body.style.opacity = '0';
          void body.offsetHeight;      // 强制先应用一次样式,过渡才会发生
          body.style.transition = 'height 240ms ' + EASE + ', opacity 200ms ' + EASE;
          body.style.height = target + 'px';
          body.style.opacity = '1';
          afterHeight(body, function () { clearBodyStyle(body); });
        } else {
          var from = body.offsetHeight;
          body.classList.add('is-animating');
          body.style.transition = 'none';
          body.style.height = from + 'px';
          body.style.opacity = '1';
          void body.offsetHeight;
          body.style.transition = 'height 200ms ' + EASE + ', opacity 200ms ' + EASE;
          body.style.height = '0px';
          body.style.opacity = '0';
          afterHeight(body, function () { clearBodyStyle(body); details.open = false; });
        }
      });
    });
  }

  /* ==========================================================================
     §5 复制按钮(含旧内核兜底)
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
     §6 平滑锚点跳转 + 焦点管理
     滚动交给 CSS 的 scroll-behavior + scroll-padding-top(自动让开吸顶栏),
     这里只补两件事:把地址栏同步成 #hash(不新增历史记录),以及把焦点移到目标章节,
     让键盘 / 读屏用户不会"跳过去了但焦点还留在导航上"。
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
     启动:每个模块独立 try/catch,某一块出问题也不会连累其余功能,
     更不会让"滚动进入动画"没跑起来而把正文永久留在 opacity:0。
     ========================================================================== */
  function boot() {
    function safe(fn) { try { fn(); } catch (err) { /* 静默降级:内容照常可读 */ } }

    safe(initNav);

    // 章节条要先建(需要读 h2 原文)
    var items = [];
    safe(function () { items = buildToc(); });
    safe(function () { initScrollUI(items); });

    safe(initReveal);
    safe(initDetails);
    safe(initCopy);
    safe(initAnchors);

    // 当前页导航高亮兜底(HTML 里已写 aria-current,这里防漏)
    safe(function () {
      var here = location.pathname.split('/').pop() || 'index.html';
      $('.site-nav a[href]').forEach(function (a) {
        if (a.getAttribute('href') === here && !a.hasAttribute('aria-current')) {
          a.setAttribute('aria-current', 'page');
        }
      });
    });

    // 告诉 <head> 里的兜底计时器:脚本已经跑起来了,保留 html.has-js
    root.setAttribute('data-site-ready', '');
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();
