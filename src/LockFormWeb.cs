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
        private bool _failover;   // 已降级给原生 LockForm 接管，Close 时不再误清解锁标记

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
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.Manual;
            this.WindowState = FormWindowState.Normal;
            this.Activated += (s, e) => { this.TopMost = true; };

            _web = new WebView2
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(0x1C, 0x1B, 0x1F)
            };
            _web.CoreWebView2InitializationCompleted += OnWebViewInit;
            Controls.Add(_web);

            _ = InitializeWebView();
        }

        private async Task InitializeWebView()
        {
            // 受保护目录（如 Program Files）普通用户无写权限时 WebView2 的 UserDataFolder 无法创建，
            // 此时直接降级（Program.cs 已据 IsWebView2Available 预检，这里再兜底一次）。
            if (!WebUiShell.IsWebView2Available())
            {
                Logger.Warn("WebView2 不可用，降级为原生锁屏");
                FailOverToNative();
                return;
            }
            try
            {
                // 显式指定 UserDataFolder 到 LocalAppData（始终可写），避免 exe 目录不可写导致初始化失败（黑屏根因）。
                string udf = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LunchHelper", "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, udf);
                await _web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                Logger.Error("WebView2 初始化失败，降级为原生锁屏: " + ex.Message);
                FailOverToNative();
            }
        }

        private void OnWebViewInit(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                Logger.Error("WebView2 初始化失败: " + (e.InitializationException?.Message ?? "未知错误"));
                FailOverToNative();
                return;
            }
            if (_failover) return;   // 已在其它路径降级，避免访问已释放的 CoreWebView2
            try
            {
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
                _web.CoreWebView2.NavigateToString(LoadHtml());
                try { _web.Focus(); } catch { }   // 确保物理键盘事件进入网页文档（无需先点一下）
            }
            catch (Exception ex)
            {
                Logger.Error("WebView2 加载锁屏页失败，降级为原生锁屏: " + ex.Message);
                FailOverToNative();
            }
        }

        /// <summary>
        /// WebView2 不可用时的安全降级：停掉本窗体的安全逻辑，转交原生 LockForm 接管并显示，自身关闭。
        /// 保证任何 WebView2 失败路径都不会留下黑屏——用户始终能看到可操作的锁屏（安全不降级）。
        /// </summary>
        private void FailOverToNative()
        {
            if (_failover) return;
            _failover = true;
            Logger.Warn("锁屏降级为原生 LockForm（WebView2 不可用）");
            try { _mainTimer?.Stop(); } catch { }
            try { _guardianTimer?.Stop(); } catch { }
            try { _lockoutTimer?.Stop(); } catch { }
            try { AntiTamper.Cleanup(); } catch { }
            try { _web?.Dispose(); } catch { }
            try
            {
                this.Hide();                                  // 立即隐藏黑屏窗体，避免闪现
                var native = new LockForm(_debug, _guardianPid);
                native.Show();                                // 原生锁屏 OnLoad 会 Clear 解锁标记 + 启定时器 + 启 AntiTamper
            }
            catch (Exception ex)
            {
                Logger.Error("降级原生锁屏失败: " + ex.Message);
            }
            try { this.Close(); } catch { }
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
                    PushConfig();
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

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // 覆盖所有显示器
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            foreach (var s in Screen.AllScreens)
                bounds = Rectangle.Union(bounds, s.Bounds);
            this.Bounds = bounds;

            _cfg = ConfigManager.Load();
            _remaining = _cfg.LockSeconds;
            LockSignal.Clear();
            AntiTamper.Enable();

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
            // 降级给原生 LockForm 接管时，解锁标记与 AntiTamper 由原生锁屏负责，本窗体不可改写/清理。
            if (_failover) return;
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
        }
    }
}
