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
        // 插件要求的宿主 API 主版本（如 "1"）；留空表示兼容当前版本
        [DataMember(Name = "apiVersion")] public string ApiVersion { get; set; }
        // 依赖的其它插件 id 列表（必选；缺失/禁用/异常则本插件标 Error 不加载）
        [DataMember(Name = "dependencies")] public List<string> Dependencies { get; set; }
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
        [DataMember(Name = "dependencies")] public List<string> Dependencies { get; set; }
        [DataMember(Name = "apiVersion")] public string ApiVersion { get; set; }
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

        /// <summary>当前宿主插件 API 主版本。插件 plugin.json 的 apiVersion 主版本不得高于此值。</summary>
        internal const string CurrentApiVersion = "1";

        /// <summary>解析插件主版本号（取首个数字段），无法解析时按 1 处理（宽容对待旧插件）。</summary>
        private static int MajorVersion(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return 1;
            var part = v.Split('.')[0];
            int m;
            return int.TryParse(part, out m) ? m : 1;
        }

        /// <summary>插件要求的 apiVersion 是否与当前宿主兼容（旧版本插件向后兼容）。</summary>
        private static bool IsApiVersionCompatible(string v)
        {
            return MajorVersion(v) <= MajorVersion(CurrentApiVersion);
        }

        /// <summary>插件原始解析记录（含错误与禁用状态）。</summary>
        private sealed class RawPlugin
        {
            public PluginManifest Manifest;
            public string Dir;
            public bool Disabled;
            public string Error; // 非空表示不可加载
            public bool Loadable { get { return Error == null && !Disabled && Manifest != null; } }
        }

        /// <summary>扫描并解析所有插件清单，计算禁用/错误/依赖状态（不保证加载顺序）。</summary>
        private static Dictionary<string, RawPlugin> ParseAll()
        {
            var map = new Dictionary<string, RawPlugin>();
            string root = PluginsRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return map;

            foreach (var dir in Directory.GetDirectories(root))
            {
                // 已标记卸载的插件：本次忽略，下次启动 CleanupUninstall 会真正删除
                if (File.Exists(Path.Combine(dir, ".uninstall"))) continue;
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
                    map[Path.GetFileName(dir)] = new RawPlugin { Dir = dir, Manifest = null, Disabled = File.Exists(Path.Combine(dir, ".disabled")), Error = "清单解析失败：" + ex.Message };
                    continue;
                }
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id)) continue;
                bool disabled = File.Exists(Path.Combine(dir, ".disabled"));
                map[manifest.Id] = new RawPlugin { Manifest = manifest, Dir = dir, Disabled = disabled };
            }

            // apiVersion 不兼容（独立于依赖链，先标）
            foreach (var rp in map.Values)
            {
                if (rp.Manifest == null || rp.Error != null) continue;
                if (!IsApiVersionCompatible(rp.Manifest.ApiVersion))
                    rp.Error = "API 版本不兼容（要求 apiVersion " + (rp.Manifest.ApiVersion ?? "?") + "，宿主为 " + CurrentApiVersion + "）";
            }

            // 依赖缺失/禁用/异常 + 循环依赖检测（递归传播）
            foreach (var rp in map.Values)
                PropagateDepErrors(rp, map, new HashSet<string>());

            return map;
        }

        /// <summary>递归解析依赖链，缺失/禁用/异常/循环则标记 Error。</summary>
        private static void PropagateDepErrors(RawPlugin rp, Dictionary<string, RawPlugin> map, HashSet<string> stack)
        {
            if (rp.Error != null || rp.Manifest == null) return;
            if (stack.Contains(rp.Manifest.Id)) { rp.Error = "循环依赖（与依赖项互相引用）"; return; }
            stack.Add(rp.Manifest.Id);
            if (rp.Manifest.Dependencies != null)
            {
                foreach (var dep in rp.Manifest.Dependencies)
                {
                    if (string.IsNullOrWhiteSpace(dep)) continue;
                    RawPlugin depRp;
                    if (!map.TryGetValue(dep, out depRp) || depRp.Manifest == null) { rp.Error = "缺少依赖：" + dep; stack.Remove(rp.Manifest.Id); return; }
                    if (depRp.Disabled) { rp.Error = "依赖未启用：" + dep; stack.Remove(rp.Manifest.Id); return; }
                    PropagateDepErrors(depRp, map, stack);
                    if (depRp.Error != null) { rp.Error = "依赖异常：" + dep; stack.Remove(rp.Manifest.Id); return; }
                }
            }
            stack.Remove(rp.Manifest.Id);
        }

        /// <summary>对可加载插件做依赖拓扑排序，返回加载顺序（依赖在前）。</summary>
        private static List<RawPlugin> TopoOrder(Dictionary<string, RawPlugin> map)
        {
            var loadables = map.Values.Where(r => r.Loadable).ToList();
            var indegree = new Dictionary<string, int>();
            var dependents = new Dictionary<string, List<RawPlugin>>();
            foreach (var r in loadables)
            {
                indegree[r.Manifest.Id] = 0;
                dependents[r.Manifest.Id] = new List<RawPlugin>();
            }
            foreach (var r in loadables)
            {
                foreach (var dep in r.Manifest.Dependencies ?? new List<string>())
                {
                    if (dependents.ContainsKey(dep))
                    {
                        indegree[r.Manifest.Id]++;
                        dependents[dep].Add(r);
                    }
                }
            }
            var q = new Queue<RawPlugin>(loadables.Where(r => indegree[r.Manifest.Id] == 0));
            var order = new List<RawPlugin>();
            while (q.Count > 0)
            {
                var r = q.Dequeue();
                order.Add(r);
                foreach (var d in dependents[r.Manifest.Id])
                    if (--indegree[d.Manifest.Id] == 0) q.Enqueue(d);
            }
            foreach (var r in loadables) if (!order.Contains(r)) order.Add(r); // 兜底（环已标 error，正常到不了）
            return order;
        }

        /// <summary>列出已安装插件（用于管理页），含禁用/错误状态与依赖信息。</summary>
        public static List<PluginView> ListPlugins()
        {
            var map = ParseAll();
            var views = new List<PluginView>();
            foreach (var rp in map.Values)
            {
                if (rp.Manifest == null)
                {
                    views.Add(new PluginView { Id = Path.GetFileName(rp.Dir), Name = "(清单解析失败)", Enabled = false, DisabledFile = rp.Disabled, Error = rp.Error });
                    continue;
                }
                views.Add(new PluginView
                {
                    Id = rp.Manifest.Id,
                    Name = rp.Manifest.Name,
                    Version = rp.Manifest.Version ?? "1.0.0",
                    Author = rp.Manifest.Author ?? "未知作者",
                    Description = rp.Manifest.Description ?? "",
                    Enabled = !rp.Disabled,
                    DisabledFile = rp.Disabled,
                    Error = rp.Error,
                    Dependencies = rp.Manifest.Dependencies ?? new List<string>(),
                    ApiVersion = rp.Manifest.ApiVersion ?? ""
                });
            }
            return views;
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

        /// <summary>从 zip 内仅读取 plugin.json 解析为清单（不解压全部），供安装预览与取真实 id。</summary>
        public static PluginManifest PeekManifest(string zipPath, out string error)
        {
            error = null;
            try
            {
                using (var za = ZipFile.OpenRead(zipPath))
                {
                    ZipArchiveEntry entry = null;
                    foreach (var e in za.Entries)
                    {
                        // 顶层 <id>/plugin.json（或根 plugin.json）
                        if (e.FullName.EndsWith("plugin.json", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(e.Name))
                        {
                            entry = e;
                            break;
                        }
                    }
                    if (entry == null) { error = "zip 内未找到 plugin.json"; return null; }
                    using (var ms = new MemoryStream())
                    {
                        entry.Open().CopyTo(ms);
                        ms.Position = 0;
                        // 兼容带 UTF-8 BOM 的文件（记事本/PowerShell Set-Content 默认带 BOM）
                        byte[] bom = System.Text.Encoding.UTF8.GetPreamble();
                        if (ms.Length >= bom.Length)
                        {
                            byte[] head = new byte[bom.Length];
                            ms.Read(head, 0, bom.Length);
                            bool hasBom = true;
                            for (int i = 0; i < bom.Length; i++)
                                if (head[i] != bom[i]) { hasBom = false; break; }
                            if (!hasBom) ms.Position = 0; // 没有 BOM，回到开头
                        }
                        return (PluginManifest)new DataContractJsonSerializer(typeof(PluginManifest)).ReadObject(ms);
                    }
                }
            }
            catch (Exception ex) { error = ex.Message; return null; }
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
            var map = ParseAll();
            var order = TopoOrder(map);
            var result = new List<PluginInfo>();
            foreach (var rp in order)
            {
                if (!rp.Loadable || rp.Manifest == null) continue;
                string iconHtml = null;
                if (!string.IsNullOrWhiteSpace(rp.Manifest.Icon))
                {
                    string iconPath = Path.Combine(rp.Dir, rp.Manifest.Icon);
                    if (File.Exists(iconPath)) iconHtml = File.ReadAllText(iconPath);
                }
                var info = new PluginInfo { Id = rp.Manifest.Id, Name = rp.Manifest.Name, Icon = iconHtml, Pages = new List<PluginPage>() };
                if (rp.Manifest.Pages != null)
                {
                    foreach (var pm in rp.Manifest.Pages)
                    {
                        if (pm == null || string.IsNullOrWhiteSpace(pm.Src)) continue;
                        string htmlPath = Path.Combine(rp.Dir, pm.Src);
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
