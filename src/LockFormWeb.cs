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
        private readonly bool _stripStyle;   // 调试开关 -nostyle：基线为普通窗口，配合下方各 -st-* 增量开关单独加回某个锁屏样式以做二次隔离
        // 有效窗体样式（由 -nostyle 与各 -st-* 增量开关组合决定），用于二次隔离「提权下失败」究竟是哪一个样式导致
        private readonly bool _useBorderless; // 无边框（FormBorderStyle.None）
        private readonly bool _useNoTaskbar;  // 不在任务栏（ShowInTaskbar=false）
        private readonly bool _useFullscreen; // 全屏覆盖所有显示器（StartPosition=Manual + 并集 Bounds）
        private readonly bool _useTopmost;    // 锁屏视觉置顶（用 HWND_TOP 周期前置，不使用会破坏 WebView2 的 WS_EX_TOPMOST）
        private int _guardianPid;
        private Config _cfg;

        private int _remaining;
        private int _lockoutRemaining;   // 防暴破倒计时剩余秒数（C# 权威，每秒一拍）
        private bool _exiting;
        private bool _locked;          // 防暴破锁定期（C# 权威）

        private Timer _mainTimer;
        private Timer _lockoutTimer;
        private Timer _guardianTimer;
        private Timer _topTimer;       // 以「非 WS_EX_TOPMOST」方式周期把锁屏窗体置于普通窗口最前，
                                       // 既保证锁屏视觉置顶，又避免 WS_EX_TOPMOST 抬高 WebView2 控制器创建失败率

        private WebView2 _web;
        private bool _aborted;    // WebView2 已放弃：保留深色窗体由倒计时解锁，不降级原生锁屏

        private Timer _renderWatchdog;   // 渲染看门狗：导航后未收到 ready 回执则放弃原生降级（覆盖提权下 WebView2 静默空白）
        private bool _readyReceived;     // 锁屏页已通过宿主桥接回执 ready（说明 WebView2 真正跑起来了）
        private int _initAttempts;        // WebView2 初始化重试计数（提权/Job 环境下控制器创建偶发失败，重试可自愈）
        private const int MaxInitAttempts = 3;

        private bool _uiAccessGranted;   // 启动时探测一次：提权工具是否以 uiAccess 令牌拉起本进程（决定能否用真 WS_EX_TOPMOST 盖 Win+L）
        private bool _topmostEnabled;    // 渲染成功后是否已按 uiAccess 环境启用真 WS_EX_TOPMOST（自愈计时器据此判定是否需工作）
        private Timer _healTimer;        // 自愈计时器：真置顶+uiAccess 环境下周期轻量重绘，规避 WebView2 合成层偶发冻结致空白

        // 每次启动使用独立的、带 GUID 的 UserDataFolder（见构造函数）：锁屏为 kiosk 形态无需持久化
        // profile/cookie；核心目的是规避「上一次运行的 WebView2 浏览器子进程残留并锁住同一 UDF」
        // 导致后续启动 CreateCoreWebView2ControllerAsync 抛 E_INVALIDARG（首次成功、之后全败的死相）。
        // 缓存只写在程序自身目录内（webview2_lock_* 子目录），绝不向系统目录（LocalAppData 等）写任何数据，退出时清理。
        private string _udf;

        // 防暴破锁定时长（秒）：错误密码后键盘冻结该时长，期间忽略提交。
        private const int LockoutSeconds = 5;

        public LockFormWeb(bool debug, int guardianPid)
        {
            _debug = debug;
            _guardianPid = guardianPid;
            // 二次隔离开关：基线为 -nostyle（普通窗口），再用各 -st-* 单独加回某个锁屏样式，
            // 以判定「提权工具下失败」究竟由哪一个窗体样式导致。
            //   -nostyle        移除全部锁屏样式（普通 Sizable 窗口，已知可成功）
            //   -st-borderless  加回 无边框（FormBorderStyle.None）
            //   -st-notaskbar   加回 不在任务栏（ShowInTaskbar=false）
            //   -st-fullscreen  加回 全屏覆盖所有显示器
            //   -st-topmost     加回 锁屏视觉置顶（HWND_TOP 周期前置，不使用 WS_EX_TOPMOST）
            // 不加 -nostyle 时即为完整锁屏（全部样式生效）。
            string[] args = Environment.GetCommandLineArgs();
            bool strip = false, addBl = false, addTb = false, addFs = false, addTm = false;
            foreach (var a in args)
            {
                if (string.Equals(a, "-nostyle", StringComparison.OrdinalIgnoreCase)) strip = true;
                else if (string.Equals(a, "-st-borderless", StringComparison.OrdinalIgnoreCase)) addBl = true;
                else if (string.Equals(a, "-st-notaskbar", StringComparison.OrdinalIgnoreCase)) addTb = true;
                else if (string.Equals(a, "-st-fullscreen", StringComparison.OrdinalIgnoreCase)) addFs = true;
                else if (string.Equals(a, "-st-topmost", StringComparison.OrdinalIgnoreCase)) addTm = true;
            }
            _stripStyle = strip;
            if (strip)
            {
                // 基线为普通窗口，仅加上显式请求的样式
                _useBorderless = addBl;
                _useNoTaskbar = addTb;
                _useFullscreen = addFs;
                _useTopmost = addTm;
            }
            else
            {
                // 完整锁屏：全部样式生效
                _useBorderless = true;
                _useNoTaskbar = true;
                _useFullscreen = true;
                _useTopmost = true;
            }
            // 每次启动使用全新的、位于程序自身目录内的 UserDataFolder（webview2_lock_<GUID>）：
            // 既不向系统目录（如 LocalAppData）写任何缓存，又规避「复用旧 UDF 被上一轮残留浏览器进程锁住」导致的初始化失败。
            // 退出时由 OnFormClosed 清理本目录；启动时先清理历史残留（均限定在程序目录内）。
            _udf = Path.Combine(
                Path.GetDirectoryName(Application.ExecutablePath),
                "webview2_lock_" + Guid.NewGuid().ToString("N"));
            // 清理历史残留缓存目录（仅限程序目录内的 webview2_lock_*）交由后台线程执行：
            // 该枚举+递归删除可能随启动次数累积而变慢，若放在首帧热路径会拖慢“深色盖屏”出现时机。
            // 当前启动使用的 _udf 唯一且不会被其删除（CleanStaleCacheDirs 会跳过自身），后台删不影响正确性；
            // 该 Task 属本进程内线程池任务，随进程退出而结束，绝不在软件结束后残留后台。
            Task.Run(() => CleanStaleCacheDirs());
            EnsureUdfWritable();
            Logger.Info("[诊断] 进程环境: Elevated=" + IsCurrentProcessElevated()
                + ", 完整性级别=" + GetCurrentIntegrityLevel()
                + ", uiAccess=" + GetCurrentUiAccess()
                + ", 是否处于Job=" + GetCurrentJobState()
                + ", 样式组合: -nostyle=" + _stripStyle
                + ", 无边框=" + _useBorderless
                + ", 不在任务栏=" + _useNoTaskbar
                + ", 全屏=" + _useFullscreen
                + ", 置顶=" + _useTopmost);
            InitializeComponent();
            // 锁屏视觉置顶：用 HWND_TOP 周期把本窗体置于普通窗口最前（不带 WS_EX_TOPMOST），
            // 避免 WinForms TopMost(WS_EX_TOPMOST) 在提权/Job/uiAccess 环境下显著抬高 WebView2 控制器创建失败率。
            if (_useTopmost)
            {
                _topTimer = new Timer { Interval = 400 };
                _topTimer.Tick += (s, e) => BringToFrontSafe();
                _topTimer.Start();
            }
            StartupTimer.Mark("锁屏窗体构造完成");
        }

        /// <summary>探测本机能否用 WebView2 承载锁屏（运行时存在且 exe 目录可写）。</summary>
        internal static bool IsWebView2Available() => WebUiShell.IsWebView2Available();

        private void InitializeComponent()
        {
            this.BackColor = Color.FromArgb(0x1C, 0x1B, 0x1F);   // surface 暗色底色，WebView2 就绪前即深色，无白闪
            this.ForeColor = Color.White;
            // 窗体样式由有效标志（_use*）决定，便于用 -nostyle + 各 -st-* 做二次隔离。
            // 窗体在【控制器创建阶段】不使用 WS_EX_TOPMOST（WinForms TopMost）：该样式在提权/Job/uiAccess
            // 环境下会抬高 WebView2 控制器创建失败率。渲染成功后再由 EnableLockTopmost 按 uiAccess 环境启用真置顶。
            this.FormBorderStyle = _useBorderless ? FormBorderStyle.None : FormBorderStyle.Sizable;
            this.TopMost = false;   // 创建阶段不置顶；渲染成功后由 EnableLockTopmost 按需启用真置顶
            this.ShowInTaskbar = !_useNoTaskbar;
            this.StartPosition = _useFullscreen ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
            this.WindowState = FormWindowState.Normal;

            // WebView2 控件不再于构造期创建（避免 new WebView2() 加载 webviewloader.dll 拖慢「深色盖屏」出现时机）；
            // 改为 OnShown 时由 EnsureWebViewControl() 惰性创建，与「深色窗体先盖屏」解耦——盖屏更早，WebView2 冷启动与之重叠。
        }

        /// <summary>惰性创建 WebView2 控件（不再于 InitializeComponent 占用构造期）：
        /// 仅设置控件属性并挂接初始化完成事件，不触发 EnsureCoreWebView2Async（那一步在 InitializeWebView 进行）。
        /// 延迟创建使「深色窗体构造」更快完成、更早盖屏，WebView2 加载与其重叠，不引入任何后台进程、不写程序目录外缓存。</summary>
        private void EnsureWebViewControl()
        {
            if (_web != null) return;
            _web = new WebView2
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(0x1C, 0x1B, 0x1F),
                // UserDataFolder 指向程序自身目录内的临时缓存子目录（不向系统目录写任何数据），不附加自定义浏览器参数。
                CreationProperties = new CoreWebView2CreationProperties
                {
                    // 每次启动使用独立的、带 GUID 的 UserDataFolder（见构造函数 _udf），位于程序目录内：
                    // 保证每次都是全新目录，不会被上一次残留的 WebView2 浏览器进程锁住，
                    // 从根上消除「首次成功、之后全败」。
                    UserDataFolder = _udf
                }
            };
            _web.CoreWebView2InitializationCompleted += OnWebViewInit;
            Controls.Add(_web);
            _web.Visible = false;   // 初始化阶段隐藏，窗体先显深色底色；暗色页面绘制完成（ready）后再首显，避免首帧白闪
        }

        /// <summary>清理上一轮启动残留的临时缓存目录（仅限程序目录内的 webview2_lock_*），避免无限堆积；
        /// 当前启动使用的 _udf 自身不动。被残留浏览器进程占用时删除会失败，忽略即可，下一轮启动会再次尝试清理。</summary>
        private void CleanStaleCacheDirs()
        {
            try
            {
                string baseDir = Path.GetDirectoryName(Application.ExecutablePath);
                if (!Directory.Exists(baseDir)) return;
                foreach (var dir in Directory.GetDirectories(baseDir, "webview2_lock_*"))
                {
                    if (string.Equals(dir, _udf, StringComparison.OrdinalIgnoreCase)) continue;
                    try { Directory.Delete(dir, true); }
                    catch { /* 可能被上一轮残留的浏览器进程占用，忽略，下次启动再清 */ }
                }
            }
            catch { }
        }

        // 确保 UDF 目录可创建（位于程序自身目录内）。IsWebView2Available 已预检 exe 目录可写，通常成功；
        // 仅当极少见的创建失败（如并发占用）才回退另一个一次性 GUID 目录（仍在程序目录内，webview2_lock_ 前缀），避免初始化卡死。
        private void EnsureUdfWritable()
        {
            try
            {
                Directory.CreateDirectory(_udf);
            }
            catch
            {
                _udf = Path.Combine(Path.GetDirectoryName(_udf), "webview2_lock_" + Guid.NewGuid().ToString("N"));
                try { Directory.CreateDirectory(_udf); } catch { }
            }
        }

        private async Task InitializeWebView()
        {
            if (_aborted) return;
            if (_initAttempts >= MaxInitAttempts) { GiveUpGraceful(); return; }
            _initAttempts++;
            Logger.Debug("InitializeWebView 开始（第 " + _initAttempts + " 次尝试）");
            if (!WebUiShell.IsWebView2Available())
            {
                Logger.Warn("WebView2 不可用，放弃原生降级（保留深色窗体由倒计时解锁）");
                GiveUpGraceful();
                return;
            }
            try
            {
                Logger.Info("WebView2 UserDataFolder: " + _udf);
                // 诊断：与配置页保持一致，使用默认环境（不附加 --no-sandbox/--disable-gpu 等自定义参数），
                // 以判定「提权/非 Shell 启动」下的空白是否由自定义浏览器参数导致；UserDataFolder 已由
                // CreationProperties 指定到程序自身目录（始终可写，且退出时清理）。
                Logger.Debug("锁屏 WebView2 使用默认环境（与配置页一致），不附加自定义浏览器参数");
                LogWebState("初始化前");

                // 渲染看门狗：无论 EnsureCoreWebView2Async 卡死还是页面静默空白（ready 永不送达），
                // 超时即优雅放弃（保留深色窗体，不降级原生锁屏），杜绝永久黑屏。仅创建一次，避免重试时叠加。
                if (_renderWatchdog == null)
                {
                    _renderWatchdog = new Timer { Interval = 10000 };
                    _renderWatchdog.Tick += OnRenderWatchdog;
                    _renderWatchdog.Start();
                }

                await _web.EnsureCoreWebView2Async(null);
                Logger.Info("WebView2 环境创建成功，浏览器版本: " + (_web.CoreWebView2?.Environment?.BrowserVersionString ?? "?"));
                StartupTimer.Mark("WebView2环境创建成功");
            }
            catch (Exception ex)
            {
                Logger.Error("WebView2 初始化失败（第 " + _initAttempts + " 次）: " + DescribeException(ex));
                Logger.Debug("WebView2 初始化异常堆栈:\n" + ex.StackTrace);
                ScheduleWebViewRetry();
            }
        }

        /// <summary>WebView2 初始化失败后的重试/放弃裁决：未达上限则短延时后重试（提权/Job 环境下偶发失败可自愈），
        /// 达到上限则优雅放弃（保留深色窗体由倒计时解锁）。重试经由 BeginInvoke 封送回 UI 线程，
        /// 避免跨线程调用 WebView2 控件方法。</summary>
        private void ScheduleWebViewRetry()
        {
            if (_aborted) return;
            if (_initAttempts < MaxInitAttempts)
            {
                Logger.Warn("WebView2 初始化重试中（" + _initAttempts + "/" + MaxInitAttempts + "）");
                Task.Delay(400).ContinueWith(_ =>
                {
                    if (_aborted || IsDisposed || !IsHandleCreated) return;
                    try { this.BeginInvoke(new Action(() => { _ = InitializeWebView(); })); }
                    catch { }
                });
            }
            else
            {
                Logger.Error("WebView2 初始化重试 " + MaxInitAttempts + " 次仍失败，放弃原生降级");
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
                Logger.Error("WebView2 初始化失败（第 " + _initAttempts + " 次）: " + DescribeException(e.InitializationException));
                if (e.InitializationException != null)
                    Logger.Debug("WebView2 初始化异常堆栈:\n" + e.InitializationException.StackTrace);
                ScheduleWebViewRetry();
                return;
            }
            if (_aborted) return;   // 已在其它路径放弃，避免访问已释放的 CoreWebView2
            try
            {
                // 注意：此处【不】立即启用 WS_EX_TOPMOST 真置顶。父窗口带该样式会显著抬高 WebView2
                // 控制器创建失败率，且创建阶段启用会在提权/Job/uiAccess 环境下冻结合成层而空白。
                // 置顶推迟到锁屏页 ready 回执（HandleLockHost "ready"）后由 EnableLockTopmost 启用，
                // 那时页面已确认渲染完成，再切 WS_EX_TOPMOST 并强重绘即可兼顾「盖住 Win+L」与「画面可见」。
                var settings = _web.CoreWebView2.Settings;
                TrySetWebViewDarkBackground();   // 合成层暗色底色，消除首帧白闪
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
            try { EnableLockTopmost(); } catch { }   // 即使 WebView2 不可用，也按 uiAccess 环境启用真置顶盖住锁屏
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
            var cfg = _cfg ?? ConfigManager.Load();   // 复用 OnLoad 已加载的配置，避免重复读盘
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
                    StartupTimer.Mark("WebView2就绪(可输密码)");
                    try { _web.Visible = true; } catch { }   // 首显已绘制的暗色页面，避免首帧白闪
                    EnableLockTopmost();         // 渲染确认后再启用真置顶（uiAccess 环境）+ 强重绘，兼顾覆盖与渲染
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
            var cfg = _cfg ?? ConfigManager.Load();   // 复用 OnLoad 已加载的配置，避免重复读盘
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
            if (!_useFullscreen) return;   // 非全屏模式：保留默认窗口尺寸
            // 覆盖所有显示器
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            foreach (var s in Screen.AllScreens)
                bounds = Rectangle.Union(bounds, s.Bounds);
            this.Bounds = bounds;
            // 注意：WebView2 初始化推迟到 OnShown（窗体真正可见后）执行。
            // OnShown 时父 HWND 已有效，但该环境下 WebView2 控制器创建仍偶发 E_INVALIDARG；
            // WS_EX_TOPMOST 父窗口会显著抬高创建失败率，故创建阶段本窗体不带 WS_EX_TOPMOST。
            // 渲染成功后再由 EnableLockTopmost 按 uiAccess 环境启用真置顶（见该方法与 BringToFrontSafe）。
        }

        /// <summary>
        /// 窗体首次显示后初始化 WebView2。此时窗体已可见、父 HWND 已稳定有效，
        /// 规避 OnHandleCreated 阶段父窗口未实现导致 CreateCoreWebView2ControllerAsync 抛 E_INVALIDARG。
        /// </summary>
        protected override void OnShown(EventArgs e)
        {
            EnsureWebViewControl();   // 延迟创建 WebView2 控件，不再占用构造期，盖屏更早
            StartupTimer.Mark("WebView2控件创建完成");
            base.OnShown(e);
            StartupTimer.Mark("OnShown(开始初始化WebView2)");
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
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_UPDATENOW = 0x0002;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_FRAME = 0x0400;
        [DllImport("dwmapi.dll")]
        private static extern void DwmFlush();

        // 通过 COM 将 WebView2 合成层默认底色设为暗色，消除首帧白闪。
        // 旧版 SDK 1.0.4078.44 的 C# 包装无 CoreWebView2.DefaultBackgroundColor 属性，故直接 QI ICoreWebView2Controller2 设置。
        // 声明原生 vtable（含 v1 预留槽位）与反射取控制器字段均依赖 SDK 内部实现，整体包在 try/catch，失败仅留白闪、不影响覆盖。
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("F9606ACC-DA9C-4B2A-A942-8D4E8A7D9E6D")]
        private interface ICoreWebView2Controller
        {
            void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6();
            void _7(); void _8(); void _9(); void _10(); void _11(); void _12(); void _13();
            void _14(); void _15(); void _16(); void _17(); void _18(); void _19(); void _20();
            void _21(); void _22();
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("ABB37CAE-6FEC-453C-A68A-BB8889F700C7")]
        private interface ICoreWebView2Controller2 : ICoreWebView2Controller
        {
            uint GetDefaultBackgroundColor();
            void SetDefaultBackgroundColor(uint color);
        }

        /// <summary>将 WebView2 合成层默认底色设为暗色（与窗体一致），消除首帧白闪。
        /// 仅在 ICoreWebView2Controller2 可用时生效；失败仅留白闪，不影响覆盖与渲染。</summary>
        private void TrySetWebViewDarkBackground()
        {
            try
            {
                // COREWEBVIEW2_COLOR 字节序为 A,R,G,B（低→高），暗色 surface 0x1C1B1F 不透明：
                // A=FF,R=1C,G=1B,B=1F → uint = 0x1F1B1CFF
                const uint dark = 0x1F1B1CFF;
                object ctrl = null;
                // 1) 优先按常见字段名取控制器（不同 SDK 版本字段名不一）
                foreach (var name in new[] { "myController", "_coreWebView2Controller", "coreWebView2Controller", "_nativeController", "controller" })
                {
                    var fld = typeof(WebView2).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                    if (fld != null) { var v = fld.GetValue(_web); if (v != null) { ctrl = v; break; } }
                }
                // 2) 兜底：遍历所有私有 COM 字段，逐个尝试 QI 到 ICoreWebView2Controller2
                if (ctrl == null)
                {
                    var iid2 = new Guid("ABB37CAE-6FEC-453C-A68A-BB8889F700C7");
                    foreach (var f in typeof(WebView2).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                    {
                        var v = f.GetValue(_web);
                        if (v == null || !Marshal.IsComObject(v)) continue;
                        IntPtr pu = Marshal.GetIUnknownForObject(v);
                        if (Marshal.QueryInterface(pu, ref iid2, out IntPtr pTmp) == 0)
                        { Marshal.Release(pTmp); Marshal.Release(pu); ctrl = v; break; }
                        Marshal.Release(pu);
                    }
                }
                if (ctrl == null) { Logger.Info("TrySetWebViewDarkBackground: 未找到控制器，跳过"); return; }
                IntPtr pUnk = Marshal.GetIUnknownForObject(ctrl);
                var iid = new Guid("ABB37CAE-6FEC-453C-A68A-BB8889F700C7");
                if (Marshal.QueryInterface(pUnk, ref iid, out IntPtr pCtrl2) != 0)
                { Logger.Info("TrySetWebViewDarkBackground: QI ICoreWebView2Controller2 失败，跳过"); Marshal.Release(pUnk); return; }
                try
                {
                    var ctrl2 = (ICoreWebView2Controller2)Marshal.GetObjectForIUnknown(pCtrl2);
                    ctrl2.SetDefaultBackgroundColor(dark);
                    Logger.Info("TrySetWebViewDarkBackground: 已设置暗色合成层底色");
                }
                finally { Marshal.Release(pCtrl2); Marshal.Release(pUnk); }
            }
            catch (Exception ex) { Logger.Info("TrySetWebViewDarkBackground 异常（忽略，仅留白闪）: " + ex.Message); }
        }

        // ---- 进程提权状态诊断（用于确认「提权自动化工具下失败」是否确由提权令牌导致）----
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
            IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_ELEVATION { public int TokenIsElevated; }

        /// <summary>判断当前进程是否以提权（高完整性/admin 令牌）运行。失败按非提权处理。</summary>
        private static bool IsCurrentProcessElevated()
        {
            try
            {
                IntPtr token;
                if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0008 /*TOKEN_QUERY*/, out token))
                    return false;
                try
                {
                    int size = Marshal.SizeOf(typeof(TOKEN_ELEVATION));
                    IntPtr buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        uint ret;
                        if (!GetTokenInformation(token, 20 /*TokenElevation*/, buf, (uint)size, out ret))
                            return false;
                        var elev = (TOKEN_ELEVATION)Marshal.PtrToStructure(buf, typeof(TOKEN_ELEVATION));
                        return elev.TokenIsElevated != 0;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { CloseHandle(token); }
            }
            catch { return false; }
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertSidToStringSid(IntPtr pSid, out IntPtr str);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsProcessInJob(IntPtr processHandle, IntPtr jobHandle, out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_MANDATORY_LABEL { public SID_AND_ATTRIBUTES Label; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SID_AND_ATTRIBUTES { public IntPtr Sid; public int Attributes; }

        /// <summary>读取当前进程令牌的实际完整性级别（低/中/高/系统）。用于区分「普通提权(高完整性)」
        /// 与「工具以更高/特殊令牌启动」——WebView2 官方建议在标准(中)完整性下运行，宿主完整性越高越易失败。</summary>
        private static string GetCurrentIntegrityLevel()
        {
            IntPtr token;
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0008 /*TOKEN_QUERY*/, out token))
                return "未知(OpenProcessToken失败)";
            try
            {
                uint size;
                GetTokenInformation(token, 25 /*TokenIntegrityLevel*/, IntPtr.Zero, 0, out size);
                if (size == 0) return "未知(取长度失败)";
                IntPtr buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (!GetTokenInformation(token, 25, buf, size, out size))
                        return "未知(GetTokenInformation失败)";
                    var tml = (TOKEN_MANDATORY_LABEL)Marshal.PtrToStructure(buf, typeof(TOKEN_MANDATORY_LABEL));
                    IntPtr str;
                    if (!ConvertSidToStringSid(tml.Label.Sid, out str))
                        return "未知(ConvertSid失败)";
                    try
                    {
                        string sid = Marshal.PtrToStringAuto(str);   // 形如 S-1-16-12288
                        string[] parts = sid.Split('-');
                        uint rid;
                        if (parts.Length >= 1 && uint.TryParse(parts[parts.Length - 1], out rid))
                        {
                            switch (rid)
                            {
                                case 0x0000: return "无(0)";
                                case 0x1000: return "低(Low)";
                                case 0x2000: return "中(Medium)";
                                case 0x2100: return "中+(Medium+)";
                                case 0x3000: return "高(High)";
                                case 0x4000: return "系统(System)";
                                case 0x5000: return "保护进程(Protected)";
                                default: return "IL=0x" + rid.ToString("X4") + "(" + sid + ")";
                            }
                        }
                        return sid;
                    }
                    finally { LocalFree(str); }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(token); }
        }

        /// <summary>读取当前进程令牌的 uiAccess 标志（TokenUIAccess=26）。若提权工具自身带 uiAccess，
        /// 子进程会继承该令牌，可能干扰 WebView2 派生其浏览器子进程。</summary>
        private static string GetCurrentUiAccess()
        {
            IntPtr token;
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0008, out token))
                return "未知";
            try
            {
                IntPtr buf = Marshal.AllocHGlobal(4);
                try
                {
                    uint ret;
                    if (!GetTokenInformation(token, 26 /*TokenUIAccess*/, buf, 4, out ret))
                        return "未知";
                    return Marshal.ReadInt32(buf) != 0 ? "是(True)" : "否(False)";
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(token); }
        }

        /// <summary>判断当前进程是否处于作业对象(Job)中。若提权工具把子进程放入禁用 child-breakaway 的作业，
        /// WebView2 将无法派生其浏览器子进程，导致内容已加载却空白。</summary>
        private static string GetCurrentJobState()
        {
            try
            {
                bool inJob;
                IsProcessInJob(Process.GetCurrentProcess().Handle, IntPtr.Zero, out inJob);
                return inJob ? "是(在Job中)" : "否(不在Job)";
            }
            catch (Exception ex) { return "未知(" + ex.Message + ")"; }
        }

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

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private static readonly IntPtr HWND_TOP = new IntPtr(0);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        /// <summary>以「非 WS_EX_TOPMOST」方式把本窗体置于普通窗口 z 序最前，且不抢占焦点（SWP_NOACTIVATE）。
        /// 既能保证锁屏视觉置顶，又规避 WS_EX_TOPMOST 父窗口抬高 WebView2 控制器创建失败率的已知问题。</summary>
        private void BringToFrontSafe()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                SetWindowPos(this.Handle, HWND_TOP, 0, 0, 0, 0,
                    SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            catch { }
        }

        /// <summary>渲染成功后启用锁屏置顶。uiAccess 环境下必须用真正的 WS_EX_TOPMOST（this.TopMost=true）
        /// 才能盖住 Win+L 安全桌面锁屏；普通环境无 uiAccess，WS_EX_TOPMOST 既盖不住又抬高 WebView2 合成层
        /// 空白风险，故退回 HWND_TOP 视觉置顶。置顶推迟到渲染成功后，避免干扰 WebView2 控制器创建。</summary>
        private void EnableLockTopmost()
        {
            if (!_useTopmost) return;
            if (_uiAccessGranted)
            {
                try { this.TopMost = true; } catch { }   // 真置顶：配合 uiAccess 盖住 Win+L
                try { _topTimer?.Stop(); } catch { }     // 已是永久最顶层，无需周期 HWND_TOP 前置
                _topmostEnabled = true;
                StartHealTimer();                         // 置顶后启动自愈，周期轻量重绘规避偶发空白
                ForceWebPresent();                         // 置顶后强制重新合成，规避 WS_EX_TOPMOST 下偶发空白
            }
            else
            {
                _topmostEnabled = false;
                BringToFrontSafe();                        // 普通视觉置顶，不引入 WS_EX_TOPMOST
            }
        }

        /// <summary>
        /// 强制 WebView2 重新呈现：偶发「JS 已 ready、但合成层未刷新」导致视觉空白。
        /// 通过重新置前 + 强制布局重算 + 极小尺寸扰动（±1 像素后还原）触发 DWM 重新合成来兜底。
        /// </summary>
        private void ForceWebPresent()
        {
            if (_web == null || _web.IsDisposed || this.IsDisposed || !this.IsHandleCreated) return;
            Logger.Debug("ForceWebPresent: 触发重新置顶与重绘兜底");
            try { this.BringToFront(); } catch { }
            try
            {
                // 强重绘：强制布局重算与重绘，触发 DWM 重新合成，规避「JS 已 ready 但视觉空白」。
                // 不做 Visible 切换（detach/reattach 合成层会露白帧）。
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
                // 强制 DWM 对整个窗口（含 WebView2 子控件）重新合成，根治 WS_EX_TOPMOST 下「JS ready 但空白」
                try { RedrawWindow(this.Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN | RDW_FRAME); } catch { }
                try { DwmFlush(); } catch { }
                LogWebState("ForceWebPresent后");
            }
            catch (Exception ex)
            {
                Logger.Debug("ForceWebPresent 内部异常: " + ex.Message);
            }
        }

        /// <summary>启动置顶自愈计时器：uiAccess + WS_EX_TOPMOST 环境下 WebView2 合成层偶发冻结致空白，
        /// 周期（1.2s）轻量强制 DWM 重绘可即时自愈，避免长期黑屏。仅当确为 uiAccess 真置顶时才启用。</summary>
        private void StartHealTimer()
        {
            if (_healTimer != null) return;
            _healTimer = new Timer { Interval = 1200 };
            _healTimer.Tick += OnHealTick;
            _healTimer.Start();
            Logger.Debug("置顶自愈计时器已启动（1.2s 周期轻量重绘）");
        }

        /// <summary>自愈节拍：仅做轻量 DWM 重绘 + 维持 z 序最前，不做 Visible 切换，避免打断交互/动画。</summary>
        private void OnHealTick(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated || _exiting || _aborted) return;
            if (!_uiAccessGranted || !_topmostEnabled) return;   // 仅 uiAccess 真置顶环境需自愈
            try { RedrawWindow(this.Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN); } catch { }
            try { DwmFlush(); } catch { }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // 一次性探测 uiAccess 令牌（提权工具是否以 uiAccess 拉起本进程），决定后续能否用真 WS_EX_TOPMOST 盖 Win+L
            _uiAccessGranted = (GetCurrentUiAccess() == "是(True)");
            Logger.Info("[诊断] uiAccess 已授予=" + _uiAccessGranted);

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
            StartupTimer.Mark("OnLoad完成");
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
            try { using (var p = Process.GetProcessById(pid)) return true; }
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
            try { _topTimer?.Dispose(); } catch { }
            try { _healTimer?.Dispose(); } catch { }
            try { _renderWatchdog?.Dispose(); } catch { }
            // 释放 WebView2 以让浏览器子进程退出，并清理本次的临时 UDF 目录（被占用时忽略，下轮再清）
            try { _web?.Dispose(); } catch { }
            try { if (_udf != null && Directory.Exists(_udf)) Directory.Delete(_udf, true); } catch { }
        }
    }
}
