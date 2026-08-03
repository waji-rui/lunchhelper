// animations.js —— LunchHelper 统一组件动画工具（全站复用，杜绝各页面手写重复逻辑）
// 提供：
//   UIAnim.syncList  列表增量同步（进入/离开动画，复用已有节点，原地更新，不重建稳定子树）
//   UIAnim.setText   仅当文本变化才写入，避免无谓重排与动画重播
//   UIAnim.flash     内容刚变化时的“脉冲”提示（如徽标文字改变）
//   UIAnim.enter/leave 单元素进入/离开动画
//   UIAnim.animating 当前是否允许动画（跟随 html.no-anim，由 C# 在 Windows 关闭动画时注入）
// 依赖：无（纯 DOM）。须在任何页面脚本之前加载。
// 设计原则：动画只由 CSS 关键帧/过渡驱动；本模块只负责“何时播放/重播”，不内联任何硬编码动效。
(function () {
  "use strict";
  var root = document.documentElement;

  function animating() {
    return !root.classList.contains("no-anim");
  }

  // 仅当文本确实变化时才写入，避免 innerHTML 式重建导致的动画重播。
  function setText(el, text) {
    if (el && el.textContent !== text) el.textContent = text;
  }

  // 一次性脉冲：移除 class → 强制回流 → 重新添加，使同名 CSS 动画可重复播放。
  // no-anim 时静默（不播动画，但内容已由 setText 更新）。
  function flash(el, cls) {
    if (!el || !animating()) return;
    cls = cls || "ui-flash";
    el.classList.remove(cls);
    void el.offsetWidth; // 强制回流以重启 CSS 动画
    el.classList.add(cls);
  }

  // 进入动画：下一帧移除 enterClass，让 CSS 过渡/关键帧播放一次。
  function enter(el, cls) {
    if (!el || !animating()) return;
    cls = cls || "is-adding";
    el.classList.add(cls);
    requestAnimationFrame(function () { el.classList.remove(cls); });
  }

  // 离开动画：播 leaveClass 后于动画结束(或超时)时移除节点；onDone 可选。
  function leave(el, cls, onDone) {
    if (!el) { if (onDone) onDone(); return; }
    cls = cls || "is-removing";
    var finish = function () {
      if (el.__uiRemoving) return;
      el.__uiRemoving = true;
      el.removeEventListener("animationend", finish);
      if (el.parentNode) el.parentNode.removeChild(el);
      if (onDone) onDone();
    };
    if (!animating()) { finish(); return; }
    el.classList.add(cls);
    el.addEventListener("animationend", finish);
    setTimeout(finish, 450); // 兜底：动画事件未触发也按时移除
  }

  // 增量同步列表：按 data-key（回退 data-plugin-id）匹配复用已有节点。
  //   container : 列表容器
  //   items     : 新数据数组（顺序即最终顺序）
  //   opts.key(item)->string ; opts.render(item)->Element ; opts.update(el,item) ;
  //   opts.onRemove(el,key)? ; opts.enterClass? ; opts.leaveClass? ; opts.emptyHtml?
  // 行为：
  //   - 仍存在项：原地 update(el,item)，不重建，保留其子控件（如开关）的过渡状态；
  //   - 新增项：render 后播进入动画；
  //   - 消失项：播离开动画后删除（动画期间仍留在容器中，避免生硬跳变）。
  function syncList(container, items, opts) {
    if (!container || !opts || !opts.key || !opts.render) return;
    items = items || [];

    // 空列表：一次性清空并显示空态（不逐行播离开动画，避免残影）
    if (items.length === 0) {
      container.innerHTML = opts.emptyHtml || "";
      return;
    }

    var enterClass = opts.enterClass || "is-adding";
    var leaveClass = opts.leaveClass || "is-removing";

    // 1) 收集现有行（按 key 建索引，但保持节点仍在文档中、不 detach）
    var existing = {};
    Array.prototype.forEach.call(container.children, function (el) {
      var k = el.getAttribute("data-key") || el.getAttribute("data-plugin-id");
      if (k) existing[k] = el;
    });

    // 2) 组装顺序：复用/原地更新已有行，新建行播进入动画
    var liveNodes = [];
    items.forEach(function (it) {
      var k = opts.key(it);
      var el = existing[k];
      if (el) {
        if (opts.update) opts.update(el, it);
      } else {
        el = opts.render(it);
        enter(el, enterClass);
      }
      liveNodes.push(el);
    });

    // 3) 原地重排：仅把“位置不对”的节点移动到正确位置（用 insertBefore 而非统一 appendChild），
    //    这样开关 :checked 滑动等正在进行的过渡不会被无谓的 DOM 移动打断；
    //    顺序本就一致时（例如仅切换启禁用）完全不移动任何节点，过渡得以完整保留。
    for (var i = 0; i < liveNodes.length; i++) {
      if (container.children[i] !== liveNodes[i]) {
        container.insertBefore(liveNodes[i], container.children[i] || null);
      }
    }

    // 4) 消失行：仍在文档中，播离开动画后由 leave() 移除（动画正常播放）
    var toRemove = [];
    Array.prototype.forEach.call(container.children, function (el) {
      if (liveNodes.indexOf(el) === -1) toRemove.push(el);
    });
    toRemove.forEach(function (el) {
      if (opts.onRemove) opts.onRemove(el, el.getAttribute("data-key") || el.getAttribute("data-plugin-id"));
      leave(el, leaveClass);
    });
  }

  window.UIAnim = {
    animating: animating,
    setText: setText,
    flash: flash,
    enter: enter,
    leave: leave,
    syncList: syncList
  };
})();
