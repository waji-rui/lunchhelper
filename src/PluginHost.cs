using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace LunchHelper
{
    /// <summary>插件清单中的页面描述（不含 html 正文，正文在 src 指向的片段文件里读取）。</summary>
    [DataContract]
    internal sealed class PluginPageManifest
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "src")] public string Src { get; set; }
    }

    /// <summary>插件清单（plugins/&lt;id&gt;/plugin.json），含管理元信息。</summary>
    [DataContract]
    internal sealed class PluginManifest
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "version")] public string Version { get; set; }
        [DataMember(Name = "author")] public string Author { get; set; }
        [DataMember(Name = "description")] public string Description { get; set; }
        // 插件页侧边栏图标（SVG 文件路径，相对插件根目录）；未指定时前端 fallback 到默认图标
        [DataMember(Name = "icon")] public string Icon { get; set; }
        [DataMember(Name = "pages")] public List<PluginPageManifest> Pages { get; set; }
    }

    /// <summary>注册到前端的最终页面对象（含已读入的 html 片段）。</summary>
    [DataContract]
    internal sealed class PluginPage
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "html")] public string Html { get; set; }
    }

    [DataContract]
    internal sealed class PluginInfo
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        // 侧边栏图标 SVG 字符串（plugin.json 的 icon 字段读取而来；null 则前端用默认图标）
        [DataMember(Name = "icon")] public string Icon { get; set; }
        [DataMember(Name = "pages")] public List<PluginPage> Pages { get; set; }
    }

    /// <summary>
    /// 插件管理视图模型（管理页「已安装」Tab 展示用）。
    /// </summary>
    [DataContract]
    internal sealed class PluginView
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "version")] public string Version { get; set; }
        [DataMember(Name = "author")] public string Author { get; set; }
        [DataMember(Name = "description")] public string Description { get; set; }
        [DataMember(Name = "enabled")] public bool Enabled { get; set; }
        [DataMember(Name = "disabledFile")] public bool DisabledFile { get; set; }
        [DataMember(Name = "error")] public string Error { get; set; }
    }

    /// <summary>
    /// 插件宿主：扫描 exe 同目录下的 plugins/，提供「已安装」列表、启禁、卸载、本地 zip 安装。
    /// </summary>
    internal static class PluginHost
    {
        internal static string PluginsRoot()
        {
            try { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins"); }
            catch (Exception ex) { Debug.WriteLine("插件根目录解析失败: " + ex.Message); return null; }
        }

        /// <summary>列出已安装插件（用于管理页），跳过被禁用/损坏的插件。</summary>
        public static List<PluginView> ListPlugins()
        {
            var result = new List<PluginView>();
            string root = PluginsRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;

            foreach (var dir in Directory.GetDirectories(root))
            {
                string jsonPath = Path.Combine(dir, "plugin.json");
                if (!File.Exists(jsonPath)) continue;

                string disabledPath = Path.Combine(dir, ".disabled");
                bool disabled = File.Exists(disabledPath);

                PluginManifest manifest;
                try
                {
                    using (var ms = new MemoryStream(File.ReadAllBytes(jsonPath)))
                        manifest = (PluginManifest)new DataContractJsonSerializer(typeof(PluginManifest)).ReadObject(ms);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("插件清单解析失败 " + jsonPath + ": " + ex.Message);
                    result.Add(new PluginView { Id = Path.GetFileName(dir), Name = "(清单解析失败)", Enabled = false, DisabledFile = disabled, Error = ex.Message });
                    continue;
                }
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id)) continue;

                result.Add(new PluginView
                {
                    Id = manifest.Id,
                    Name = manifest.Name,
                    Version = manifest.Version ?? "1.0.0",
                    Author = manifest.Author ?? "未知作者",
                    Description = manifest.Description ?? "",
                    Enabled = !disabled,
                    DisabledFile = disabled,
                    Error = null
                });
            }
            return result;
        }

        /// <summary>启用/禁用插件：写/删 .disabled 标记。</summary>
        public static bool SetEnabled(string id, bool enable)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            string root = PluginsRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
            var match = new DirectoryInfo(root).GetDirectories().FirstOrDefault(d => d.Name == id);
            if (match == null) return false;
            string disabledPath = Path.Combine(match.FullName, ".disabled");
            if (enable)
            {
                if (File.Exists(disabledPath))
                {
                    try { File.Delete(disabledPath); }
                    catch (Exception ex) { Debug.WriteLine(ex); return false; }
                }
            }
            else
            {
                if (!File.Exists(disabledPath))
                {
                    try { File.WriteAllText(disabledPath, ""); }
                    catch (Exception ex) { Debug.WriteLine(ex); return false; }
                }
            }
            return true;
        }

        /// <summary>卸载插件：写 .uninstall 标记，下次启动由 CleanupUninstall 真正删除目录。</summary>
        public static bool Uninstall(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            string root = PluginsRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
            var match = new DirectoryInfo(root).GetDirectories().FirstOrDefault(d => d.Name == id);
            if (match == null) return false;
            string uninstallPath = Path.Combine(match.FullName, ".uninstall");
            try { File.WriteAllText(uninstallPath, ""); }
            catch (Exception ex) { Debug.WriteLine(ex); return false; }
            return true;
        }

        /// <summary>启动时清理带 .uninstall 标记的插件目录（真正删除）。</summary>
        public static void CleanupUninstall()
        {
            string root = PluginsRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root))
            {
                string uninstallPath = Path.Combine(dir, ".uninstall");
                if (File.Exists(uninstallPath))
                {
                    try { Directory.Delete(dir, true); }
                    catch (Exception ex) { Debug.WriteLine("删除插件目录失败 " + dir + ": " + ex.Message); }
                }
            }
        }

        /// <summary>从 zip 安装插件（解压到 plugins/&lt;id&gt;）。</summary>
        public static string InstallFromZip(string zipPath, string id)
        {
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath)) return "未找到 zip 文件。";
            string root = PluginsRoot();
            if (string.IsNullOrEmpty(root)) return "插件根目录不可用。";
            string targetDir = Path.Combine(root, id);
            try
            {
                if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
                ZipFile.ExtractToDirectory(zipPath, root);
                return null;
            }
            catch (Exception ex) { return "安装失败：" + ex.Message; }
        }

        // ---- 原有插件注册逻辑（保持不变） ----

        public static List<PluginInfo> LoadAll()
        {
            var result = new List<PluginInfo>();
            string root = PluginsRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;

            foreach (var dir in Directory.GetDirectories(root))
            {
                string jsonPath = Path.Combine(dir, "plugin.json");
                if (!File.Exists(jsonPath)) continue;

                PluginManifest manifest;
                try
                {
                    using (var ms = new MemoryStream(File.ReadAllBytes(jsonPath)))
                        manifest = (PluginManifest)new DataContractJsonSerializer(typeof(PluginManifest)).ReadObject(ms);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("插件清单解析失败 " + jsonPath + ": " + ex.Message);
                    continue;
                }
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id)) continue;

                string iconHtml = null;
                if (!string.IsNullOrWhiteSpace(manifest.Icon))
                {
                    string iconPath = Path.Combine(dir, manifest.Icon);
                    if (File.Exists(iconPath)) iconHtml = File.ReadAllText(iconPath);
                }
                var info = new PluginInfo { Id = manifest.Id, Name = manifest.Name, Icon = iconHtml, Pages = new List<PluginPage>() };
                if (manifest.Pages != null)
                {
                    foreach (var pm in manifest.Pages)
                    {
                        if (pm == null || string.IsNullOrWhiteSpace(pm.Src)) continue;
                        string htmlPath = Path.Combine(dir, pm.Src);
                        string html = File.Exists(htmlPath) ? File.ReadAllText(htmlPath) : "";
                        info.Pages.Add(new PluginPage { Id = pm.Id, Title = pm.Title ?? pm.Id, Html = html });
                    }
                }
                result.Add(info);
            }
            return result;
        }

        public static string ToJson(List<PluginInfo> plugins)
        {
            if (plugins == null || plugins.Count == 0) return "[]";
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(List<PluginInfo>)).WriteObject(ms, plugins);
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static string ToJson(List<PluginView> plugins)
        {
            if (plugins == null || plugins.Count == 0) return "[]";
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(List<PluginView>)).WriteObject(ms, plugins);
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }
}
