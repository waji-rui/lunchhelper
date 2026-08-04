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
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LunchHelper
{
    /// <summary>
    /// 离线 Web UI 共享基建：供配置页（ConfigForm）与锁屏页（LockFormWeb）复用，
    /// 统一「内嵌资源读取 + 占位符注入」「MD3 暗色动态强调色」「WebView2 可用预检」「桥接消息工具」。
    /// 这里的 MD3 配色算法与 ConfigForm 内 private 实现同源同参（统一从 Windows 强调色生成），
    /// 保证锁屏页与配置页的 accent / surface 等角色 100% 一致。ConfigForm 自身逻辑保持不动。
    /// </summary>
    internal static class WebUiShell
    {
        // ---------- 资源读取与 HTML 拼装 ----------

        /// <summary>读取指定后缀的内嵌资源文本（找不到返回 null）。</summary>
        internal static string ReadEmbedded(Assembly asm, string suffix)
        {
            string name = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            using (var s = asm.GetManifestResourceStream(name))
            using (var r = new StreamReader(s))
                return r.ReadToEnd();
        }

        /// <summary>
        /// 读取页面骨架（内嵌资源），并将其中的占位符按 replacements 替换为对应内嵌资源内容。
        /// replacements：键为页面里的占位符字符串（如 "/*CSS_TOKENS*/"），值为资源后缀（如 "ui.tokens.css"）。
        /// </summary>
        internal static string BuildHtml(Assembly asm, string pageResource, Dictionary<string, string> replacements)
        {
            string html = ReadEmbedded(asm, pageResource);
            if (html == null) return FallbackHtml();
            foreach (var kv in replacements)
            {
                string content = ReadEmbedded(asm, kv.Value) ?? "";
                html = html.Replace(kv.Key, content);
            }
            return html;
        }

        /// <summary>资源缺失时的兜底页面（不白屏，给出可读提示）。</summary>
        internal static string FallbackHtml() =>
            "<html><body style='font-family:Segoe UI;background:#1C1B1F;color:#E6E1E5;padding:1.5rem'>未找到内嵌页面资源。</body></html>";

        // ---------- MD3 暗色动态强调色（与配置页同算法同源） ----------

        /// <summary>
        /// 生成注入到锁屏页的 &lt;style id="md3-theme"&gt; 块：把从 Windows 强调色算出的整套 MD3 暗色角色
        /// 写入 :root，覆盖 tokens.css 里的静态默认值。变量集合与 ConfigForm.BuildConfigJson 完全一致，
        /// 确保锁屏页与配置页视觉同源。
        /// </summary>
        internal static string BuildMd3ThemeStyle()
        {
            var s = GenerateMd3DarkScheme(NativeMethods.GetAccentColor());
            var sb = new StringBuilder();
            sb.Append("<style id=\"md3-theme\">:root{");
            sb.Append("--accent:").Append(ColorToHex(s.Primary)).Append(';');
            sb.Append("--on-accent:").Append(ColorToHex(s.OnPrimary)).Append(';');
            sb.Append("--accent-container:").Append(ColorToHex(s.PrimaryContainer)).Append(';');
            sb.Append("--on-accent-container:").Append(ColorToHex(s.OnPrimaryContainer)).Append(';');
            sb.Append("--surface:").Append(ColorToHex(s.Surface)).Append(';');
            sb.Append("--on-surface:").Append(ColorToHex(s.OnSurface)).Append(';');
            sb.Append("--surface-container:").Append(ColorToHex(s.SurfaceContainer)).Append(';');
            sb.Append("--surface-container-high:").Append(ColorToHex(s.SurfaceContainerHigh)).Append(';');
            sb.Append("--surface-container-highest:").Append(ColorToHex(s.SurfaceContainerHighest)).Append(';');
            sb.Append("--surface-variant:").Append(ColorToHex(s.SurfaceVariant)).Append(';');
            sb.Append("--on-surface-variant:").Append(ColorToHex(s.OnSurfaceVariant)).Append(';');
            sb.Append("--outline:").Append(ColorToHex(s.Outline)).Append(';');
            sb.Append("}</style>");
            return sb.ToString();
        }

        /// <summary>
        /// 与 BuildMd3ThemeStyle 同源，但改为「运行时把整套 MD3 角色写到 documentElement 行内样式」。
        /// 行内样式优先级高于任何 :root 样式表规则（含 tokens.css 静态兜底与本类的 &lt;style&gt; 注入），
        /// 与 ConfigForm.applyConfig 采用同一手法，确保锁屏强调色/表面色在无白闪前提下 100% 跟随 Windows。
        /// </summary>
        internal static string BuildMd3ThemeScript()
        {
            var s = GenerateMd3DarkScheme(NativeMethods.GetAccentColor());
            var sb = new StringBuilder("var r=document.documentElement.style;");
            Action<string, Color> set = (name, c) =>
                sb.Append("r.setProperty('").Append(name).Append("','").Append(ColorToHex(c)).Append("');");
            set("--accent", s.Primary);
            set("--on-accent", s.OnPrimary);
            set("--accent-container", s.PrimaryContainer);
            set("--on-accent-container", s.OnPrimaryContainer);
            set("--surface", s.Surface);
            set("--on-surface", s.OnSurface);
            set("--surface-container", s.SurfaceContainer);
            set("--surface-container-high", s.SurfaceContainerHigh);
            set("--surface-container-highest", s.SurfaceContainerHighest);
            set("--surface-variant", s.SurfaceVariant);
            set("--on-surface-variant", s.OnSurfaceVariant);
            set("--outline", s.Outline);
            return sb.ToString();
        }

        private static string ColorToHex(Color c) =>
            "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

        /// <summary>MD3 暗色主题整套角色色板（由 source color 生成）。算法与 ConfigForm 一致。</summary>
        private sealed class Md3Scheme
        {
            public Color Surface, OnSurface, SurfaceContainer, SurfaceContainerHigh, SurfaceContainerHighest,
                         SurfaceVariant, OnSurfaceVariant, Outline,
                         Primary, OnPrimary, PrimaryContainer, OnPrimaryContainer;
        }

        /// <summary>
        /// 按 Material Design 3 官方暗色 tone 角色映射，从源色（Windows 强调色）生成整套配色。
        /// 与 ConfigForm.GenerateMd3DarkScheme 同源；HSL-L 近似 tone 阶梯（surface≈L11 / container≈L13 /
        /// high≈L18 / highest=L22 / variant=L30），对主题化已足够。
        /// </summary>
        private static Md3Scheme GenerateMd3DarkScheme(Color source)
        {
            RgbToHsl(source, out double h, out double s, out _);
            Color primary = HslToColor(h, s, 80);
            Color onPrimary = HslToColor(h, s, 20);
            Color primaryContainer = HslToColor(h, s, 30);
            Color onPrimaryContainer = HslToColor(h, s, 90);
            double ns = Math.Min(s * 0.23, 8.0);
            double nvs = Math.Min(s * 0.33, 12.0);
            double os = Math.Min(s * 0.14, 5.0);
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

        // ---------- WebView2 可用预检 ----------

        /// <summary>探测本机能否用 WebView2 承载锁屏：运行时存在且 exe 目录可写（UserDataFolder 需要写权限）。</summary>
        internal static bool IsWebView2Available()
        {
            try
            {
                string ver = CoreWebView2Environment.GetAvailableBrowserVersionString();
                if (string.IsNullOrWhiteSpace(ver)) return false;
                return IsExeDirWritable();
            }
            catch { return false; }
        }

        private static bool IsExeDirWritable()
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                string probe = Path.Combine(dir, ".lh_writetest_" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var fs = File.Create(probe)) { fs.WriteByte(0); }
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        // ---------- 锁屏桥接消息工具 ----------

        /// <summary>锁屏页 → C# 的消息模型（cmd 恒为 "host"，由 op 区分操作）。</summary>
        [DataContract]
        internal class LockMsg
        {
            [DataMember(Name = "cmd")] public string Cmd { get; set; }
            [DataMember(Name = "id")] public int Id { get; set; }
            [DataMember(Name = "op")] public string Op { get; set; }
            [DataMember(Name = "data")] public LockData Data { get; set; }
        }

        [DataContract]
        internal class LockData
        {
            [DataMember(Name = "code")] public string Code { get; set; }
        }

        internal static T Deserialize<T>(string json)
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(ms);
        }

        /// <summary>把桥接结果回传给前端（前端经 window.__hostResult 按 id 配对 Promise）。</summary>
        internal static void SendHostResult(CoreWebView2 wv, int id, bool ok, string payloadJson, string error = null)
        {
            if (wv == null) return;
            string payload = ok ? (payloadJson ?? "null") : "null";
            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(id).Append(",\"ok\":").Append(ok ? "true" : "false")
              .Append(",\"payload\":").Append(payload);
            if (!ok) sb.Append(",\"error\":").Append(JsonString(error ?? "unknown error"));
            sb.Append("}");
            wv.ExecuteScriptAsync("window.__hostResult(" + sb + ")");
        }

        /// <summary>把字符串安全转成 JSON 字符串字面量（转义引号/反斜杠/控制字符）。</summary>
        internal static string JsonString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder();
            sb.Append('"');
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
            sb.Append('"');
            return sb.ToString();
        }
    }
}
