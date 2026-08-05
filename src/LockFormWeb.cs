// LunchHelper — 触控锁屏助手
// Copyright (C) 2026  LunchHelper Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LunchHelper
{
    /// <summary>
    /// 锁屏主窗体（WebView2 渲染版，控制模式）。
    /// 视觉复用配置页同一套 MD3 暗色公共样式（tokens/components/lock.css + 动态强调色），
    /// 与配置页 100% 同源同色；安全逻辑与原有 LockForm 完全一致：
    ///   - 全屏覆盖所有显示器（union）；
    ///   - 倒计时归零无论何种状态都自动解锁（解锁安全网，独立于 WebView2 是否就绪）；
    ///   - AntiTamper 全局低级键盘钩子（拦截 Win/Alt/Ctrl 组合）照常生效；
    ///   - 应急密码经后台 PBKDF2 校验，绝不接触 JS；错误后防暴破锁定 5 秒（C# 权威）；
    ///   - 守护进程定时拉起；ExitLock 写解锁标记 + AntiTamper.Cleanup + Close。
    /// WebView2 不可用时（运行时缺失 / 目录不可写）由 Program.cs 降级为原生 LockForm。
    /// </summary>
    internal class LockFormWeb : Form
    {
        private readonly bool _debug;
        private int _guardianPid;
        private Config _cfg;

        private int _remaining;
        private int _lockoutRemaining;   // 防暴破倒计时剩余秒数（C# 权威，每秒一拍）
        private bool _exiting;
        private bool _locked;          // 防暴破锁定期（C# 权威）

        private Timer _mainTimer;
        private Timer _lockoutTimer;
        private Timer _guardianTimer;

        private WebView2 _web;
        private bool _aborted;    // WebView2 已放弃：保留深色窗体由倒计时解锁，不降级原生锁屏

        private Timer _renderWatchdog;   // 渲染看门狗：导航后未收到 ready 回执则放弃原生降级（覆盖提权下 WebView2 静默空白）
        private bool _readyReceived;     // 锁屏页已通过宿主桥接回执 ready（说明 WebView2 真正跑起来了）

        // 防暴破锁定时长（秒）：错误密码后键盘冻结该时长，期间忽略提交。
        private const int LockoutSeconds = 5;

        public LockFormWeb(bool debug, int guardianPid)
        {
            _debug = debug;
            _guardianPid = guardianPid;
            InitializeComponent();
        }

        /// <summary>探测本机能否用 WebView2 承载锁屏（运行时存在且 exe 目录可写）。</summary>
        internal static bool IsWebView2Available() => WebUiShell.IsWebView2Available();

        private void InitializeComponent()
        {
            this.BackColor = Color.FromArgb(0x1C, 0x1B, 0x1F);   // surface 暗色底色，WebView2 就绪前即深色，无白闪
            this.ForeColor = Color.White;
            this.FormBorderStyle = FormBorderStyle.None;
            // TopMost 必须推迟到 WebView2 控制器创建成功后再置位（见 OnWebViewInit / GiveUpGraceful）。
            // 在 TopMost（WS_EX_TOPMOST）父窗口上创建 WebView2 控制器会触发
            // CreateCoreWebView2ControllerAsync 返回 E_INVALIDARG（"值不在预期的范围内"），
            // 导致锁屏 WebView2 永远初始化失败；配置页（非 TopMost）正常即印证此点。
            this.TopMost = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.Manual;
            this.WindowState = FormWindowState.Normal;
            // 不再于 Activated 里重复置 TopMost=true：激活事件可能在控制器创建前触发，
            // 会提前把窗口变成 TopMost 而破坏初始化。

            _web = new WebView2
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(0x1C, 0x1B, 0x1F),
                // 与配置页一致：UserDataFolder 指向 LocalAppData（始终可写），不附加自定义浏览器参数。
                CreationProperties = new CoreWebView2CreationProperties
                {
                    // 独立 UserDataFolder：必须与配置页(exe 目录)不同，否则同进程内两个 WebView2
                    // 共用同一 UDF 会导致第二个初始化失败（官方文档确认：同 UDF 多实例需各自独立目录）。
                    UserDataFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "LunchHelper", "LockWebView2")
                }
            };
            _web.CoreWebView2InitializationCompleted += OnWebViewInit;
            Controls.Add(_web);
            // 注意：InitializeWebView 推迟到 OnHandleCreated（窗体尺寸确定后）再调用，
            // 避免「先以小尺寸初始化、后再放大到全屏」导致 WebView2 合成层不刷新而空白。
        }

        private async Task InitializeWebView()
        {
            Logger.Debug("InitializeWebView 开始");
            if (!WebUiShell.IsWebView2Available())
            {
                Logger.Warn("WebView2 不可用，放弃原生降级（保留深色窗体由倒计时解锁）");
                GiveUpGraceful();
                return;
            }
            try
            {
                string udf = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LunchHelper", "LockWebView2");
                Logger.Info("WebView2 UserDataFolder: " + udf);
                // 诊断：与可用的配置页保持一致，使用默认环境（不附加 --no-sandbox/--disable-gpu 等自定义参数），
                // 以判定「提权/非 Shell 启动」下的空白是否由自定义浏览器参数导致；UserDataFolder 已由
                // CreationProperties 指定到 LocalAppData（始终可写）。
                Logger.Debug("锁屏 WebView2 使用默认环境（与配置页一致），不附加自定义浏览器参数");
                LogWebState("初始化前");

                // 渲染看门狗：无论 EnsureCoreWebView2Async 卡死还是页面静默空白（ready 永不送达），
                // 超时即优雅放弃（保留深色窗体，不降级原生锁屏），杜绝永久黑屏。
                _renderWatchdog = new Timer { Interval = 10000 };
                _renderWatchdog.Tick += OnRenderWatchdog;
                _renderWatchdog.Start();

                await _web.EnsureCoreWebView2Async(null);
                Logger.Info("WebView2 环境创建成功，浏览器版本: " + (_web.CoreWebView2?.Environment?.BrowserVersionString ?? "?"));
            }
            catch (Exception ex)
            {
                Logger.Error("WebView2 初始化失败，放弃原生降级: " + DescribeException(ex));
                Logger.Debug("WebView2 初始化异常堆栈:\n" + ex.StackTrace);
                GiveUpGraceful();
            }
        }

        /// <summary>
        /// 渲染看门狗：若锁屏页在限定时间内未通过宿主桥接回执 ready（说明 WebView2 未真正渲染，
        /// 常见于提权/非交互桌面下 GPU 进程失败致空白），则优雅放弃——保留深色窗体、不降级原生锁屏。
        /// </summary>
        private void OnRenderWatchdog(object sender, EventArgs e)
        {
            try { _renderWatchdog?.Stop(); } catch { }
            if (_readyReceived || _aborted || IsDisposed) return;
            Logger.Error("锁屏页未在限定时间内就绪（WebView2 可能未渲染），保留深色窗体（不降级原生）");
            GiveUpGraceful();
        }

        private void OnWebViewInit(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                Logger.Error("WebView2 初始化失败: " + DescribeException(e.InitializationException));
                if (e.InitializationException != null)
                    Logger.Debug("WebView2 初始化异常堆栈:\n" + e.InitializationException.StackTrace);
                GiveUpGraceful();
                return;
            }
            if (_aborted) return;   // 已在其它路径放弃，避免访问已释放的 CoreWebView2
            try
            {
                this.TopMost = true;   // 控制器已创建成功，此时再置顶不再影响初始化
                var settings = _web.CoreWebView2.Settings;
                settings.AreDefaultContextMenusEnabled = false;   // 触控界面更干净
                settings.AreDevToolsEnabled = _debug;
                settings.IsZoomControlEnabled = false;
                settings.IsWebMessageEnabled = true;              // 启用 window.chrome.webview 消息通道（解锁交互依赖）

                // 注意：本页由 NavigateToString 加载离线 HTML，页面内无任何可触发外部导航的元素
                // （无超链接、无表单提交、无 window.open），不存在 JS「跳走」风险。
                // 因此【不】挂 NavigationStarting 取消过滤器——该过滤器在部分 WebView2 构建下
                // 会把 NavigateToString 的文档 URI（空串或 data:）误判为外部协议而取消导航，
                // 导致黑屏或宿主桥接消息通道异常。去掉它反而更稳妥。

                _web.CoreWebView2.WebMessageReceived += OnWebMessage;
                _web.CoreWebView2.ProcessFailed += (s, pf) =>
                {
                    Logger.Error("WebView2 进程异常: " + pf.ProcessFailedKind);
                    GiveUpGraceful();
                };
                _web.CoreWebView2.NavigationCompleted += (s, nc) =>
                {
                    Logger.Debug("锁屏页导航完成: IsSuccess=" + nc.IsSuccess
                        + (nc.IsSuccess ? "" : ", WebErrorStatus=" + nc.WebErrorStatus));
                };
                _web.CoreWebView2.NavigateToString(LoadHtml());
                LogWebState("OnWebViewInit-导航前");
                Logger.Debug("锁屏页 NavigateToString 已提交，等待 ready 回执");
                try { _web.Focus(); } catch { }   // 确保物理键盘事件进入网页文档（无需先点一下）
            }
            catch (Exception ex)
            {
                Logger.Error("WebView2 加载锁屏页失败，放弃原生降级: " + ex.Message);
                GiveUpGraceful();
            }
        }

        /// <summary>
        /// WebView2 渲染不可用时的优雅放弃：保留深色窗体（不降级原生锁屏，原生 WinForm 视觉不符需求），
        /// 由主倒计时自动解锁。保留 AntiTamper 继续拦截系统组合键；仅停掉守护/防暴破/看门狗计时器。
        /// </summary>
        private void GiveUpGraceful()
        {
            if (_aborted) return;
            _aborted = true;
            try { this.TopMost = true; } catch { }   // 即使 WebView2 不可用，深色锁屏窗体仍须置顶以起到锁屏作用
            Logger.Error("锁屏 WebView2 不可用，保留深色窗体（不降级原生），由倒计时自动解锁");
            try { _guardianTimer?.Stop(); } catch { }
            try { _lockoutTimer?.Stop(); } catch { }
            try { _renderWatchdog?.Stop(); } catch { }
            // 注意：不清理 AntiTamper（保持键盘拦截），不关闭窗体（保留深色界面），主倒计时仍驱动自动解锁。
        }

        /// <summary>组合锁屏离线 HTML：注入公共 CSS / 动画 / 桥接 / 锁屏逻辑 + C# 动态强调色 + 初始配置。</summary>
        private string LoadHtml()
        {
            var asm = Assembly.GetExecutingAssembly();
            string html = WebUiShell.ReadEmbedded(asm, "LockPage.html");
            if (html == null) return WebUiShell.FallbackHtml();
            string tokens = WebUiShell.ReadEmbedded(asm, "ui.tokens.css") ?? "";
            string comps = WebUiShell.ReadEmbedded(asm, "ui.components.css") ?? "";
            string lockCss = WebUiShell.ReadEmbedded(asm, "ui.lock.css") ?? "";
            string animJs = WebUiShell.ReadEmbedded(asm, "ui.animations.js") ?? "";
            string lockJs = WebUiShell.ReadEmbedded(asm, "ui.lock.js") ?? "";

            // 初始配置：标语 / 锁定秒数 / 动画开关（跟随 Windows“显示动画”）。
            // 直接写进页面，加载即可见，不依赖「JS 发 ready → C# 回推」回合；
            // 即便宿主桥接因任何原因未就绪，标语/倒计时/主题也不会回退到默认文案。
            var cfg = ConfigManager.Load();
            var cfgSb = new StringBuilder();
            cfgSb.Append("window.__LOCK_CONFIG__={");
            cfgSb.Append("\"slogan\":").Append(WebUiShell.JsonString(string.IsNullOrWhiteSpace(cfg.Slogan) ? "设备已锁定" : cfg.Slogan));
            cfgSb.Append(",\"lockSeconds\":").Append(cfg.LockSeconds);
            cfgSb.Append(",\"animOff\":").Append(NativeMethods.AreAnimationsEnabled() ? "false" : "true");
            cfgSb.Append("};");

            html = html.Replace("/*CSS_TOKENS*/", tokens)
                       .Replace("/*CSS_COMPONENTS*/", comps)
                       .Replace("/*CSS_LOCK*/", lockCss)
                       .Replace("/*JS_ANIM*/", animJs)
                       .Replace("/*JS_LOCK*/", lockJs)
                       // BuildMd3ThemeStyle 返回完整 <style id="md3-theme">:root{…}</style>，
                       // 直接注入（LockPage.html 此处已不留外层 <style> 包裹，避免嵌套 <style> 破坏解析）。
                       .Replace("/*MD3_THEME*/", WebUiShell.BuildMd3ThemeStyle())
                       .Replace("/*LOCK_CONFIG*/", cfgSb.ToString());
            return html;
        }

        // ---- JS ↔ C# 桥接 ----

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (IsDisposed) return;
            string raw = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(raw)) return;
            Logger.Info("锁屏 WebView2 收到消息: " + raw);
            WebUiShell.LockMsg msg;
            try { msg = WebUiShell.Deserialize<WebUiShell.LockMsg>(raw); }
            catch { return; }
            if (msg == null || msg.Cmd != "host") return;
            HandleLockHost(msg);
        }

        private void HandleLockHost(WebUiShell.LockMsg msg)
        {
            if (msg == null) return;
            switch (msg.Op)
            {
                case "ready":
                    _readyReceived = true;
                    try { _renderWatchdog?.Stop(); } catch { }
                    LogWebState("ready");
                    Logger.Debug("锁屏页 ready 回执，WebView2 已确认渲染");
                    ForceWebPresent();           // 首轮兜底重绘：规避提权/全屏 TopMost 下合成层不刷新导致空白
                    PushConfig();
                    // 二次兜底：稍后再触发一次，规避首轮过早、合成层尚未来得及刷新的极端情况
                    Task.Delay(400).ContinueWith(_ =>
                    {
                        try { this.BeginInvoke(new Action(ForceWebPresent)); } catch { }
                    });
                    break;
                case "submitCode":
                    HandleSubmit(msg.Data != null ? (msg.Data.Code ?? "") : "", msg.Id);
                    break;
                default:
                    WebUiShell.SendHostResult(_web?.CoreWebView2, msg.Id, false, null, "未知锁屏操作: " + (msg.Op ?? ""));
                    break;
            }
        }

        /// <summary>向 JS 下发初始配置（标语 / 锁定秒数）并同步系统动画开关。</summary>
        private void PushConfig()
        {
            if (_web?.CoreWebView2 == null) return;
            var cfg = ConfigManager.Load();
            var sb = new StringBuilder();
            sb.Append("window.__applyLockConfig({");
            sb.Append("\"slogan\":").Append(WebUiShell.JsonString(string.IsNullOrWhiteSpace(cfg.Slogan) ? "设备已锁定" : cfg.Slogan));
            sb.Append(",\"lockSeconds\":").Append(_remaining);
            sb.Append("});");
            _web.CoreWebView2.ExecuteScriptAsync(sb.ToString());

            // 跟随 Windows 强调色：运行时把动态 MD3 角色写到 documentElement 行内样式，
            // 优先级高于 tokens.css 静态兜底与 <style id="md3-theme"> 注入，确保 100% 跟随系统（与 ConfigForm.applyConfig 同源）。
            _web.CoreWebView2.ExecuteScriptAsync(WebUiShell.BuildMd3ThemeScript());

            // 跟随 Windows“显示动画”系统设置（与配置页 applyConfig 加 .no-anim 一致）
            bool animOff = !NativeMethods.AreAnimationsEnabled();
            _web.CoreWebView2.ExecuteScriptAsync("document.documentElement.classList.toggle('no-anim', " + (animOff ? "true" : "false") + ");");
        }

        private void HandleSubmit(string code, int id)
        {
            if (_exiting)
            {
                WebUiShell.SendHostResult(_web?.CoreWebView2, id, true, "{\"ignored\":true}", null);
                return;
            }
            // 防暴破锁定期内：直接忽略提交（保持冻结，等 C# 5 秒后 __setLocked(false) 恢复）
            if (_locked)
            {
                WebUiShell.SendHostResult(_web?.CoreWebView2, id, true, "{\"ignored\":true}", null);
                return;
            }
            // 彩蛋：短于最小长度下限的输入永远不可能命中真实密码（密码强制 4–12 位），视作误触——
            // 静默清空、不冻结、不写日志。安全红线：阈值必须用公开常量 MinPinLength，绝不可用实际 PinLength。
            if (code.Length < ConfigManager.MinPinLength)
            {
                WebUiShell.SendHostResult(_web?.CoreWebView2, id, true, "{\"short\":true}", null);
                return;
            }

            // 后台 PBKDF2 校验，避免卡住 WebView2 渲染线程。
            string hash = _cfg.PasswordHash;
            string salt = _cfg.PasswordSalt;
            int iters = _cfg.PasswordIterations;
            Task.Run(() =>
            {
                try
                {
                    bool ok = ConfigManager.VerifyPassword(code, hash, salt, iters);
                    if (this.IsDisposed || !this.IsHandleCreated) return;
                    this.BeginInvoke(new Action(() =>
                    {
                        if (_exiting) return;
                        if (ok)
                        {
                            // 先回发成功响应（让前端解除“验证中”态），再关闭窗体解锁
                            Logger.Info("应急密码正确，立即解锁");
                            WebUiShell.SendHostResult(_web?.CoreWebView2, id, true, "{\"ok\":true}", null);
                            ExitLock();
                            return;
                        }
                        // 错误：先由 C# 推送 __setLocked(true)+__showError（权威冻结+提示），
                        // 再回发 ok:true 带 wrong 标志（前端不做额外状态变更，仅作确认）。
                        Logger.Warn("应急密码错误，键盘锁定 5 秒");
                        StartLockout();
                        WebUiShell.SendHostResult(_web?.CoreWebView2, id, true, "{\"wrong\":true}", null);
                    }));
                }
                catch (Exception ex)
                {
                    if (!this.IsDisposed && this.IsHandleCreated)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            if (!_exiting) WebUiShell.SendHostResult(_web?.CoreWebView2, id, false, null, "校验异常: " + ex.Message);
                        }));
                    }
                }
            });
        }

        // ---- C# → JS 推送（JD 权威状态） ----

        private void ExecScript(string js)
        {
            if (_web?.CoreWebView2 == null) return;
            _web.CoreWebView2.ExecuteScriptAsync(js);
        }
        private void PushRemaining(int n) => ExecScript("window.__setRemaining(" + n + ");");
        private void PushLockoutRemaining() => ExecScript("window.__setLockoutRemaining(" + _lockoutRemaining + ");");
        private void PushLocked(bool locked) => ExecScript("window.__setLocked(" + (locked ? "true" : "false") + ");");
        private void PushError(string msg) => ExecScript("window.__showError(" + WebUiShell.JsonString(msg) + ");");
        private void PushClearError() => ExecScript("window.__clearError();");

        /// <summary>窗体句柄创建后确定全屏覆盖尺寸（仅尺寸，不初始化 WebView2）。
        /// WebView2 初始化放在 OnShown（窗体可见后）执行，详见该方法注释。</summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 覆盖所有显示器
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            foreach (var s in Screen.AllScreens)
                bounds = Rectangle.Union(bounds, s.Bounds);
            this.Bounds = bounds;
            // 注意：WebView2 初始化推迟到 OnShown（窗体真正可见后）执行。
            // 经验证 OnShown 时父 HWND 已有效（IsWindow=True）却仍 E_INVALIDARG——
            // 真因为锁屏窗体曾是 TopMost（WS_EX_TOPMOST），TopMost 父窗口上创建 WebView2 控制器会失败。
            // 故 TopMost 已改到控制器创建成功后再置位（见 OnWebViewInit / GiveUpGraceful）。
        }

        /// <summary>
        /// 窗体首次显示后初始化 WebView2。此时窗体已可见、父 HWND 已稳定有效，
        /// 规避 OnHandleCreated 阶段父窗口未实现导致 CreateCoreWebView2ControllerAsync 抛 E_INVALIDARG。
        /// </summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { _web.CreateControl(); } catch { }
            Logger.Debug("[WebView2诊断] OnShown: _web.IsHandleCreated=" + _web.IsHandleCreated
                + ", Handle=0x" + _web.Handle.ToString("X8")
                + ", IsWindow=" + IsWindow(_web.Handle)
                + ", 窗体Visible=" + this.Visible + ", 窗体IsHandleCreated=" + this.IsHandleCreated);
            _ = InitializeWebView();
            // 样式快照用 Info 级：即便 -lock 模式 debug=False 也能看到，用于确认 WebView2 初始化失败时
            // 父窗口是否仍带 WS_EX_TOPMOST / WS_EX_LAYERED 等会破坏控制器创建的扩展样式。
            try
            {
                int ex = GetWindowLong(this.Handle, GWL_EXSTYLE);
                Logger.Info("[WebView2诊断] OnShown 窗体样式: TopMost(prop)=" + this.TopMost
                    + ", WS_EX_TOPMOST=" + ((ex & WS_EX_TOPMOST) != 0)
                    + ", WS_EX_LAYERED=" + ((ex & WS_EX_LAYERED) != 0)
                    + ", EXSTYLE=0x" + ex.ToString("X8"));
            }
            catch (Exception ex) { Logger.Info("窗体样式快照异常: " + ex.Message); }
        }

        /// <summary>只读诊断：记录 WebView2 控件与窗体的尺寸/可见性/屏幕信息，用于定位「提权启动下空白」。</summary>
        private void LogWebState(string tag)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("[WebView2状态 ").Append(tag).Append("] ");
                sb.Append("Visible=").Append(_web.Visible);
                sb.Append(",Size=").Append(_web.Size.Width).Append("x").Append(_web.Size.Height);
                sb.Append(",ClientSize=").Append(_web.ClientSize.Width).Append("x").Append(_web.ClientSize.Height);
                sb.Append(",IsHandleCreated=").Append(_web.IsHandleCreated);
                sb.Append(",Bounds=").Append(this.Bounds.X).Append(",").Append(this.Bounds.Y)
                  .Append(" ").Append(this.Bounds.Width).Append("x").Append(this.Bounds.Height);
                sb.Append(",Screens=").Append(Screen.AllScreens.Length).Append("[");
                foreach (var s in Screen.AllScreens)
                    sb.Append(s.Bounds.Width).Append("x").Append(s.Bounds.Height).Append(";");
                sb.Append("]");
                Logger.Debug(sb.ToString());
            }
            catch (Exception ex) { Logger.Debug("LogWebState 异常: " + ex.Message); }
        }

        /// <summary>判断给定 HWND 是否为系统有效窗口（WebView2 控制器创建前用来验证父窗口有效性）。</summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_LAYERED = 0x00080000;

        /// <summary>把异常展开为可读诊断串：类型 + 消息 + HRESULT（COM 异常）+ 内部异常链。</summary>
        private static string DescribeException(Exception ex)
        {
            if (ex == null) return "无异常";
            var sb = new StringBuilder();
            sb.Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
            if (ex is System.Runtime.InteropServices.COMException com)
                sb.Append(" (HRESULT=0x").Append(com.ErrorCode.ToString("X8")).Append(")");
            if (ex.InnerException != null)
                sb.Append(" | 内部异常: ").Append(ex.InnerException.GetType().FullName)
                  .Append(": ").Append(ex.InnerException.Message);
            return sb.ToString();
        }

        /// <summary>
        /// 强制 WebView2 重新呈现：提权/全屏 TopMost 边框窗体下偶发「JS 已 ready、但合成层未刷新」导致视觉空白。
        /// 通过重新置顶 + 强制布局重算 + 极小尺寸扰动（±1 像素后还原）触发 DWM 重新合成来兜底。
        /// </summary>
        private void ForceWebPresent()
        {
            if (_web == null || _web.IsDisposed || this.IsDisposed || !this.IsHandleCreated) return;
            Logger.Debug("ForceWebPresent: 触发重新置顶与重绘兜底");
            try { this.BringToFront(); } catch { }
            try { this.Activate(); } catch { }
            try
            {
                _web.PerformLayout();
                _web.Invalidate();
                _web.Update();
                // 极小尺寸扰动：先 +1 再 -1，强制 WebView2 控制器重算 bounds 并重新合成
                var oldDock = _web.Dock;
                _web.Dock = DockStyle.None;
                _web.SetBounds(_web.Left, _web.Top, _web.Width + 1, _web.Height + 1);
                _web.SetBounds(_web.Left, _web.Top, _web.Width - 1, _web.Height - 1);
                _web.Dock = oldDock;
                _web.PerformLayout();
                _web.Invalidate();
                _web.Update();
                try { _web.Focus(); } catch { }   // 重新置顶后把键盘焦点交还网页文档，保证物理应急密码可用
                LogWebState("ForceWebPresent后");
            }
            catch (Exception ex)
            {
                Logger.Debug("ForceWebPresent 内部异常: " + ex.Message);
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            _cfg = ConfigManager.Load();
            _remaining = _cfg.LockSeconds;
            LockSignal.Clear();
            AntiTamper.Enable();
            Logger.Debug("锁屏窗体 OnLoad；窗口站=" + NativeMethods.GetCurrentWindowStationName()
                + ", 桌面=" + NativeMethods.GetCurrentDesktopName() + ", 交互式=" + NativeMethods.IsOnInteractiveDesktop());

            _mainTimer = new Timer { Interval = 1000 };
            _mainTimer.Tick += OnMainTick;
            _mainTimer.Start();

            _lockoutTimer = new Timer { Interval = 1000 };   // 每秒一拍驱动防暴破倒计时
            _lockoutTimer.Tick += OnLockoutTick;

            _guardianTimer = new Timer { Interval = 3000 };
            _guardianTimer.Tick += OnGuardianTick;
            _guardianTimer.Start();

            Logger.Info("锁屏界面（WebView2）已加载，锁定 " + _remaining + " 秒");
        }

        private void OnMainTick(object sender, EventArgs e)
        {
            if (_exiting) return;
            _remaining--;
            if (_remaining <= 0)
            {
                PushRemaining(0);
                Logger.Info("倒计时归零，自动解锁");
                ExitLock();
                return;
            }
            PushRemaining(_remaining);
        }

        /// <summary>防暴破：错误后锁定键盘 5 秒，每秒刷新一次提示倒计时，归零恢复。</summary>
        private void StartLockout()
        {
            _locked = true;
            PushLocked(true);
            _lockoutRemaining = LockoutSeconds;
            PushError("密码错误，请" + LockoutSeconds + "秒后重试");   // 首帧：带抖动 + 错误态
            _lockoutTimer.Stop();
            _lockoutTimer.Start();
            Logger.Warn("应急密码错误，键盘锁定 " + LockoutSeconds + " 秒");
        }

        private void OnLockoutTick(object sender, EventArgs e)
        {
            _lockoutRemaining--;
            if (_lockoutRemaining <= 0)
            {
                _lockoutTimer.Stop();
                if (_exiting) return;
                _locked = false;
                PushLocked(false);
                PushClearError();
                Logger.Info("防暴破锁定结束，键盘恢复");
                return;
            }
            PushLockoutRemaining();   // 仅更新倒计时数字，不再触发抖动
        }

        private void OnGuardianTick(object sender, EventArgs e)
        {
            if (_exiting || _guardianPid <= 0) return;
            if (!ProcessExists(_guardianPid))
            {
                Logger.Warn("守护进程已退出，尝试重新拉起");
                _guardianPid = Program.LaunchGuardian();
            }
        }

        private static bool ProcessExists(int pid)
        {
            try { return Process.GetProcessById(pid) != null; }
            catch { return false; }
        }

        /// <summary>正常解锁退出：写入解锁标记，清理并关闭。</summary>
        private void ExitLock()
        {
            if (_exiting) return;
            _exiting = true;

            try { _mainTimer?.Stop(); } catch { }
            try { _lockoutTimer?.Stop(); } catch { }
            try { _guardianTimer?.Stop(); } catch { }

            LockSignal.SetUnlocked();
            AntiTamper.Cleanup();

            try { this.Close(); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            // 不再有原生 LockForm 接管：无论正常解锁还是优雅放弃后倒计时关闭，解锁标记与 AntiTamper 均由本窗体负责。
            LockSignal.SetUnlocked();
            AntiTamper.Cleanup();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            // Timer 未加入组件容器，需手动释放避免原生资源泄漏
            try { _mainTimer?.Dispose(); } catch { }
            try { _lockoutTimer?.Dispose(); } catch { }
            try { _guardianTimer?.Dispose(); } catch { }
            try { _renderWatchdog?.Dispose(); } catch { }
        }
    }
}
