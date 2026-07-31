// ============================================================
//  MD3Dialog —— 可复用弹窗组件（全局、无第三方依赖）
//
//  用法：
//    MD3Dialog.show({
//      title:   '恢复默认',                 // 可选
//      icon:    'info' | 'warning' | 'error' | '<svg>…</svg>',  // 可选
//      content: '纯文本内容' | domNode,     // 可选；domNode 支持自定义控件
//      buttons: [                           // 默认 [{id:'ok',text:'确定',style:'primary'}]
//        { id: 'ok',     text: '恢复',   style: 'primary' },
//        { id: 'cancel', text: '取消' }
//      ]
//    }).then(function (buttonId) {
//      // buttonId 为被点击按钮的 id；ESC / 点击遮罩 / 关闭返回 null
//    });
//
//  动画：跟随全局 html.no-anim（来自 Windows“显示动画”开关）。
//  无障碍：打开聚焦首个按钮，关闭后焦点归还触发元素；ESC 关闭。
// ============================================================
window.MD3Dialog = (function () {
  'use strict';

  var ICONS = {
    info: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M11 7h2v2h-2zm0 4h2v6h-2zm1-9C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8z"/></svg>',
    warning: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z"/></svg>',
    error: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 2C6.47 2 2 6.47 2 12s4.47 10 10 10 10-4.47 10-10S17.53 2 12 2zm5 13.59L15.59 17 12 13.41 8.41 17 7 15.59 10.59 12 7 8.41 8.41 7 12 10.59 15.59 7 17 8.41 13.41 12 17 15.59 16 1z"/></svg>'
  };

  var current = null;

  function el(tag, cls, html) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (html != null) e.innerHTML = html;
    return e;
  }

  function show(opts) {
    opts = opts || {};
    return new Promise(function (resolve) {
      // 若已有弹窗，先关闭（结果 null），避免叠加
      if (current) current.close(null);

      var backdrop = el('div', 'dlg-backdrop');
      var surface = el('div', 'dlg-surface');
      surface.setAttribute('role', 'alertdialog');
      surface.setAttribute('aria-modal', 'true');

      // ---- header（title / icon 可选） ----
      if (opts.title || opts.icon) {
        var header = el('div', 'dlg-header');
        if (opts.icon) {
          var icon = el('span', 'dlg-icon');
          var svg = (typeof opts.icon === 'string' && ICONS[opts.icon]) ? ICONS[opts.icon] : opts.icon;
          icon.innerHTML = svg || '';
          header.appendChild(icon);
        }
        if (opts.title) {
          var title = el('span', 'dlg-title');
          title.textContent = opts.title;
          header.appendChild(title);
        }
        surface.appendChild(header);
      }

      // ---- content（文本或自定义 DOM） ----
      var content = el('div', 'dlg-content');
      if (opts.content) {
        if (typeof opts.content === 'string') {
          // 若内容含 HTML 标签则渲染，否则当纯文本（安全兜底）
          if (opts.content.indexOf('<') >= 0) content.innerHTML = opts.content;
          else content.textContent = opts.content;
        } else if (opts.content.nodeType) {
          content.appendChild(opts.content);
        }
      }
      surface.appendChild(content);

      // ---- actions ----
      var actions = el('div', 'dlg-actions');
      var btns = opts.buttons || [{ id: 'ok', text: '确定', style: 'primary' }];
      btns.forEach(function (b) {
        var btn = el('button', 'btn' + (b.style === 'primary' ? ' primary' : ''));
        btn.type = 'button';
        btn.textContent = b.text;
        btn.addEventListener('click', function () { dlg.close(b.id); });
        actions.appendChild(btn);
      });
      surface.appendChild(actions);

      backdrop.appendChild(surface);
      document.body.appendChild(backdrop);

      var lastFocus = document.activeElement;

      function onKey(e) {
        if (e.key === 'Escape') { e.preventDefault(); dlg.close(null); }
      }

      var dlg = {
        close: function (result) {
          if (!backdrop) return;
          document.removeEventListener('keydown', onKey, true);
          backdrop.classList.remove('open');
          var b = backdrop;
          backdrop = null;
          current = null;
          var remove = function () { if (b && b.parentNode) b.parentNode.removeChild(b); };
          if (document.documentElement.classList.contains('no-anim')) {
            remove();
          } else {
            b.addEventListener('transitionend', remove, { once: true });
            setTimeout(remove, 400); // 兜底，防 transitionend 未触发
          }
          if (lastFocus && lastFocus.focus) { try { lastFocus.focus(); } catch (x) { } }
          resolve(result);
        }
      };
      current = dlg;

      // 双 rAF 触发进入过渡（确保初始态已渲染）
      requestAnimationFrame(function () {
        requestAnimationFrame(function () { if (backdrop) backdrop.classList.add('open'); });
      });
      var firstBtn = actions.querySelector('button');
      if (firstBtn) firstBtn.focus();
      document.addEventListener('keydown', onKey, true);
    });
  }

  return {
    show: show,
    close: function (r) { if (current) current.close(r); }
  };
})();
