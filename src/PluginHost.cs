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
        [DataMember(Name = "icon")] public string Icon { get; set; }
        [DataMember(Name = "enabled")] public bool Enabled { get; set; }
        [DataMember(Name = "disabledFile")] public bool DisabledFile { get; set; }
        [DataMember(Name = "error")] public string Error { get; set; }
        [DataMember(Name = "dependencies")] public List<string> Dependencies { get; set; }
        [DataMember(Name = "apiVersion")] public string ApiVersion { get; set; }
        // 待卸载：已点卸载、写了 .uninstall 标记，重启才真正删除；重启前可见且可撤销。
        [DataMember(Name = "pendingUninstall")] public bool PendingUninstall { get; set; }
        // 待安装：zip 已放入 .pending 暂存区，重启才真正解压；重启前可见且可撤销。
        [DataMember(Name = "pendingInstall")] public bool PendingInstall { get; set; }
        // 本次启动时的实际生效状态（是否已被加载）。用户切换启禁后 .disabled 文件改变，但 EffectiveEnabled 不变，直到重启。
        [DataMember(Name = "effectiveEnabled")] public bool EffectiveEnabled { get; set; }
    }

    /// <summary>插件详情视图（三栏布局右侧详情面板）：在 PluginView 基础上补充 README 与图标。</summary>
    [DataContract]
    internal sealed class PluginDetailView
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "version")] public string Version { get; set; }
        [DataMember(Name = "author")] public string Author { get; set; }
        [DataMember(Name = "description")] public string Description { get; set; }
        // 插件目录下 README.md / readme.md / description.md 的原始 Markdown 文本（null 表示无）
        [DataMember(Name = "readme")] public string Readme { get; set; }
        // 图标 base64 data URI（如 "data:image/svg+xml;base64,..."），null 则前端用首字母占位
        [DataMember(Name = "icon")] public string Icon { get; set; }
        [DataMember(Name = "enabled")] public bool Enabled { get; set; }
        [DataMember(Name = "effectiveEnabled")] public bool EffectiveEnabled { get; set; }
        [DataMember(Name = "error")] public string Error { get; set; }
        [DataMember(Name = "dependencies")] public List<string> Dependencies { get; set; }
        [DataMember(Name = "apiVersion")] public string ApiVersion { get; set; }
        [DataMember(Name = "pendingUninstall")] public bool PendingUninstall { get; set; }
        [DataMember(Name = "pendingInstall")] public bool PendingInstall { get; set; }
        [DataMember(Name = "manifestOk")] public bool ManifestOk { get; set; }
    }

    /// <summary>
    /// 插件宿主：扫描 exe 同目录下的 plugins/，提供「已安装」列表、启禁、卸载、本地 zip 安装。
    /// </summary>
    internal static class PluginHost
    {
        /// <summary>本次启动实际已加载的插件 id 集合（用于管理页区分「当前生效」与「下次生效」）。</summary>
        internal static readonly HashSet<string> LoadedAtStartup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>LoadAll 是否已执行过。用于区分「ListPlugins 在 LoadAll 前被调用」与「本次启动确实没有任何可加载插件」，
        /// 避免后者误把已禁用插件的 EffectiveEnabled 算成 true（否则禁用态启用后不显示待生效）。</summary>
        internal static bool LoadAllRan = false;

        internal static string PluginsRoot()
        {
            try { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins"); }
            catch (Exception ex) { Debug.WriteLine("插件根目录解析失败: " + ex.Message); return null; }
        }

        /// <summary>当前宿主插件 API 主版本。插件 plugin.json 的 apiVersion 主版本不得高于此值。</summary>
        internal const string CurrentApiVersion = "1";

        /// <summary>校验插件 id 是否可作为安全的目录/文件名：不含路径分隔符、非法字符或 ".." 遍历，且不以点开头。</summary>
        private static bool IsSafeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            if (id.StartsWith(".")) return false;
            if (id.Contains("..")) return false;
            if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            return true;
        }

        /// <summary>按目录名或目录内 plugin.json 的 id 查找插件目录。
        /// 兼容目录名与 manifest id 不一致的情况（常见 zip 解压后目录名大小写/风格不同）。</summary>
        private static DirectoryInfo FindPluginDirectory(string root, string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !Directory.Exists(root)) return null;
            // 1. 优先按目录名匹配（错误插件的 Id 通常是目录名）
            var byName = new DirectoryInfo(root).GetDirectories()
                .FirstOrDefault(d => string.Equals(d.Name, id, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;
            // 2. 按目录内 plugin.json 的 id 匹配（manifest id 与目录名不一致时）
            foreach (var d in new DirectoryInfo(root).GetDirectories())
            {
                if (d.Name.StartsWith(".")) continue;
                string json = Path.Combine(d.FullName, "plugin.json");
                if (!File.Exists(json)) continue;
                try
                {
                    byte[] raw = File.ReadAllBytes(json);
                    using (var ms = new MemoryStream(raw))
                    {
                        // 兼容 UTF-8 BOM
                        byte[] bom = System.Text.Encoding.UTF8.GetPreamble();
                        if (ms.Length >= bom.Length)
                        {
                            byte[] head = new byte[bom.Length];
                            ms.Read(head, 0, bom.Length);
                            bool hasBom = true;
                            for (int i = 0; i < bom.Length; i++)
                                if (head[i] != bom[i]) { hasBom = false; break; }
                            if (!hasBom) ms.Position = 0;
                        }
                        var m = (PluginManifest)new DataContractJsonSerializer(typeof(PluginManifest)).ReadObject(ms);
                        if (m != null && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
                            return d;
                    }
                }
                catch { continue; }
            }
            return null;
        }

        /// <summary>解析插件主版本号（取首个数字段），无法解析时按 1 处理（宽容对待旧插件）。</summary>
        private static int MajorVersion(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return 1;
            var part = v.Split('.')[0];
            int m;
            return int.TryParse(part, out m) ? m : 1;
        }

        /// <summary>插件要求的 apiVersion 是否与当前宿主兼容（向后兼容）。</summary>
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
            public bool Uninstalling; // 已点卸载、待重启删除
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
                string dirName = Path.GetFileName(dir);
                // 跳过 .pending 等隐藏目录（待安装暂存区，不是插件目录）
                if (dirName.StartsWith(".")) continue;
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
                    map[Path.GetFileName(dir)] = new RawPlugin { Dir = dir, Manifest = null, Disabled = File.Exists(Path.Combine(dir, ".disabled")), Uninstalling = File.Exists(Path.Combine(dir, ".uninstall")), Error = "清单解析失败：" + ex.Message };
                    continue;
                }
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id)) continue;
                bool disabled = File.Exists(Path.Combine(dir, ".disabled"));
                // 待卸载：保留在列表中（Uninstalling=true），本会话仍正常加载/运行，
                // 下次启动才由 CleanupUninstall 真正删除。
                bool uninstalling = File.Exists(Path.Combine(dir, ".uninstall"));
                map[manifest.Id] = new RawPlugin { Manifest = manifest, Dir = dir, Disabled = disabled, Uninstalling = uninstalling };
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

        /// <summary>列出已安装插件（用于管理页），含禁用/错误状态、依赖信息与待卸载标记；并附加待安装的暂存项。</summary>
        public static List<PluginView> ListPlugins()
        {
            var map = ParseAll();
            var views = new List<PluginView>();
            foreach (var rp in map.Values)
            {
                if (rp.Manifest == null)
                {
                    views.Add(new PluginView { Id = Path.GetFileName(rp.Dir), Name = "(清单解析失败)", Enabled = false, DisabledFile = rp.Disabled, Error = rp.Error, PendingUninstall = rp.Uninstalling, EffectiveEnabled = false });
                    continue;
                }
                views.Add(new PluginView
                {
                    Id = rp.Manifest.Id,
                    Name = rp.Manifest.Name,
                    Version = rp.Manifest.Version ?? "1.0.0",
                    Author = rp.Manifest.Author ?? "未知作者",
                    Description = rp.Manifest.Description ?? "",
                    Icon = ReadIconAsDataUri(rp.Dir, rp.Manifest.Icon),
                    Enabled = !rp.Disabled,
                    DisabledFile = rp.Disabled,
                    Error = rp.Error,
                    Dependencies = rp.Manifest.Dependencies ?? new List<string>(),
                    ApiVersion = rp.Manifest.ApiVersion ?? "",
                    PendingUninstall = rp.Uninstalling,
                    // 若 LoadAll 尚未执行（LoadAllRan 为 false），按当前文件状态视为已生效，避免误显示待启用徽标；
                    // 一旦 LoadAll 执行过，则严格以「本次启动是否真正加载」为准（禁用插件不在集合内 → EffectiveEnabled=false）。
                    // 但插件本身存在错误（如 API 不兼容、依赖缺失）时，EffectiveEnabled 应与 Enabled 一致，避免永远卡在"待启用/待禁用"。
                    EffectiveEnabled = string.IsNullOrEmpty(rp.Error)
                        ? (LoadAllRan ? LoadedAtStartup.Contains(rp.Manifest.Id) : !rp.Disabled)
                        : !rp.Disabled
                });
            }
            // 待安装项（重启才真正解压）追加在已安装列表之后；若某 id 已安装（或待卸载），跳过避免重复显示
            var installedIds = new HashSet<string>(views.Select(v => v.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var pending in ListPendingInstalls())
            {
                if (!installedIds.Contains(pending.Id))
                {
                    views.Add(pending);
                    installedIds.Add(pending.Id);
                }
            }
            return views;
        }

        /// <summary>获取插件详情（含 README Markdown 与图标），用于三栏布局右侧详情面板。支持已安装目录与待安装 zip。</summary>
        public static PluginDetailView GetPluginDetails(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !IsSafeId(id)) return null;
            string root = PluginsRoot();
            if (string.IsNullOrEmpty(root)) return null;

            // 1) 优先从已安装目录读取
            var match = FindPluginDirectory(root, id);
            if (match != null) return GetInstalledPluginDetails(match);

            // 2) 未安装则尝试 .pending 暂存 zip
            string pendingZip = Path.Combine(root, ".pending", id + ".zip");
            if (File.Exists(pendingZip)) return GetPendingPluginDetails(pendingZip, id);

            return null;
        }

        private static PluginDetailView GetInstalledPluginDetails(DirectoryInfo dir)
        {
            PluginManifest manifest = null;
            string error = null;
            string jsonPath = Path.Combine(dir.FullName, "plugin.json");
            if (File.Exists(jsonPath))
            {
                try
                {
                    byte[] raw = File.ReadAllBytes(jsonPath);
                    using (var ms = new MemoryStream(raw))
                    {
                        // 兼容 UTF-8 BOM
                        byte[] bom = System.Text.Encoding.UTF8.GetPreamble();
                        if (ms.Length >= bom.Length)
                        {
                            byte[] head = new byte[bom.Length];
                            ms.Read(head, 0, bom.Length);
                            bool hasBom = true;
                            for (int i = 0; i < bom.Length; i++)
                                if (head[i] != bom[i]) { hasBom = false; break; }
                            if (!hasBom) ms.Position = 0;
                        }
                        manifest = (PluginManifest)new DataContractJsonSerializer(typeof(PluginManifest)).ReadObject(ms);
                    }
                }
                catch (Exception ex) { error = "清单解析失败：" + ex.Message; }
            }
            else error = "未找到 plugin.json";

            bool disabled = File.Exists(Path.Combine(dir.FullName, ".disabled"));
            bool uninstalling = File.Exists(Path.Combine(dir.FullName, ".uninstall"));
            if (manifest != null && string.IsNullOrEmpty(error))
            {
                if (!IsApiVersionCompatible(manifest.ApiVersion))
                    error = "API 版本不兼容（要求 apiVersion " + (manifest.ApiVersion ?? "?") + "，宿主为 " + CurrentApiVersion + "）";
            }

            // 补充依赖链错误（循环依赖 / 缺失 / 禁用 / 异常），与 ListPlugins / LoadAll 保持一致
            if (manifest != null && string.IsNullOrEmpty(error))
            {
                try
                {
                    var all = ParseAll();
                    RawPlugin rp;
                    if (all.TryGetValue(manifest.Id, out rp) && !string.IsNullOrEmpty(rp.Error))
                        error = rp.Error;
                }
                catch (Exception ex) { Logger.Warn("获取插件详情时依赖检测失败：" + ex.Message); }
            }

            var detail = new PluginDetailView
            {
                Id = manifest != null && !string.IsNullOrWhiteSpace(manifest.Id) ? manifest.Id : dir.Name,
                Name = manifest != null ? manifest.Name : dir.Name,
                Version = manifest != null ? (manifest.Version ?? "1.0.0") : "",
                Author = manifest != null ? (manifest.Author ?? "未知作者") : "",
                Description = manifest != null ? (manifest.Description ?? "") : "",
                Readme = ReadReadme(dir.FullName),
                Icon = manifest != null ? ReadIconAsDataUri(dir.FullName, manifest.Icon) : null,
                Enabled = !disabled,
                EffectiveEnabled = string.IsNullOrEmpty(error)
                    ? (LoadAllRan ? LoadedAtStartup.Contains(manifest != null ? manifest.Id : dir.Name) : !disabled)
                    : !disabled,
                Error = error,
                Dependencies = manifest != null ? (manifest.Dependencies ?? new List<string>()) : new List<string>(),
                ApiVersion = manifest != null ? (manifest.ApiVersion ?? "") : "",
                PendingUninstall = uninstalling,
                PendingInstall = false,
                ManifestOk = manifest != null && string.IsNullOrEmpty(error)
            };
            return detail;
        }

        private static PluginDetailView GetPendingPluginDetails(string zipPath, string id)
        {
            string err;
            PluginManifest manifest = PeekManifest(zipPath, out err);
            string readme = null;
            string icon = null;
            try
            {
                using (var za = ZipFile.OpenRead(zipPath))
                {
                    readme = ReadZipEntryText(za, "README.md") ?? ReadZipEntryText(za, "readme.md") ?? ReadZipEntryText(za, "description.md");
                    if (manifest != null && !string.IsNullOrWhiteSpace(manifest.Icon))
                        icon = ReadZipEntryBase64DataUri(za, manifest.Icon);
                }
            }
            catch { }
            return new PluginDetailView
            {
                Id = manifest != null && !string.IsNullOrWhiteSpace(manifest.Id) ? manifest.Id : id,
                Name = manifest != null ? manifest.Name : id,
                Version = manifest != null ? (manifest.Version ?? "1.0.0") : "",
                Author = manifest != null ? (manifest.Author ?? "未知作者") : "",
                Description = manifest != null ? (manifest.Description ?? "") : "",
                Readme = readme,
                Icon = icon,
                Enabled = true,
                EffectiveEnabled = false,
                Error = err,
                Dependencies = manifest != null ? (manifest.Dependencies ?? new List<string>()) : new List<string>(),
                ApiVersion = manifest != null ? (manifest.ApiVersion ?? "") : "",
                PendingUninstall = false,
                PendingInstall = true,
                ManifestOk = manifest != null && string.IsNullOrEmpty(err)
            };
        }

        private static string ReadReadme(string dir)
        {
            foreach (var name in new[] { "README.md", "readme.md", "description.md", "Description.md" })
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    try { return File.ReadAllText(path, System.Text.Encoding.UTF8); }
                    catch { }
                }
            }
            return null;
        }

        private static string ReadIconAsDataUri(string dir, string iconPath)
        {
            if (string.IsNullOrWhiteSpace(iconPath)) return null;
            string full = Path.Combine(dir, iconPath);
            try
            {
                // 防御目录穿越：icon 必须落在插件目录内（防止恶意 manifest 用 "../../x" 读取外部文件）
                string dirNorm = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string fullNorm = Path.GetFullPath(full);
                if (!fullNorm.StartsWith(dirNorm, StringComparison.OrdinalIgnoreCase)) return null;
                if (!File.Exists(full)) return null;
                byte[] data = File.ReadAllBytes(full);
                string mime = MimeFromExtension(Path.GetExtension(full));
                return "data:" + mime + ";base64," + Convert.ToBase64String(data);
            }
            catch { return null; }
        }

        private static string ReadZipEntryText(ZipArchive za, string fileName)
        {
            foreach (var e in za.Entries)
            {
                if (string.Equals(Path.GetFileName(e.FullName), fileName, StringComparison.OrdinalIgnoreCase) && e.Length > 0)
                {
                    try
                    {
                        using (var s = e.Open())
                        using (var r = new StreamReader(s, System.Text.Encoding.UTF8))
                            return r.ReadToEnd();
                    }
                    catch { }
                }
            }
            return null;
        }

        private static string ReadZipEntryBase64DataUri(ZipArchive za, string fileName)
        {
            foreach (var e in za.Entries)
            {
                if (string.Equals(Path.GetFileName(e.FullName), fileName, StringComparison.OrdinalIgnoreCase) && e.Length > 0)
                {
                    try
                    {
                        using (var s = e.Open())
                        using (var ms = new MemoryStream())
                        {
                            s.CopyTo(ms);
                            string mime = MimeFromExtension(Path.GetExtension(e.FullName));
                            return "data:" + mime + ";base64," + Convert.ToBase64String(ms.ToArray());
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        private static string MimeFromExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return "application/octet-stream";
            switch (ext.ToLowerInvariant())
            {
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";
                default: return "application/octet-stream";
            }
        }

        /// <summary>读取 .pending 暂存区的 zip 包，作为“待安装”项返回（重启才生效）。</summary>
        private static List<PluginView> ListPendingInstalls()
        {
            var list = new List<PluginView>();
            string root = PluginsRoot();
            if (string.IsNullOrEmpty(root)) return list;
            string pendingDir = Path.Combine(root, ".pending");
            if (!Directory.Exists(pendingDir)) return list;
            foreach (var zip in Directory.GetFiles(pendingDir, "*.zip"))
            {
                string err;
                PluginManifest m = PeekManifest(zip, out err);
                if (m == null || string.IsNullOrWhiteSpace(m.Id)) continue;
                string icon = null;
                try
                {
                    using (var za = ZipFile.OpenRead(zip))
                        if (!string.IsNullOrWhiteSpace(m.Icon))
                            icon = ReadZipEntryBase64DataUri(za, m.Icon);
                }
                catch { }
                list.Add(new PluginView
                {
                    Id = m.Id,
                    Name = m.Name,
                    Version = m.Version ?? "1.0.0",
                    Author = m.Author ?? "未知作者",
                    Description = m.Description ?? "",
                    Icon = icon,
                    Enabled = true,
                    DisabledFile = false,
                    Error = null,
                    Dependencies = m.Dependencies ?? new List<string>(),
                    ApiVersion = m.ApiVersion ?? "",
                    PendingInstall = true
                });
            }
            return list;
        }

        /// <summary>启用/禁用插件：写/删 .disabled 标记（下次启动才真正生效；当前会话保持原状态）。</summary>
        public static bool SetEnabled(string id, bool enable)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            string root = PluginsRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
            var match = FindPluginDirectory(root, id);
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

        /// <summary>卸载插件：写 .uninstall 标记，下次启动由 CleanupUninstall 真正删除目录。返回 null 表示成功，否则为错误描述。</summary>
        public static string Uninstall(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "插件 id 为空。";
            string root = PluginsRoot();
            if (string.IsNullOrEmpty(root)) return "插件根目录解析失败。";
            if (!Directory.Exists(root)) return "插件目录不存在：" + root;
            var match = FindPluginDirectory(root, id);
            if (match == null) return "未找到插件目录：" + id;
            string uninstallPath = Path.Combine(match.FullName, ".uninstall");
            try { File.WriteAllText(uninstallPath, ""); }
            catch (Exception ex) { return "写卸载标记失败：" + ex.Message; }
            return null;
        }

        /// <summary>撤销卸载：删除 .uninstall 标记，使插件恢复为已安装（重启前可撤销）。</summary>
        public static string CancelUninstall(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "插件 id 为空。";
            string root = PluginsRoot();
            if (string.IsNullOrEmpty(root)) return "插件根目录解析失败。";
            var match = FindPluginDirectory(root, id);
            if (match == null) return "未找到插件目录：" + id;
            string uninstallPath = Path.Combine(match.FullName, ".uninstall");
            if (!File.Exists(uninstallPath)) return null; // 本来就没标记，视为成功
            try { File.Delete(uninstallPath); }
            catch (Exception ex) { return "删除卸载标记失败：" + ex.Message; }
            return null;
        }

        /// <summary>强制删除：立即删除插件目录（用于清单解析失败等异常插件）。若文件被占用无法立即删除，则回退为写 .uninstall 标记延迟删除。返回 null 表示成功；返回 "DEFERRED:原因" 表示已改为延迟删除；其它为错误描述。</summary>
        public static string ForceDelete(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "插件 id 为空。";
            string root = PluginsRoot();
            if (string.IsNullOrEmpty(root)) return "插件根目录解析失败。";
            if (!Directory.Exists(root)) return "插件目录不存在：" + root;
            var match = FindPluginDirectory(root, id);
            if (match == null) return "未找到插件目录：" + id;
            try
            {
                DeleteDirectoryRecursive(match.FullName);
                return null;
            }
            catch (Exception ex)
            {
                // 立即删除失败（文件占用、权限不足等）：回退到延迟卸载，保证用户至少能清掉
                try { File.WriteAllText(Path.Combine(match.FullName, ".uninstall"), ""); }
                catch (Exception ex2)
                {
                    Logger.Error("强制删除失败且无法写入卸载标记 " + match.FullName + ": " + ex2.Message);
                    return "强制删除失败，且无法写入卸载标记：" + ex2.Message;
                }
                Logger.Warn("强制删除 " + match.FullName + " 立即删除失败，已改为重启后删除：" + ex.Message);
                return "DEFERRED:" + ex.Message;
            }
        }

        /// <summary>递归删除目录，先移除只读属性，避免 .NET DirectoryDelete(true) 在只读文件上失败。</summary>
        private static void DeleteDirectoryRecursive(string path)
        {
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if ((fi.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        fi.Attributes &= ~FileAttributes.ReadOnly;
                }
                catch { }
            }
            Directory.Delete(path, true);
        }

        /// <summary>启动时清理带 .uninstall 标记的插件目录（真正删除）。</summary>
        public static void CleanupUninstall()
        {
            string root = PluginsRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (Path.GetFileName(dir).StartsWith(".")) continue; // 跳过 .pending 等隐藏目录
                string uninstallPath = Path.Combine(dir, ".uninstall");
                if (File.Exists(uninstallPath))
                {
                    try { DeleteDirectoryRecursive(dir); }
                    catch (Exception ex) { Logger.Error("清理待卸载插件目录失败 " + dir + ": " + ex.Message); }
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

        /// <summary>把安装包暂存到 plugins/.pending/&lt;id&gt;.zip，重启时才真正解压（延迟安装）。返回 null 表示成功。</summary>
        public static string QueueInstall(string zipPath, string id)
        {
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath)) return "未找到 zip 文件。";
            if (string.IsNullOrWhiteSpace(id)) return "插件 id 为空。";
            if (!IsSafeId(id)) return "插件 id 含非法字符，无法安装。";
            string root = PluginsRoot();
            if (string.IsNullOrEmpty(root)) return "插件根目录不可用。";
            string pendingDir = Path.Combine(root, ".pending");
            try { Directory.CreateDirectory(pendingDir); }
            catch (Exception ex) { return "无法创建待安装目录：" + ex.Message; }
            string dest = Path.Combine(pendingDir, id + ".zip");
            try { File.Copy(zipPath, dest, true); }
            catch (Exception ex) { return "写入待安装包失败：" + ex.Message; }
            return null;
        }

        /// <summary>撤销待安装：删除 .pending/&lt;id&gt;.zip。返回 null 表示成功。</summary>
        public static string CancelPendingInstall(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "插件 id 为空。";
            string root = PluginsRoot();
            if (string.IsNullOrEmpty(root)) return "插件根目录不可用。";
            string pendingDir = Path.Combine(root, ".pending");
            if (!Directory.Exists(pendingDir)) return null;
            string dest = Path.Combine(pendingDir, id + ".zip");
            if (!File.Exists(dest)) return null; // 本就没有待安装项，视为成功
            try { File.Delete(dest); }
            catch (Exception ex) { return "删除待安装包失败：" + ex.Message; }
            return null;
        }

        /// <summary>判断 zip 是否以「唯一顶层目录」包裹（单插件 zip 常见形态）。
        /// 若是，返回该顶层目录（含尾部 '/'，统一为正斜杠）；否则返回 null（多插件 / 扁平 zip）。</summary>
        private static string DetectCommonTop(List<ZipArchiveEntry> entries)
        {
            if (entries == null || entries.Count == 0) return null;
            var names = entries
                .Select(e => (e.FullName ?? "").Replace('\\', '/'))
                .Where(n => n.Length > 0)
                .ToList();
            if (names.Count == 0) return null;
            // 存在根级文件（不含 '/'）→ 视为扁平 / 多插件，不剥离包裹目录
            if (names.Any(n => !n.Contains('/'))) return null;
            // 所有条目都含 '/'：取首个顶层目录段，若全部以此开头则为唯一包裹目录
            string firstTop = names[0].Split('/')[0];
            if (names.All(n => n.StartsWith(firstTop + "/")))
                return firstTop + "/";
            return null; // 多个并列顶层目录（如一个 zip 含多个插件）
        }

        /// <summary>把 zip 条目解压到 baseDir；stripPrefix 非空时先剥离该公共顶层前缀（需以 '/' 结尾）。
        /// 逐条校验目标路径仍落在 baseDir 内，防止 zip 穿越（zip-slip）。</summary>
        private static void ExtractEntries(ZipArchive za, string baseDir, string stripPrefix)
        {
            string fullBase = Path.GetFullPath(baseDir);
            foreach (var entry in za.Entries)
            {
                string full = (entry.FullName ?? "").Replace('\\', '/');
                if (string.IsNullOrEmpty(full)) continue;
                if (!string.IsNullOrEmpty(stripPrefix) && full.StartsWith(stripPrefix))
                    full = full.Substring(stripPrefix.Length);
                if (string.IsNullOrEmpty(full)) continue; // 被剥离的顶层目录自身
                string destPath = Path.GetFullPath(Path.Combine(baseDir, full));
                if (!destPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("插件包含非法路径，拒绝解压：" + entry.FullName);
                string dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (!string.IsNullOrEmpty(entry.Name)) entry.ExtractToFile(destPath, true);
            }
        }

        /// <summary>启动时把 .pending 暂存区的安装包解压到 plugins/（真正安装），并清理暂存包。须在 CleanupUninstall 之后调用。</summary>
        public static void ApplyPendingInstalls()
        {
            string root = PluginsRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            string pendingDir = Path.Combine(root, ".pending");
            if (!Directory.Exists(pendingDir)) return;
            foreach (var zip in Directory.GetFiles(pendingDir, "*.zip"))
            {
                string id = Path.GetFileNameWithoutExtension(zip);
                if (string.IsNullOrWhiteSpace(id) || !IsSafeId(id)) { try { File.Delete(zip); } catch { } continue; }
                try
                {
                    using (var za = ZipFile.OpenRead(zip))
                    {
                        var entries = za.Entries.Where(e => !string.IsNullOrEmpty(e.FullName)).ToList();
                        string commonTop = DetectCommonTop(entries);
                        if (commonTop != null)
                        {
                            // 单插件 zip（以同名目录包裹）：剥掉包裹层，解压到 plugins/<id>/，避免双重嵌套 plugins/<id>/<id>/plugin.json 导致插件“消失”。
                            string targetDir = Path.Combine(root, id);
                            if (Directory.Exists(targetDir)) DeleteDirectoryRecursive(targetDir);
                            Directory.CreateDirectory(targetDir);
                            ExtractEntries(za, targetDir, commonTop);
                        }
                        else
                        {
                            // 多插件 / 扁平 zip：保留 zip 顶层结构，直接解压到 plugins/（每个顶层目录即一个插件目录）。
                            ExtractEntries(za, root, null);
                        }
                    }
                    File.Delete(zip);
                }
                catch (Exception ex) { Logger.Error("应用待安装插件失败 " + zip + ": " + ex.Message); }
            }
        }

        // ---- 原有插件注册逻辑（保持不变） ----

        public static List<PluginInfo> LoadAll()
        {
            var map = ParseAll();
            var order = TopoOrder(map);
            LoadedAtStartup.Clear();
            var result = new List<PluginInfo>();
            foreach (var rp in order)
            {
                if (!rp.Loadable || rp.Manifest == null) continue;
                LoadedAtStartup.Add(rp.Manifest.Id);
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
            LoadAllRan = true; // 标记 LoadAll 已执行，后续 ListPlugins 以 LoadedAtStartup 为权威
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

        public static string ToJson(PluginDetailView detail)
        {
            if (detail == null) return "null";
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(PluginDetailView)).WriteObject(ms, detail);
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }
}
