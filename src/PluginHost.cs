using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

    /// <summary>插件清单（plugins/&lt;id&gt;/plugin.json）。</summary>
    [DataContract]
    internal sealed class PluginManifest
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
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
        [DataMember(Name = "pages")] public List<PluginPage> Pages { get; set; }
    }

    /// <summary>
    /// 插件加载器：扫描 exe 同目录下的 plugins/ 子目录，读取每个插件的 plugin.json 与其页面片段，
    /// 序列化为前端 <c>window.__registerPlugins</c> 可用的 JSON。
    /// <para>信任模型：插件为本地磁盘上的可信代码（与本地 exe/插件同级），由用户/开发者放置；
    /// 框架不联网下载、不沙箱隔离。插件页面 HTML 经前端 innerHTML 注入（不执行 &lt;script&gt;，
    /// 但会解析 on* 属性），故仅应加载可信来源的插件。插件如需脚本交互，使用 data-plugin-init 钩子。</para>
    /// </summary>
    internal static class PluginHost
    {
        public static List<PluginInfo> LoadAll()
        {
            var result = new List<PluginInfo>();
            string root;
            try { root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins"); }
            catch (Exception ex) { Debug.WriteLine("插件根目录解析失败: " + ex.Message); return result; }
            if (!Directory.Exists(root)) return result;

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

                var info = new PluginInfo { Id = manifest.Id, Name = manifest.Name, Pages = new List<PluginPage>() };
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
    }
}
