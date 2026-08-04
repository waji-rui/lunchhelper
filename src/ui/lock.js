// lock.js —— 锁屏交互逻辑（自包含 WebView2 桥接，不依赖 host-bridge 的全局 window.LunchHelper）
// 安全边界全部在 C#（LockFormWeb）侧：JS 只负责渲染与转发输入，无法单方面解锁。
(function () {
  "use strict";

  var root, lockTime, lockDate, lockSlogan, lockCountdown,
      lockPrompt, lockDisplay, lockDots, keypad, btnAction, btnBack, btnConfirm;
  var code = "";
  var MAX_LEN = 32;
  var locked = false;   // C# 权威锁定（防暴破 5 秒）
  var busy = false;     // 提交校验中（本地防重复点击）

  // ---- 自包含宿主桥接（锁屏不依赖 host-bridge.js 的全局 window.LunchHelper，避免离线文档下的初始化顺序/环境问题） ----
  // 直接走 WebView2 标准通道 window.chrome.webview.postMessage，C# 经 window.__hostResult 按 id 配对回执。
  var _lhPending = {};
  var _lhNextId = 1;
  window.__hostResult = function (res) {
    if (!res || !_lhPending[res.id]) return;
    var p = _lhPending[res.id];
    delete _lhPending[res.id];
    if (res.ok) p.resolve(res.payload);
    else p.reject(new Error(res.error || "host error"));
  };
  function lhReady() { return !!(window.chrome && window.chrome.webview); }
  function lhPost(op, data) {
    return new Promise(function (resolve, reject) {
      var id = _lhNextId++;
      _lhPending[id] = { resolve: resolve, reject: reject };
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(JSON.stringify({ cmd: "host", op: op, id: id, data: data }));
      } else {
        delete _lhPending[id];
        reject(new Error("宿主桥接未就绪"));
      }
    });
  }

  function $(id) { return document.getElementById(id); }

  function ready() {
    root = $("lockRoot");
    lockTime = $("lockTime");
    lockDate = $("lockDate");
    lockSlogan = $("lockSlogan");
    lockCountdown = $("lockCountdown");
    lockPrompt = $("lockPrompt");
    lockDisplay = $("lockDisplay");
    lockDots = $("lockDots");
    keypad = $("keypad");
    btnAction = $("btnAction");
    btnBack = $("btnBack");
    btnConfirm = $("btnConfirm");

    tickClock();
    setInterval(tickClock, 1000);

    keypad.addEventListener("click", onKey);
    btnAction.addEventListener("click", enterPassword);
    btnBack.addEventListener("click", exitPassword);
    btnConfirm.addEventListener("click", submit);

    // 初始配置已在页面加载时由 C# 注入（window.__LOCK_CONFIG__），此处直接应用，
    // 不依赖宿主桥接回合；即使桥接不可用，标语/倒计时/动画开关也已正确显示。
    applyInitialConfig();

    // 通知 C# 就绪（用于二次确认配置/主题，非必需）。桥接不可用时仅记录，不阻塞界面。
    if (lhReady()) {
      lhPost("ready", {}).catch(function (e) {
        if (window.console) console.warn("锁屏 ready 回执失败: ", e);
      });
    } else if (window.console) {
      console.warn("锁屏宿主桥接未就绪：解锁交互可能不可用");
    }
  }

  // 应用 C# 注入的初始配置（标语 / 锁定秒数 / 动画开关）。
  function applyInitialConfig() {
    var c = window.__LOCK_CONFIG__;
    if (!c) return;
    if (c.slogan && lockSlogan) lockSlogan.textContent = c.slogan;
    if (typeof c.lockSeconds === "number" && lockCountdown) {
      lockCountdown.textContent = c.lockSeconds + " 秒后将自动解锁屏幕";
    }
    if (c.animOff && document.documentElement) {
      document.documentElement.classList.add("no-anim");
    }
  }

  function tickClock() {
    var now = new Date();
    var p = function (n) { return n < 10 ? "0" + n : "" + n; };
    if (lockTime) lockTime.textContent = p(now.getHours()) + ":" + p(now.getMinutes()) + ":" + p(now.getSeconds());
    if (lockDate) {
      var wd = ["日", "一", "二", "三", "四", "五", "六"][now.getDay()];
      lockDate.textContent = (now.getMonth() + 1) + "月" + now.getDate() + "日 星期" + wd;
    }
  }

  function enterPassword() {
    if (root.classList.contains("show-password")) return;
    root.classList.add("show-password");
    clearCode();
    // 防暴破锁定中（locked）保留错误提示与倒计时，避免回切锁屏时闪回默认文案破坏连贯性；
    // 仅非锁定态复位为默认提示。
    if (!locked) clearErrorLocal();
  }
  function exitPassword() {
    root.classList.remove("show-password");
    clearCode();
    // 同上：锁定中不要改写提示（主界面隐藏，回切时由 C# 倒计时继续驱动）
    if (!locked) setPrompt("请输入应急解锁密码");
  }

  function onKey(e) {
    var t = e.target;
    if (!t || !t.classList.contains("key")) return;
    if (locked || busy) return;
    var k = t.getAttribute("data-k");
    if (k === "clear") { clearCode(); }
    else if (k === "delete") { code = code.slice(0, -1); renderDots(); }
    else if (k >= "0" && k <= "9") {
      if (code.length < MAX_LEN) { code += k; renderDots(); clearErrorLocal(); }
    }
  }

  function clearCode() { code = ""; renderDots(); }
  function renderDots() {
    if (!lockDots) return;
    lockDots.innerHTML = "";
    for (var i = 0; i < code.length; i++) {
      var d = document.createElement("span");
      d.className = "dot filled";
      lockDots.appendChild(d);
    }
  }

  function setPrompt(text) { if (lockPrompt) lockPrompt.textContent = text; }

  function submit() {
    if (locked || busy) return;
    if (code.length === 0) return;
    busy = true;
    refreshKeypad();                 // 进入“验证中”：禁用键盘并给出反馈
    setPrompt("校验中…");
    if (lockPrompt) lockPrompt.classList.remove("error");
    if (lockDisplay) lockDisplay.classList.remove("error");
    var sent = code;

    // 防御：宿主桥接未就绪时，立即恢复键盘并提示，绝不陷入永久冻结。
    if (!lhReady()) {
      busy = false;
      refreshKeypad();
      setPrompt("解锁功能暂不可用（宿主桥接未就绪）");
      if (lockPrompt) lockPrompt.classList.add("error");
      if (lockDisplay) lockDisplay.classList.add("error");
      return;
    }

    // C# 是锁定/解锁的权威：错误与成功均由 __setLocked / __showError / ExitLock 驱动，
    // 这里仅在“无需锁定的轻量结果”（过短 / 处于防暴破窗口被忽略）下恢复本地状态。
    lhPost("submitCode", { code: sent })
      .then(function (res) {
        if (res && res.short) {       // 过短：静默清空，不冻结
          clearCode();
          busy = false;
          refreshKeypad();
          clearErrorLocal();          // 复位提示（不再停在“校验中…”）
        } else if (res && res.ignored) {
          // 处于防暴破窗口内被忽略：保持忙碌，等 __setLocked(false) 恢复
          busy = false;
          refreshKeypad();
        }
        // wrong / ok：保持 busy，交给 C# 推送驱动（正确时窗体将关闭）
      })
      .catch(function () {
        // 传输层异常（极少）：恢复并可重试，避免卡死
        busy = false;
        refreshKeypad();
        setPrompt("验证失败，请重试");
        if (lockPrompt) lockPrompt.classList.add("error");
        if (lockDisplay) lockDisplay.classList.add("error");
      });
  }

  // 依据 C# 权威状态（locked）与本地“验证中”状态（busy）统一刷新键盘可用性
  function refreshKeypad() { setKeypadDisabled(locked || busy); }

  // 清除本地错误视觉（输入新数字 / 进入密码态 / C# 解除锁定时调用）
  function clearErrorLocal() {
    if (lockPrompt) { lockPrompt.classList.remove("error"); lockPrompt.textContent = "请输入应急解锁密码"; }
    if (lockDisplay) lockDisplay.classList.remove("error");
  }

  function setKeypadDisabled(d) { if (keypad) keypad.classList.toggle("locked", d); }

  // ---- C# → JS 推送入口（由 LockFormWeb 经 ExecuteScriptAsync 调用） ----

  // 初始配置：标语 / 锁定秒数
  window.__applyLockConfig = function (cfg) {
    if (!cfg) return;
    if (cfg.slogan) lockSlogan.textContent = cfg.slogan;
    if (typeof cfg.lockSeconds === "number") {
      lockCountdown.textContent = cfg.lockSeconds + " 秒后将自动解锁屏幕";
    }
  };
  // 倒计时权威推送（C# 每秒下发）
  window.__setRemaining = function (n) {
    if (typeof n !== "number") return;
    lockCountdown.textContent = n + " 秒后将自动解锁屏幕";
  };
  // 防暴破锁定开/关（C# 权威）：锁定期间禁用键盘并显示冻结态；解除时清错误并恢复
  window.__setLocked = function (isLocked) {
    locked = !!isLocked;
    busy = false;                 // C# 解锁信号到来即恢复可输入
    refreshKeypad();
    if (!locked) clearErrorLocal();
  };
  // 输错提示 + 抖动（仅首帧调用）；同时清空已输入密码，避免残留
  window.__showError = function (msg) {
    clearCode();
    setPrompt(msg || "密码错误，请稍后重试");
    if (lockPrompt) lockPrompt.classList.add("error");
    if (lockDisplay) {
      lockDisplay.classList.add("error", "shake");
      setTimeout(function () { if (lockDisplay) lockDisplay.classList.remove("shake"); }, 320);
    }
  };
  // 防暴破倒计时逐秒刷新（仅更新数字，不重复抖动）
  window.__setLockoutRemaining = function (n) {
    if (typeof n !== "number") return;
    setPrompt("密码错误，请" + n + "秒后重试");
    if (lockPrompt) lockPrompt.classList.add("error");
  };
  // 清除错误态（C# 主动调用）
  window.__clearError = function () {
    clearErrorLocal();
  };

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", ready);
  else ready();
})();
