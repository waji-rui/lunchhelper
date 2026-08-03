// markdown.js — LunchHelper 轻量 Markdown 渲染器（零第三方依赖）
// LunchHelper —— 触控锁屏助手
// Copyright (C) 2026  LunchHelper contributors
//
// 本程序是自由软件：你可以根据 GNU 通用公共许可证（版本 3 或更高版本，
// 由你选择）的条款重新发布和/或修改它。本程序按“原样”提供，不提供任何担保。
// 许可证副本见 <https://www.gnu.org/licenses/gpl-3.0.html>。
// 本文件依 GPL-3.0 发布。
// 支持：标题、段落、加粗/斜体、行内代码、代码块、无序/有序列表、链接、引用、分隔线。
// 输出已转义的安全 HTML。
(function (root, factory) {
  "use strict";
  if (typeof module !== "undefined" && module.exports) module.exports = factory();
  else root.Markdown = factory();
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  function escHtml(s) {
    return String(s || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function inline(md) {
    // 代码 `...`（最先处理，避免内部被转义）
    md = md.replace(/`([^`]+)`/g, function (_, code) {
      return "<code>" + escHtml(code) + "</code>";
    });
    // 加粗 **...** 或 __...__
    md = md.replace(/\*\*([^*]+)\*\*|__([^_]+)__/g, function (_, a, b) {
      return "<strong>" + (a !== undefined ? escHtml(a) : escHtml(b)) + "</strong>";
    });
    // 斜体 *...* 或 _..._（不重复处理已加粗的 *）
    md = md.replace(/(^|[^*])\*([^*]+)\*(?![*])/g, function (_, pre, txt) {
      return pre + "<em>" + escHtml(txt) + "</em>";
    });
    md = md.replace(/(^|[^_])_([^_]+)_(?![_])/g, function (_, pre, txt) {
      return pre + "<em>" + escHtml(txt) + "</em>";
    });
    // 链接 [text](url)：仅允许安全协议，禁止 javascript:/file: 等注入
    md = md.replace(/\[([^\]]+)\]\(([^)]+)\)/g, function (_, txt, url) {
      url = String(url || "").trim();
      if (!/^(https?:|mailto:|data:)/i.test(url)) return escHtml(txt); // 非安全协议：退化为纯文本，杜绝 XSS
      url = escHtml(url).replace(/"/g, "%22");
      return '<a href="' + url + '" target="_blank" rel="noopener noreferrer">' + escHtml(txt) + "</a>";
    });
    // 自动链接 <http://...>（简单支持）
    md = md.replace(/<(https?:\/\/[^>]+)>/g, function (_, url) {
      return '<a href="' + escHtml(url) + '" target="_blank" rel="noopener noreferrer">' + escHtml(url) + "</a>";
    });
    return md;
  }

  function trimLineEnd(s) { return s.replace(/[ \t]+$/g, ""); }

  function toHtml(src) {
    if (!src) return "";
    var lines = src.replace(/\r\n/g, "\n").replace(/\r/g, "\n").split("\n");
    var out = [];
    var i = 0;

    function flushPara(buf) {
      if (!buf.length) return;
      var text = buf.map(trimLineEnd).join(" ").replace(/\s+/g, " ").trim();
      if (text) out.push("<p>" + inline(escHtml(text)) + "</p>");
    }

    while (i < lines.length) {
      var line = lines[i];
      var trimmed = line.trim();

      // 空行
      if (!trimmed) { i++; continue; }

      // 分隔线
      if (/^(---|___|\*\*\*)$/.test(trimmed)) {
        out.push("<hr>");
        i++;
        continue;
      }

      // 代码块 ```
      if (trimmed.startsWith("```")) {
        var lang = trimmed.slice(3).trim();
        var code = [];
        i++;
        while (i < lines.length && !lines[i].trim().startsWith("```")) {
          code.push(lines[i]);
          i++;
        }
        if (i < lines.length) i++;
        out.push('<pre><code' + (lang ? ' class="lang-' + escHtml(lang) + '"' : "") + ">" + escHtml(code.join("\n")) + "</code></pre>");
        continue;
      }

      // 标题
      var hMatch = line.match(/^(#{1,6})\s+(.*)$/);
      if (hMatch) {
        var lvl = hMatch[1].length;
        out.push("<h" + lvl + ">" + inline(escHtml(hMatch[2].trim())) + "</h" + lvl + ">");
        i++;
        continue;
      }

      // 引用
      if (trimmed.startsWith(">")) {
        var quote = [];
        while (i < lines.length && lines[i].trim().startsWith(">")) {
          quote.push(lines[i].trim().slice(1).trim());
          i++;
        }
        out.push("<blockquote>" + inline(escHtml(quote.join(" "))) + "</blockquote>");
        continue;
      }

      // 无序列表
      if (/^[-*+]\s+/.test(trimmed)) {
        var items = [];
        while (i < lines.length && /^[-*+]\s+/.test(lines[i].trim())) {
          items.push(lines[i].trim().replace(/^[-*+]\s+/, ""));
          i++;
        }
        out.push("<ul>" + items.map(function (it) { return "<li>" + inline(escHtml(it)) + "</li>"; }).join("") + "</ul>");
        continue;
      }

      // 有序列表
      if (/^\d+\.\s+/.test(trimmed)) {
        var oItems = [];
        while (i < lines.length && /^\d+\.\s+/.test(lines[i].trim())) {
          oItems.push(lines[i].trim().replace(/^\d+\.\s+/, ""));
          i++;
        }
        out.push("<ol>" + oItems.map(function (it) { return "<li>" + inline(escHtml(it)) + "</li>"; }).join("") + "</ol>");
        continue;
      }

      // 普通段落（吞掉连续非空行）
      var para = [];
      while (i < lines.length && lines[i].trim()) {
        // 碰到特殊块则中断
        var t = lines[i].trim();
        if (/^(---|___|\*\*\*)$/.test(t) || t.startsWith("```") || t.startsWith(">") || /^#{1,6}\s+/.test(t) || /^[-*+]\s+/.test(t) || /^\d+\.\s+/.test(t)) break;
        para.push(lines[i]);
        i++;
      }
      flushPara(para);
    }

    return out.join("\n");
  }

  return { toHtml: toHtml };
});