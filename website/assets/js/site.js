/* ============================================================================
   ALH Pro 官网 · 交互动效脚本
   纯原生 JS,零外部依赖:无统计、无 Cookie、不写本地存储、不发任何请求。

   ─────────────────────────────────────────────────────────────────────────
   性能铁律(改这个文件前必读,每条都有实测依据)
     1. **这个文件里没有 scroll 监听器**。滚动期的联动全部交给:
        · CSS 滚动驱动动画(`animation-timeline`,跑在合成器,见 style.css §4/§7)
        · IntersectionObserver(只在"穿越"时回调,不是每帧)
     2. **绝不在滚动驱动的代码路径里调用任何 scroll API**
        (scrollTo / scrollIntoView / scrollTop = …)。
        实测:上一版在滚动处理器里调 `scrollIntoView({behavior:'smooth'})`,
        导致向下滚动时 scrollY 回退 51~63 帧 / 425~525px(用户说的"牵引感");
        去掉后回退帧 = 0。**这是唯一允许出现 scroll API 的地方是 §7 的锚点点击**
        (用户主动点击导航,属于预期的平滑跳转)。
     3. 每帧只允许改 `transform` / `opacity`。指针跟随用 CSS 变量喂给 `transform`,
        不改渐变、不改尺寸、不读会强制重排的属性(rect 读取有缓存,代价极低)。
     4. 入场动画只在元素进入视口时触发一次,随即 `unobserve`。
     5. 光斑这类持续动画在离开视口时**暂停**(`animation-play-state`)并摘掉 `will-change`。

   模块索引
     §0 环境与工具
     §1 手机端导航折叠
     §2 首屏光斑:进视口才跑,离开就暂停
     §3 页内章节条(生成 + 当前章节高亮 + 内部横向跟随,不碰页面滚动)
     §4 吸顶状态:用哨兵元素 + IO,不用 scroll 事件
     §5 滚动进入动画(一次性)
     §6 章节标题逐词浮现
     §7 数字滚动计数
     §8 卡片鼠标跟随光晕
     §9 步骤流逐条点亮
     §10 FAQ / 更新日志 高度过渡
     §11 复制按钮
     §12 锚点点击的平滑跳转 + 焦点管理
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
  function reduced() { return !!(mqReduce && mqReduce.matches); }

  function $(sel, ctx) { return Array.prototype.slice.call((ctx || document).querySelectorAll(sel)); }
  function $one(sel, ctx) { return (ctx || document).querySelector(sel); }
  function on(el, type, fn, opt) { if (el) { el.addEventListener(type, fn, opt || false); } }

  /* ==========================================================================
     §1 手机端导航折叠
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
     §2 首屏光斑:进入视口才播,离开就暂停
     持续跑的动画即使看不见也在吃合成器与显存,这里用 IO 精确开关。
     ========================================================================== */
  function initAurora() {
    var hero = $one('.hero');
    if (!hero || !hasIO) { return; }

    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        // is-live = 加 will-change(有自己一层) + 播放动画;否则暂停并释放
        e.target.classList.toggle('is-live', e.isIntersecting);
      });
    }, { rootMargin: '120px 0px' });

    io.observe(hero);
  }

  /* ==========================================================================
     §3 页内章节条
     生成 + 高亮;横向跟随**只滚动这条内部横向容器**(track.scrollTo),
     不可能影响页面的纵向滚动位置 —— 这是"牵引感"的根因所在,必须守住。
     ========================================================================== */
  function sectionLabel(h2) {
    var clone = h2.cloneNode(true);
    Array.prototype.forEach.call(clone.querySelectorAll('.small, .muted, .when'), function (n) {
      if (n.parentNode) { n.parentNode.removeChild(n); }
    });
    var text = (clone.textContent || '').replace(/\s+/g, ' ').trim();
    return text.length > 22 ? text.slice(0, 21) + '…' : text;
  }

  function initToc() {
    var tocEl = document.getElementById('page-toc');
    if (!tocEl) { return null; }

    var heads = $('main h2');
    if (heads.length < 2) { tocEl.hidden = true; return null; }

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
    root.classList.add('has-toc');

    var current = null;

    function setActive(item) {
      if (!item || item === current) { return; }
      if (current) { current.link.removeAttribute('aria-current'); }
      item.link.setAttribute('aria-current', 'true');
      current = item;

      // —— 横向跟随:只动内部容器,绝不碰页面滚动 ——
      var l = item.link.offsetLeft;
      var w = item.link.offsetWidth;
      var vl = track.scrollLeft;
      var vw = track.clientWidth;
      if (l < vl + 8 || l + w > vl + vw - 8) {
        var target = Math.max(0, l - (vw - w) / 2);
        if (track.scrollTo) {
          track.scrollTo({ left: target, behavior: reduced() ? 'auto' : 'smooth' });
        } else {
          track.scrollLeft = target;
        }
      }
    }

    function pick() {
      // 只在高亮可能变化时调用(IO 穿越回调 / 折叠展开),不是每帧。
      // rect 读数在无写操作时由浏览器缓存,代价极低。
      var headerH = ($one('.site-header') || {}).offsetHeight || 0;
      var tocH = tocEl.offsetHeight || 0;
      var line = headerH + tocH + 28;
      var best = null;
      for (var i = 0; i < items.length; i++) {
        if (items[i].el.getBoundingClientRect().top <= line) { best = items[i]; } else { break; }
      }
      if (!best && current) { return; }
      setActive(best || items[0]);
    }

    if (hasIO) {
      // 用 IO 当"穿越触发器":只在某章节跨过判定线时重算一次,不订阅 scroll
      var probe = new IntersectionObserver(function () { pick(); }, {
        rootMargin: '0px 0px -88% 0px', threshold: 0
      });
      items.forEach(function (it) { probe.observe(it.el); });
      // 页面顶部/底部没有穿越事件时兜底:滚到底时把最后一段点亮
      var last = items[items.length - 1];
      var tailIO = new IntersectionObserver(function (entries) {
        entries.forEach(function (e) { if (e.isIntersecting) { setActive(last); } });
      }, { threshold: 0 });
      var footer = $one('.site-footer');
      if (footer) { tailIO.observe(footer); }
    }

    on(document, 'toggle', pick, true);   // 折叠块展开会改变章节位置
    on(window, 'resize', pick);
    pick();
    return { pick: pick };
  }

  /* ==========================================================================
     §4 吸顶状态:用 1px 哨兵 + IO 取代 scroll 事件
     页面从顶部滚开 1px 就会触发一次穿越回调,之后不再有任何滚动期开销。
     ========================================================================== */
  function initStickyHeader() {
    var header = $one('.site-header');
    if (!header || !hasIO) {
      if (header) { header.classList.add('is-stuck'); }
      return;
    }
    var sentinel = document.createElement('div');
    sentinel.setAttribute('aria-hidden', 'true');
    sentinel.style.cssText = 'position:absolute;top:0;left:0;width:1px;height:1px;pointer-events:none;opacity:0';
    document.body.insertBefore(sentinel, document.body.firstChild);

    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        header.classList.toggle('is-stuck', !e.isIntersecting);
      });
    }, { threshold: 0 });
    io.observe(sentinel);
  }

  /* ==========================================================================
     §5 滚动进入动画:淡入 + 上移 12px(+ 卡片轻微放大),只在进入时触发一次
     ========================================================================== */
  function initReveal() {
    var targets = $('[data-reveal]');

    $('[data-reveal-group]').forEach(function (group) {
      Array.prototype.forEach.call(group.children, function (kid, i) {
        kid.style.setProperty('--d', String(i % 4));   // 0 / 60 / 120 / 180ms
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
        io.unobserve(entry.target);          // 只播一次
      });
    }, { rootMargin: '0px 0px -6% 0px', threshold: 0.05 });

    targets.forEach(function (t) { io.observe(t); });
  }

  /* ==========================================================================
     §6 章节标题逐词浮现
     中文按字、拉丁按词;只处理纯文本节点,含子元素的标题退回整块淡入。
     ========================================================================== */
  var LATIN = /[A-Za-z0-9]/;

  function splitTitle(h2) {
    if (h2.querySelector('*')) { h2.classList.add('t-title-plain'); return; }

    var text = h2.textContent;
    if (!text || !text.trim()) { return; }

    var frag = document.createDocumentFragment();
    var idx = 0, buf = '';

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
        frag.appendChild(document.createTextNode(' '));
        continue;
      }
      if (LATIN.test(ch)) { buf += ch; continue; }
      if (buf) { push(buf); buf = ''; }
      push(ch);
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
    }, { rootMargin: '0px 0px -8% 0px', threshold: 0 });

    titles.forEach(function (h) { io.observe(h); });
  }

  /* ==========================================================================
     §7 数字滚动计数:进入视口时从 0 滚到目标值
     结束时写回 HTML 里的原始文本,保证文案一字不差。

     性能要点(实测数据见下):每帧改文字会让浏览器重新排版,如果计数器在
     **滚动过程中**被触发,就会拖累滚动。这里做了两件事压住成本:
       · 先按最终文本的长度给出固定宽度,再打开 `contain: size`(CSS 里的
         `.is-counting`),让文字变化完全不影响外部布局;
       · 把刷新率从"每个 rAF 帧"降到约 30Hz,写次数少一半。
     消融实验(index.html,同一段滚轮窗口):
       计数开   Layout 284 次 / 55.0ms   Task 537.9ms   Script 11.8ms
       计数关   Layout  47 次 /  5.4ms   Task 392.3ms   Script  1.2ms
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

      // 固定宽度(等宽字体下 ch 就是数字宽度)+ 尺寸隔离 → 改文字不触发外部重排
      el.style.width = finalText.length + 'ch';
      el.classList.add('is-counting');

      var dur = 750;
      var started = 0;
      var lastPaint = -1;
      el.textContent = (0).toFixed(dec);

      window.requestAnimationFrame(function step(now) {
        if (reduced()) { el.textContent = finalText; return; }
        if (!started) { started = now; }
        var p = Math.min(1, (now - started) / dur);
        var eased = 1 - Math.pow(1 - p, 3);              // easeOutCubic
        if (p < 1) {
          if (lastPaint < 0 || now - lastPaint >= 50) {  // 约 20Hz:肉眼已够顺,写次数再砍一半
            el.textContent = (to * eased).toFixed(dec);
            lastPaint = now;
          }
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
    }, { threshold: 0.4 });

    els.forEach(function (el) { io.observe(el); });
  }

  /* ==========================================================================
     §8 卡片鼠标跟随光晕
     只往卡片写 --gx / --gy 两个长度变量,由 CSS 喂给固定尺寸 ::after 的 transform;
     不改渐变、不改尺寸、不触发布局。仅"有真实指针能悬停"的设备安装。
     ========================================================================== */
  function initCardGlow() {
    var mqFine = window.matchMedia ? window.matchMedia('(hover: hover) and (pointer: fine)') : null;
    if (!mqFine || !mqFine.matches || reduced()) { return; }

    $('.card').forEach(function (card) {
      var pending = false, last = null;

      function draw() {
        pending = false;
        if (!last) { return; }
        var r = card.getBoundingClientRect();
        card.style.setProperty('--gx', (last.clientX - r.left) + 'px');
        card.style.setProperty('--gy', (last.clientY - r.top) + 'px');
      }

      on(card, 'pointermove', function (e) {
        last = e;
        if (pending) { return; }
        pending = true;
        window.requestAnimationFrame(draw);        // 一帧最多算一次
      }, { passive: true });

      on(card, 'pointerleave', function () {
        card.style.removeProperty('--gx');
        card.style.removeProperty('--gy');
      });
    });
  }

  /* ==========================================================================
     §9 步骤流逐条点亮
     main .content 下的一级有序列表自动变成步骤流;每条 li 进入视口点亮。
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
    }, { rootMargin: '0px 0px -12% 0px', threshold: 0 });

    steps.forEach(function (li) { io.observe(li); });
  }

  /* ==========================================================================
     §10 FAQ / 更新日志 的高度过渡展开收起
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
    window.setTimeout(end, 620);
  }

  function initDetails() {
    $('details').forEach(function (details) {
      var summary = $one('summary', details);
      var body = $one('.details-body', details);
      if (!summary || !body) { return; }

      on(summary, 'click', function (ev) {
        if (reduced()) { return; }               // 减少动态效果:交回浏览器原生切换
        ev.preventDefault();

        if (!details.open) {
          details.open = true;
          var target = body.scrollHeight;
          body.classList.add('is-animating');
          body.style.transition = 'none';
          body.style.height = '0px';
          body.style.opacity = '0';
          void body.offsetHeight;
          body.style.transition = 'height 300ms ' + EASE + ', opacity 240ms ' + EASE;
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
          body.style.transition = 'height 260ms ' + EASE + ', opacity 240ms ' + EASE;
          body.style.height = '0px';
          body.style.opacity = '0';
          afterHeight(body, function () { clearBodyStyle(body); details.open = false; });
        }
      });
    });
  }

  /* ==========================================================================
     §11 复制按钮(含旧内核兜底)
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
    } catch (err) { return false; }
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
     §12 锚点点击的平滑跳转 + 焦点管理
     这是本文件里**唯一**允许调用页面级滚动 API 的地方 —— 由用户点击触发,
     属于预期的平滑跳转,不会在滚动过程中把页面拽走。
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
     启动:每块独立 try/catch,单块失败不影响其余
     ========================================================================== */
  function boot() {
    function safe(fn) { try { fn(); } catch (err) { /* 静默降级 */ } }

    safe(initNav);
    safe(initStickyHeader);
    safe(initAurora);

    var tocApi = null;
    safe(function () { tocApi = initToc(); });

    safe(initReveal);
    safe(initTitles);
    safe(initCounters);
    safe(initCardGlow);
    safe(initFlow);
    safe(initDetails);
    safe(initCopy);
    safe(initAnchors);

    // 锚点跳转后章节条高亮要跟上(属点击行为,不在滚动路径上)
    safe(function () {
      if (tocApi && tocApi.pick) { window.setTimeout(tocApi.pick, 620); }
    });

    safe(function () {
      var here = location.pathname.split('/').pop() || 'index.html';
      $('.site-nav a[href]').forEach(function (a) {
        if (a.getAttribute('href') === here && !a.hasAttribute('aria-current')) {
          a.setAttribute('aria-current', 'page');
        }
      });
    });

    root.setAttribute('data-site-ready', '');
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();
