/* ============================================================
   ALH Pro 官网脚本(可选增强,原生 JS,无任何外部依赖)
   作用仅三件:①手机端导航折叠;②复制按钮;③当前页导航高亮兜底
   站点无 JS 也能正常浏览:导航默认展开、所有内容与校验值都可选中复制。
   ============================================================ */
(function () {
  'use strict';

  // 标记「JS 可用」:只有此时才把手机端导航折叠起来(CSS 里用 .has-js 控制)
  document.documentElement.classList.add('has-js');

  document.addEventListener('DOMContentLoaded', function () {

    /* ---------- ① 手机端导航折叠 ---------- */
    var toggle = document.querySelector('.nav-toggle');
    var nav = document.getElementById('site-nav');

    if (toggle && nav) {
      var setOpen = function (open) {
        nav.classList.toggle('is-open', open);
        toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
      };

      toggle.addEventListener('click', function () {
        setOpen(!nav.classList.contains('is-open'));
      });

      document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') { setOpen(false); }
      });

      // 点击导航里的链接后收起(同一页面锚点跳转时更整洁)
      nav.addEventListener('click', function (e) {
        if (e.target && e.target.tagName === 'A') { setOpen(false); }
      });

      // 窗口变宽回到桌面布局时,清掉折叠状态,避免状态残留
      window.addEventListener('resize', function () {
        if (window.innerWidth > 860) { setOpen(false); }
      });
    }

    /* ---------- ② 复制按钮 ---------- */
    var buttons = document.querySelectorAll('[data-copy]');
    Array.prototype.forEach.call(buttons, function (btn) {
      btn.addEventListener('click', function () {
        var text = '';
        var id = btn.getAttribute('data-copy');

        if (id) {
          var el = document.getElementById(id);
          if (el) { text = (el.innerText || el.textContent || '').trim(); }
        } else if (btn.hasAttribute('data-copy-text')) {
          text = btn.getAttribute('data-copy-text');
        }

        if (!text) { return; }

        var done = function (ok) {
          var old = btn.getAttribute('data-label') || btn.textContent;
          btn.setAttribute('data-label', old);
          btn.textContent = ok ? '已复制 ✓' : '请手动选中复制';
          btn.classList.toggle('is-done', ok);
          window.setTimeout(function () {
            btn.textContent = old;
            btn.classList.remove('is-done');
          }, 1800);
        };

        if (navigator.clipboard && navigator.clipboard.writeText) {
          navigator.clipboard.writeText(text).then(function () { done(true); },
            function () { done(legacyCopy(text)); });
        } else {
          done(legacyCopy(text));
        }
      });
    });

    // 旧浏览器兜底:临时 textarea + execCommand
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

    /* ---------- ③ 当前页导航高亮兜底(HTML 里已写 aria-current) ---------- */
    var here = location.pathname.split('/').pop() || 'index.html';
    var links = document.querySelectorAll('.site-nav a[href]');
    Array.prototype.forEach.call(links, function (a) {
      var href = a.getAttribute('href');
      if (href === here && !a.hasAttribute('aria-current')) {
        a.setAttribute('aria-current', 'page');
      }
    });
  });
})();
