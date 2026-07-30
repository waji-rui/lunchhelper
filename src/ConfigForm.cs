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
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LunchHelper
{
    /// <summary>
    /// 触控配置界面（Material Design 3 深色主题），由 WebView2 承载内嵌离线 HTML 渲染。
    /// 选择 WebView2 的原因：浏览器引擎的布局单位（rem / vh / clamp）天生跟随系统 DPI 与
    /// Windows 文本缩放，结构上彻底消除此前 WinForms 自绘卡片“字被截断/重叠”的根因。
    /// 发行模式为 Evergreen：依赖系统已装的 WebView2 Runtime（Win11 预装、多数 Win10 已装），
    /// exe 仍保持单文件；运行时缺失时给出兜底提示而非白屏。
    /// 窗口使用原生 Sizable 风格（深色标题栏由 DWM 处理），最大化/最小化/还原/边缘缩放/
    /// 系统动画等全部由 Windows 接管；WebView2 仅负责内容区。
    /// 业务校验/密码哈希仍在 C# 端完成，HTML/JS 仅负责呈现与采集，绝不下放安全逻辑。
    /// </summary>
    internal class ConfigForm : Form
    {
        private readonly bool _debug;
        private WebView2 _web;
        private Color _accent;
        private Color _onAccent;

        // MD3 动态深色配色：由 Windows 强调色（源色）按官方 tone 映射生成整套角色，
        // 详见 GenerateMd3DarkScheme 注释。_scheme 在构造期计算一次。
        private Md3Scheme _scheme;

        public ConfigForm(bool debug)
        {
            _debug = debug;
            Color src = NativeMethods.GetAccentColor();   // Windows 强调色 = MD3 source color
            _scheme = GenerateMd3DarkScheme(src);
            _accent = _scheme.Primary;
            _onAccent = _scheme.OnPrimary;
            InitializeComponent();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.EnableDarkTitleBar(this.Handle);
            NativeMethods.EnableRoundedCorners(this.Handle);
            // 默认尺寸：按窗口所在显示器工作区百分比（逻辑像素）。
            // 注意：app.manifest 已声明 DPI 感知，Screen.WorkingArea 返回逻辑像素，无需再除以 DeviceDpi。
            // 用 FromHandle(this.Handle) 而非 PrimaryScreen，避免多屏/主屏非当前屏时取错。
            Rectangle wa = Screen.FromHandle(this.Handle).WorkingArea;
            // 默认严格 16:9 比例：宽取工作区 78%，高 = 宽 × 9/16；
            // 超高/极低屏用可用高度兜底，尽量保持 16:9。
            int w = (int)(wa.Width * 0.78);
            w = Math.Max(900, Math.Min(w, 2800));        // 默认 ≥900 逻辑 px；上限 2800 让 2K/4K 屏也能到 ~78%
            int h = (int)(w * 9.0 / 16.0);               // 严格 16:9
            int maxH = (int)(wa.Height * 0.90);          // 不超过工作区 90%（留任务栏/标题栏）
            if (h > maxH) h = maxH;
            else if (h < 540) h = 540;
            ClientSize = new Size(w, h);

            // 最小尺寸相对工作区，但刻意保持较小（上限 ~560px），
            // 以便用户把窗口缩到 CSS 桌面断点（@media max-width:45rem≈720px）之下时，
            // 能正常触发抽屉/堆叠的响应式布局（而非停在桌面态）。
            // 不设置 MaximumSize——允许用户把窗口最大化铺满全屏。
            int minW = (int)(wa.Width * 0.5);
            int minH = (int)(wa.Height * 0.5);
            minW = Math.Max(minW, 320); minW = Math.Min(minW, Math.Min(wa.Width, 560));
            minH = Math.Max(minH, 420); minH = Math.Min(minH, wa.Height);
            MinimumSize = new Size(minW, minH);
            // 因尺寸在 HandleCreated 阶段才最终确定，需手动按当前显示器居中。
            Location = new Point(
                wa.Left + (wa.Width - Size.Width) / 2,
                wa.Top + (wa.Height - Size.Height) / 2);
            Logger.Info($"ConfigForm 默认尺寸: 屏幕={wa.Width}x{wa.Height}, ClientSize={ClientSize.Width}x{ClientSize.Height}, Location={Location.X},{Location.Y}");
            // 异步初始化 WebView2（Evergreen，使用系统运行时）
            _ = InitializeWebView();
        }

        private void InitializeComponent()
        {
            Text = ConfigManager.AppName + " 设置";
            StartPosition = FormStartPosition.Manual;    // 居中逻辑在 OnHandleCreated 中按最终尺寸计算
            // 原生窗口：系统标题栏（深色）+ 最小化/最大化/还原/关闭按钮 + 边缘拖拽缩放
            // 全部由 Windows 接管，含系统动画与 Aero Snap，无需手搓。
            FormBorderStyle = FormBorderStyle.Sizable;
            // 关闭 WinForms 自动缩放：WebView2 自带 DPI 适配，窗口按逻辑像素设置即可，
            // 避免 Font 模式/构造期 DeviceDpi=96 造成的双重缩放与尺寸漂移。
            AutoScaleMode = AutoScaleMode.None;
            BackColor = _scheme.Surface;
            Font = new Font("Segoe UI", 12F);
            // 默认尺寸与最小/最大尺寸均在 OnHandleCreated 中按工作区比例计算（见该方法注释），
            // 此处不写死，避免硬编码像素在不同分辨率/DPI/缩放下失真。

            _web = new WebView2
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                BackColor = _scheme.Surface,
                // WebView2 默认把用户数据文件夹放在 exe 同级目录；若 exe 位于
                // C:\Program Files\ 等受保护目录，普通用户没有写入权限会导致初始化失败。
                // 显式指定到 %LOCALAPPDATA% 下，保证任何位置都能正常启动。
                CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        ConfigManager.AppName,
                        "WebView2Data")
                }
            };
            _web.CoreWebView2InitializationCompleted += OnWebViewInit;
            Controls.Add(_web);
        }

        private async Task InitializeWebView()
        {
            try
            {
                // null = 使用默认 Evergreen WebView2 Runtime（不打包引擎）
                await _web.EnsureCoreWebView2Async(null);
            }
            catch (Exception ex)
            {
                Logger.Error("WebView2 初始化失败: " + ex);
                ShowWebView2Error(ex.Message, IsLikelyRuntimeMissing(ex));
            }
        }

        private void OnWebViewInit(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                string detail = e.InitializationException?.Message ?? "未知错误";
                Logger.Error("WebView2 初始化失败: " + e.InitializationException);
                ShowWebView2Error(detail, IsLikelyRuntimeMissing(e.InitializationException));
                return;
            }
            try
            {
                var settings = _web.CoreWebView2.Settings;
                settings.AreDefaultContextMenusEnabled = false; // 触控界面更干净
                settings.AreDevToolsEnabled = _debug;
                settings.IsZoomControlEnabled = false;
                _web.CoreWebView2.WebMessageReceived += OnWebMessage;
                _web.CoreWebView2.NavigateToString(LoadHtml());
            }
            catch (Exception ex)
            {
                Logger.Error("WebView2 加载配置页失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 从内嵌资源读取离线 HTML 与拆分的设计令牌/组件样式，运行时注入到占位符后由
        /// WebView2.NavigateToString 加载（无需联网）。
        /// HTML 头部保留 <c>&lt;style id="md3-tokens"&gt;/*CSS_TOKENS*/&lt;/style&gt;</c>、
        /// <c>&lt;style id="md3-components"&gt;/*CSS_COMPONENTS*/&lt;/style&gt;</c>、
        /// <c>&lt;style id="md3-dialog"&gt;/*CSS_DIALOG*/&lt;/style&gt;</c> 三个样式占位符，
        /// 以及 <c>&lt;script&gt;/*JS_DIALOG*/&lt;/script&gt;</c>（弹窗组件）与
        /// <c>&lt;script&gt;/*JS_HOST*/&lt;/script&gt;</c>（宿主桥接 API）两个脚本占位符；
        /// 样式与脚本已拆到 ui/（内嵌资源），便于锁屏复用与未来主题/插件系统覆盖
        /// （换肤只需替换令牌层，组件样式无需改动；插件仅需调用 window.LunchHelper.host.*）。
        /// </summary>
        private string LoadHtml()
        {
            var asm = Assembly.GetExecutingAssembly();
            string html = ReadEmbedded(asm, "ConfigPage.html");
            if (html == null) return FallbackHtml();
            string tokens = ReadEmbedded(asm, "ui.tokens.css") ?? "";
            string css = ReadEmbedded(asm, "ui.components.css") ?? "";
            string dlgCss = ReadEmbedded(asm, "ui.dialog.css") ?? "";
            string dlgJs = ReadEmbedded(asm, "ui.dialog.js") ?? "";
            string hostJs = ReadEmbedded(asm, "ui.host-bridge.js") ?? "";
            html = html.Replace("/*CSS_TOKENS*/", tokens)
                       .Replace("/*CSS_COMPONENTS*/", css)
                       .Replace("/*CSS_DIALOG*/", dlgCss)
                       .Replace("/*JS_DIALOG*/", dlgJs)
                       .Replace("/*JS_HOST*/", hostJs);
            return html;
        }

        /// <summary>读取指定后缀的内嵌资源文本（找不到返回 null）。</summary>
        private static string ReadEmbedded(Assembly asm, string suffix)
        {
            string name = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            using (var s = asm.GetManifestResourceStream(name))
            using (var r = new StreamReader(s))
                return r.ReadToEnd();
        }

        /// <summary>资源缺失时的兜底页面（不白屏，给出可读提示）。</summary>
        private static string FallbackHtml() =>
            "<html><body style='font-family:Segoe UI;background:#19171C;color:#E5E3E8;padding:1.5rem'>未找到内嵌配置页资源。</body></html>";

        // ---- JS ↔ C# 桥接 ----

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (IsDisposed) return;
            // 本版本(1.0.4078.44)的 CoreWebView2WebMessageReceivedEventArgs 只有
            // TryGetWebMessageAsString()（无参、返回 string），无 WebMessageAsString 属性。
            // JS 端 postMessage(JSON.stringify(obj)) 发的是字符串，故此处直接取回 JSON 文本。
            string raw = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(raw)) return;
            WebMsg msg;
            try { msg = Deserialize<WebMsg>(raw); }
            catch { return; }
            if (msg == null || string.IsNullOrEmpty(msg.Cmd)) return;

            switch (msg.Cmd)
            {
                case "ready":
                    _web.ExecuteScriptAsync("window.applyConfig(" + BuildConfigJson() + ")");
                    RegisterPlugins();
                    break;
                case "save":
                    HandleSave(msg.Data, msg.Silent);
                    break;
                case "host":
                    // 宿主桥接请求：由 op 决定操作，结果经 SendHostResult 回传（消息 id 配对）
                    HandleHost(msg);
                    break;
                case "dialogResult":
                    // C# 主动弹窗（ShowHostDialog）的用户操作结果，由前端 __showHostDialog 回调触发。
                    // 目前仅记录，后续可在此接入具体业务（如确认后执行某动作）。
                    try
                    {
                        string btn = msg.Data != null ? (msg.Data.ButtonId ?? "") : "";
                        Logger.Info("宿主弹窗结果: " + (btn == null ? "null(取消)" : btn));
                    }
                    catch { }
                    break;
                case "reset":
                    try
                    {
                        ConfigManager.ResetToDefault();
                        Logger.Info("已恢复默认配置");
                        _web.ExecuteScriptAsync("window.applyConfig(" + BuildConfigJson() + ")");
                        SendSaved(true, "已恢复默认配置。");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("恢复默认配置异常: " + ex.Message);
                        SendSaved(false, "恢复默认失败：" + ex.Message);
                    }
                    break;
            }
        }

        /// <summary>加载并注册本地插件页（plugins/ 目录），注入前端 __registerPlugins。</summary>
        private void RegisterPlugins()
        {
            if (IsDisposed || _web?.CoreWebView2 == null) return;
            try
            {
                var pluginsJson = PluginHost.ToJson(PluginHost.LoadAll());
                _web.ExecuteScriptAsync("window.__registerPlugins(" + pluginsJson + ")");
            }
            catch (Exception ex) { Logger.Error("插件加载失败: " + ex.Message); }
        }

        /// <summary>处理宿主桥接请求（cmd=="host"）。op 决定操作，结果经 SendHostResult 回传前端。</summary>
        private void HandleHost(WebMsg msg)
        {
            if (msg == null) return;
            switch (msg.Op)
            {
                case "getConfig":
                    // payload 直接内联 BuildConfigJson() 产出的对象字面量
                    SendHostResult(msg.Id, true, BuildConfigJson());
                    break;
                case "setConfig":
                    // 经桥接写入：静默成功（避免与实时保存重复的 toast）；失败也要如实回传
                    bool ok = HandleSave(msg.Data, true);
                    SendHostResult(msg.Id, ok, ok ? "{\"ok\":true}" : "null");
                    break;
                case "areAnimationsEnabled":
                    SendHostResult(msg.Id, true, NativeMethods.AreAnimationsEnabled() ? "true" : "false");
                    break;
                default:
                    SendHostResult(msg.Id, false, null, "未知宿主操作: " + (msg.Op ?? ""));
                    break;
            }
        }

        private bool HandleSave(SaveData d, bool silent)
        {
            if (d == null) { SendSaved(false, "收到空数据。"); return false; }
            try
            {
                var cfg = ConfigManager.Load();
                if (!int.TryParse(d.LockSeconds, out int lockSecs)) lockSecs = 10;
                if (lockSecs < 1) lockSecs = 1;
                else if (lockSecs > 3600) lockSecs = 3600;
                if (!int.TryParse(d.LogRetentionDays, out int retDays)) retDays = 14;
                if (retDays < 0) retDays = 0;
                else if (retDays > 3650) retDays = 3650;
                cfg.LockSeconds = lockSecs;
                cfg.LogRetentionDays = retDays;
                cfg.Slogan = d.Slogan ?? "";
                cfg.EnableUiAccess = d.EnableUiAccess;

                string pwd = d.Password ?? "";
                if (pwd.Length > 0)
                {
                    // 前端已校验，此处再校验一次（不信任前端输入）
                    string pattern = $"^\\d{{{ConfigManager.MinPinLength},{ConfigManager.MaxPinLength}}}$";
                    if (!Regex.IsMatch(pwd, pattern))
                    {
                        SendSaved(false, $"应急解锁密码必须为 {ConfigManager.MinPinLength}–{ConfigManager.MaxPinLength} 位纯数字。");
                        return false;
                    }
                    ConfigManager.ComputePasswordHash(pwd, out var hash, out var salt);
                    cfg.PasswordHash = hash;
                    cfg.PasswordSalt = salt;
                    cfg.PasswordIterations = ConfigManager.DefaultIterations;
                    // 安全红线：不在配置文件中持久化真实 PIN 长度，避免泄露密码位数。
                }

                ConfigManager.Save(cfg);
                Logger.Info("配置已保存");
                SendSaved(true, "配置已保存。", silent);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("保存配置异常: " + ex.Message);
                SendSaved(false, "保存失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>将保存结果回传 HTML 端，由 onSaved 显示轻提示。silent=true 时成功不弹 toast。</summary>
        private void SendSaved(bool ok, string msg, bool silent = false)
        {
            if (IsDisposed || _web?.CoreWebView2 == null) return;
            string j = "{\"ok\":" + (ok ? "true" : "false") + ",\"msg\":" + JsonString(msg)
                + ",\"silent\":" + (silent ? "true" : "false") + "}";
            _web.ExecuteScriptAsync("window.onSaved(" + j + ")");
        }

        /// <summary>
        /// 把宿主桥接请求的结果回传前端 <c>window.__hostResult({id,ok,payload})</c>。
        /// payloadJson 为「已序列化」的 JSON 值（对象/数组/字符串/数字/布尔），直接内联，
        /// 不再经 JsonString 二次转义——调用方须自行保证其为合法 JSON 片段（如 BuildConfigJson()）。
        /// </summary>
        private void SendHostResult(int id, bool ok, string payloadJson, string error = null)
        {
            if (IsDisposed || _web?.CoreWebView2 == null) return;
            string payload = ok ? (payloadJson ?? "null") : "null";
            string j = "{\"id\":" + id + ",\"ok\":" + (ok ? "true" : "false")
                + ",\"payload\":" + payload;
            if (!ok) j += ",\"error\":" + JsonString(error ?? "unknown error");
            j += "}";
            _web.ExecuteScriptAsync("window.__hostResult(" + j + ")");
        }

        /// <summary>
        /// C# 主动弹 MD3 弹窗（经前端 MD3Dialog 渲染）。optsJson 为合法 JSON 对象字符串，
        /// 形如 {"title":"提示","content":"...","buttons":[{"id":"ok","text":"确定","style":"primary"}]}。
        /// 用户操作结果由前端以 cmd:"dialogResult" 回传，在 OnWebMessage 中处理。
        /// 用途：让 C# 业务侧（如运行期错误、插件触发的确认）也能驱动 Modal 流程。
        /// </summary>
        public void ShowHostDialog(string optsJson)
        {
            if (IsDisposed || _web?.CoreWebView2 == null) return;
            if (string.IsNullOrWhiteSpace(optsJson)) optsJson = "{}";
            _web.ExecuteScriptAsync("window.__showHostDialog(" + optsJson + ")");
        }

        /// <summary>把当前 Config 序列化为 applyConfig 所需的 JSON（手工构造，避免额外依赖）。</summary>
        private string BuildConfigJson()
        {
            var cfg = ConfigManager.Load();
            var s = _scheme;
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"lockSeconds\":").Append(cfg.LockSeconds).Append(',');
            sb.Append("\"logRetentionDays\":").Append(cfg.LogRetentionDays).Append(',');
            sb.Append("\"slogan\":").Append(JsonString(cfg.Slogan)).Append(',');
            sb.Append("\"enableUiAccess\":").Append(cfg.EnableUiAccess ? "true" : "false").Append(',');
            // MD3 动态配色：整套路色由 Windows 强调色生成后注入 CSS 变量
            sb.Append("\"accent\":\"").Append(ColorToHex(s.Primary)).Append("\",");
            sb.Append("\"on-accent\":\"").Append(ColorToHex(s.OnPrimary)).Append("\",");
            sb.Append("\"accent-container\":\"").Append(ColorToHex(s.PrimaryContainer)).Append("\",");
            sb.Append("\"on-accent-container\":\"").Append(ColorToHex(s.OnPrimaryContainer)).Append("\",");
            sb.Append("\"surface\":\"").Append(ColorToHex(s.Surface)).Append("\",");
            sb.Append("\"on-surface\":\"").Append(ColorToHex(s.OnSurface)).Append("\",");
            sb.Append("\"surface-container\":\"").Append(ColorToHex(s.SurfaceContainer)).Append("\",");
            sb.Append("\"surface-container-high\":\"").Append(ColorToHex(s.SurfaceContainerHigh)).Append("\",");
            sb.Append("\"surface-container-highest\":\"").Append(ColorToHex(s.SurfaceContainerHighest)).Append("\",");
            sb.Append("\"surface-variant\":\"").Append(ColorToHex(s.SurfaceVariant)).Append("\",");
            sb.Append("\"on-surface-variant\":\"").Append(ColorToHex(s.OnSurfaceVariant)).Append("\",");
            sb.Append("\"outline\":\"").Append(ColorToHex(s.Outline)).Append("\",");
            sb.Append("\"minPin\":").Append(ConfigManager.MinPinLength).Append(',');
            sb.Append("\"maxPin\":").Append(ConfigManager.MaxPinLength).Append(',');
            // 跟随 Windows“显示动画”系统设置：false 时前端全局禁用过渡/动画
            sb.Append("\"animations\":").Append(NativeMethods.AreAnimationsEnabled() ? "true" : "false").Append(',');
            // 关于页内联内容所需的元数据（版本号等动态数据由 C# 注入，静态文案在 HTML 中）
            sb.Append("\"about\":{\"appName\":").Append(JsonString(ConfigManager.AppName))
              .Append(",\"version\":").Append(JsonString(ConfigManager.Version)).Append('}');
            sb.Append('}');
            return sb.ToString();
        }

        // ---- 运行时缺失兜底（不白屏） ----

        private static bool IsLikelyRuntimeMissing(Exception ex)
        {
            if (ex == null) return false;
            string msg = (ex.Message ?? "").ToLowerInvariant();
            return msg.Contains("runtime") || msg.Contains("not installed") || msg.Contains("no available")
                || msg.Contains("找不到") || msg.Contains("无法找到") || msg.Contains("未能加载");
        }

        private void ShowWebView2Error(string detail, bool likelyMissingRuntime)
        {
            if (IsDisposed) return;
            BeginInvoke((Action)(() =>
            {
                Controls.Clear();
                var p = new Panel { Dock = DockStyle.Fill, BackColor = _scheme.Surface, Padding = new Padding(24) };

                string hint = likelyMissingRuntime
                    ? "本机可能未安装 Microsoft Edge WebView2 运行时。Windows 11 通常已自带；少数 Windows 10 机器需手动安装。"
                    : "常见原因：程序所在目录没有写入权限（例如 C:\\Program Files）。请尝试以管理员身份运行，或将程序移到其他目录。";

                var lbl = new Label
                {
                    Text = "无法加载配置界面：WebView2 初始化失败。\n\n" +
                           "错误详情：" + detail + "\n\n" + hint +
                           "\n请修复后重新打开本程序，或查看 logs 目录了解详情。",
                    ForeColor = _scheme.OnSurface,
                    Font = new Font("Segoe UI", 12F),
                    Dock = DockStyle.Top,
                    Height = 220,
                    TextAlign = ContentAlignment.TopLeft
                };

                var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44 };
                var btnDownload = new Button
                {
                    Text = "打开 WebView2 下载页",
                    Width = 200,
                    Height = 44,
                    Left = 0,
                    Top = 0,
                    BackColor = _accent,
                    ForeColor = _onAccent,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnDownload.FlatAppearance.BorderSize = 0;
                btnDownload.Click += (s, ev) => SafeOpenUrl("https://go.microsoft.com/fwlink/p/?LinkId=2124703");

                var btnLogs = new Button
                {
                    Text = "打开日志目录",
                    Width = 140,
                    Height = 44,
                    Left = 216,
                    Top = 0,
                    BackColor = _scheme.SurfaceContainerHighest,
                    ForeColor = _scheme.OnSurface,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnLogs.FlatAppearance.BorderSize = 0;
                btnLogs.Click += (s, ev) => SafeOpenUrl(Logger.LogDirectory);

                bottom.Controls.Add(btnDownload);
                bottom.Controls.Add(btnLogs);
                p.Controls.Add(lbl);
                p.Controls.Add(bottom);
                Controls.Add(p);
            }));
        }

        private static void SafeOpenUrl(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch { }
        }

        // ---- 工具 ----

        private static T Deserialize<T>(string json)
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(ms);
        }

        private static string JsonString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append("\"");
            return sb.ToString();
        }

        private static string ColorToHex(Color c) =>
            "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

        /// <summary>
        /// MD3 暗色主题整套角色色板（由 source color 生成）。
        /// </summary>
        private sealed class Md3Scheme
        {
            public Color Surface, OnSurface, SurfaceContainer, SurfaceContainerHigh, SurfaceContainerHighest,
                         SurfaceVariant, OnSurfaceVariant, Outline,
                         Primary, OnPrimary, PrimaryContainer, OnPrimaryContainer;
        }

        /// <summary>
        /// 按 Material Design 3 官方暗色 tone 角色映射，从源色（Windows 强调色）生成整套配色。
        /// 官方 baseline 精确 hex（source #6750A4）见 ConfigPage.html 的 :root 静态兜底：
        /// surface=#1C1B1F(tone6) / surface-container=#211F26(tone11) / surface-container-high=#2B2930(tone17) /
        /// surface-container-highest=#36343B(tone22) / surface-variant=#49454F(tone30) /
        /// on-surface=#E6E1E5(tone90) / on-surface-variant=#CAC4D0(tone80) / outline=#938F99(tone60) /
        /// primary=#D0BCFF(tone80) / on-primary=#381E72(tone20) / primary-container=#4F378B(tone30) /
        /// on-primary-container=#EADDFF(tone90)。
        ///
        /// 实现：主色板沿用源色相/饱和度，按 MD3 暗色 tone 映射（primary=80/on-primary=20/
        /// primary-container=30/on-primary-container=90）；中性板用同源色相、低饱和度。
        /// 注意：HSL 的 L 通道 ≠ M3 的 tone（HCT/CAM16 亮度），这里用 HSL-L 近似 tone 阶梯
        /// （surface≈L11 / container≈L13 / high≈L18 / highest=L22 / variant=L30）以贴近官方观感；
        /// 完整 HCT 需约千行代码，对主题化已足够。
        /// </summary>
        private static Md3Scheme GenerateMd3DarkScheme(Color source)
        {
            RgbToHsl(source, out double h, out double s, out _);
            // 主色板：沿用源色相与饱和度，按 MD3 暗色 tone 映射
            Color primary = HslToColor(h, s, 80);
            Color onPrimary = HslToColor(h, s, 20);
            Color primaryContainer = HslToColor(h, s, 30);
            Color onPrimaryContainer = HslToColor(h, s, 90);
            // 中性板：同源色相 + 低饱和度。系数参考 baseline #6750A4 反推：
            // surface(s≈7%)、container(s≈10%)、on-surface-variant(s≈11%)、outline(s≈5%)。
            double ns = Math.Min(s * 0.23, 8.0);   // 表面/容器：微色偏，封顶避免过艳
            double nvs = Math.Min(s * 0.33, 12.0); // on-surface-variant 可稍明显
            double os = Math.Min(s * 0.14, 5.0);   // 描边：极低色偏
            Color surface = HslToColor(h, ns, 11);
            Color onSurface = HslToColor(h, ns, 90);
            Color surfaceContainer = HslToColor(h, ns, 13);
            Color surfaceContainerHigh = HslToColor(h, ns, 18);
            Color surfaceContainerHighest = HslToColor(h, ns, 22);
            Color surfaceVariant = HslToColor(h, ns, 30);
            Color onSurfaceVariant = HslToColor(h, nvs, 80);
            Color outline = HslToColor(h, os, 60);
            return new Md3Scheme
            {
                Surface = surface, OnSurface = onSurface,
                SurfaceContainer = surfaceContainer, SurfaceContainerHigh = surfaceContainerHigh,
                SurfaceContainerHighest = surfaceContainerHighest,
                SurfaceVariant = surfaceVariant, OnSurfaceVariant = onSurfaceVariant,
                Outline = outline,
                Primary = primary, OnPrimary = onPrimary,
                PrimaryContainer = primaryContainer, OnPrimaryContainer = onPrimaryContainer
            };
        }

        private static void RgbToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double dl = max - min;
            l = (max + min) / 2;
            if (dl < 1e-6) { h = 0; s = 0; }
            else
            {
                s = l > 0.5 ? dl / (2 - max - min) : dl / (max + min);
                if (max == r) h = (g - b) / dl + (g < b ? 6 : 0);
                else if (max == g) h = (b - r) / dl + 2;
                else h = (r - g) / dl + 4;
                h *= 60;
                if (h < 0) h += 360;
            }
            s *= 100; l *= 100;
        }

        private static Color HslToColor(double h, double s, double l)
        {
            s /= 100; l /= 100;
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = l - c / 2;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            int rr = ClampByte((r + m) * 255);
            int gg = ClampByte((g + m) * 255);
            int bb = ClampByte((b + m) * 255);
            return Color.FromArgb(255, (byte)rr, (byte)gg, (byte)bb);
        }

        private static int ClampByte(double v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return (int)Math.Round(v);
        }

        // ---- 桥接消息模型 ----

        [DataContract]
        private class WebMsg
        {
            [DataMember(Name = "cmd")] public string Cmd { get; set; }
            [DataMember(Name = "data")] public SaveData Data { get; set; }
            // 实时保存（字段 change 触发）为 true：成功回执不弹 toast，避免每次改动都闪提示；
            // 显式动作（如恢复默认）为 false，保留成功提示。
            [DataMember(Name = "silent")] public bool Silent { get; set; }
            // 宿主桥接请求的操作名（cmd=="host" 时有效），如 getConfig/setConfig/areAnimationsEnabled
            [DataMember(Name = "op")] public string Op { get; set; }
            // 宿主桥接请求的唯一 id，C# 回执时原样带回，前端据此配对 Promise
            [DataMember(Name = "id")] public int Id { get; set; }
        }

        [DataContract]
        private class SaveData
        {
            [DataMember(Name = "lockSeconds")] public string LockSeconds { get; set; }
            [DataMember(Name = "logRetentionDays")] public string LogRetentionDays { get; set; }
            [DataMember(Name = "slogan")] public string Slogan { get; set; }
            [DataMember(Name = "enableUiAccess")] public bool EnableUiAccess { get; set; }
            [DataMember(Name = "password")] public string Password { get; set; }
            // 宿主弹窗（C# 主动触发）的用户操作结果，由前端 cmd:"dialogResult" 回传
            [DataMember(Name = "buttonId")] public string ButtonId { get; set; }
        }
    }
}
