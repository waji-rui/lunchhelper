// host-bridge.js —— LunchHelper 宿主桥接层（前端 ↔ C#）
// 暴露 window.LunchHelper.host.* 稳定 API；插件/页面只需调用这些，无需了解 postMessage 细节。
// 依赖：MD3Dialog（ui/dialog.js，须先于本文件加载）。
(function () {
  "use strict";
  // 防止被重复注入（WebView2 重导航或多次加载时）
  if (window.LunchHelper && window.LunchHelper.host) return;

  var pending = new Map();   // id -> { resolve, reject }
  var nextId = 1;

  function send(msg) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(JSON.stringify(msg));
    }
  }

  // C# 回执入口：res = { id, ok, payload | error }
  window.__hostResult = function (res) {
    if (!res || !pending.has(res.id)) return;
    var p = pending.get(res.id);
    pending.delete(res.id);
    if (res.ok) p.resolve(res.payload);
    else p.reject(new Error(res.error || "host error"));
  };

  // 前端 → C# 请求，返回 Promise（消息 id 配对回执，避免多请求串扰）
  function postAsync(op, data) {
    return new Promise(function (resolve, reject) {
      var id = nextId++;
      pending.set(id, { resolve: resolve, reject: reject });
      send({ cmd: "host", op: op, id: id, data: data });
    });
  }

  // C# 主动弹窗入口：C# 通过 ExecuteScriptAsync("window.__showHostDialog(<optsJson>)") 调用。
  // 弹窗结果回传 C#（cmd:"dialogResult"），使 C# 也能完整驱动 Modal 流程，而不只是被动响应。
  window.__showHostDialog = function (opts) {
    if (!window.MD3Dialog) { console.error("MD3Dialog 未加载，无法弹窗"); return; }
    MD3Dialog.show(opts || {}).then(function (buttonId) {
      send({ cmd: "dialogResult", data: { buttonId: buttonId } });
    });
  };

  // 稳定 API：插件只用这些，不碰窗口外壳与 postMessage
  window.LunchHelper = {
    host: {
      // 读取当前完整配置（C# 返回的配置对象）
      getConfig: function () { return postAsync("getConfig", null); },
      // 写入配置（字段同 gather() 输出：lockSeconds/logRetentionDays/slogan/enableUiAccess/password）
      setConfig: function (obj) { return postAsync("setConfig", obj || {}); },
      // 是否跟随 Windows“显示动画”系统设置
      areAnimationsEnabled: function () { return postAsync("areAnimationsEnabled", null); },
      // 切换配置页（纯前端导航，经 host 暴露以统一插件 API 形态；无需 C# 往返）
      navigate: function (pageId) {
        if (typeof showPage === "function") showPage(pageId);
        return Promise.resolve(true);
      },
      // 插件侧弹窗：直接复用 MD3Dialog，返回 Promise<buttonId>；取消/ESC 关闭返回 null
      showDialog: function (opts) {
        if (!window.MD3Dialog) return Promise.reject(new Error("MD3Dialog 未加载"));
        return MD3Dialog.show(opts || {});
      },
      // 插件管理：列出已安装（含启禁状态）
      listPlugins: function () { return postAsync("listPlugins", null); },
      // 插件详情（含 README Markdown 与图标），用于三栏布局右侧详情面板
      getPluginDetails: function (id) { return postAsync("getPluginDetails", { pluginId: id }); },
      // 启用/禁用插件（写 .disabled，下次启动才真正生效）
      setPluginEnabled: function (id, enabled) { return postAsync("setPluginEnabled", { pluginId: id, enabled: enabled }); },
      // 卸载插件（写 .uninstall 标记，重启才真正删除）
      uninstallPlugin: function (id) { return postAsync("uninstallPlugin", { pluginId: id }); },
      // 撤销卸载（删除 .uninstall 标记，重启前可撤销）
      cancelUninstall: function (id) { return postAsync("cancelUninstall", { pluginId: id }); },
      // 强制删除（立即删除目录；若被占用则回退为延迟卸载）
      forceDeletePlugin: function (id) { return postAsync("forceDeletePlugin", { pluginId: id }); },
      // 立即重启应用程序（用相同参数启动新进程后退出当前进程）
      restartApp: function () { return postAsync("restartApp", null); },
      // 立即锁屏（配置页测试按钮）：C# 启动锁屏窗体用于预览效果，不拉守护进程
      lockNow: function () { return postAsync("lockNow", null); },
      // 打开插件根目录（资源管理器）
      openPluginsFolder: function () { return postAsync("openPluginsFolder", null); },
      // 打开指定插件目录
      openPluginFolder: function (id) { return postAsync("openPluginFolder", { pluginId: id }); },
      // 启动本地 zip 安装（返回确认预览）
      installPlugin: function (id, zipData) { return postAsync("installPlugin", { pluginId: id, zipData: zipData }); },
      // 确认安装（真正解压）
      confirmInstall: function (id) { return postAsync("confirmInstall", { installId: id }); },
      // 取消安装（清除待确认会话）
      cancelInstall: function (id) { return postAsync("cancelInstall", { installId: id }); }
    }
  };
})();
