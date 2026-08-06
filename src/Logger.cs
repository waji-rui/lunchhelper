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
using System.IO;
using System.Reflection;

namespace LunchHelper
{
    /// <summary>
    /// 日志：写入程序目录下的 logs\，按天滚动。
    ///   - 保留天数 &gt; 0：启动时清理过期日志
    ///   - 保留天数 = 0：不写入任何日志
    ///   - 调试模式：忽略保留天数、不自动删除，并输出到控制台
    /// </summary>
    internal static class Logger
    {
        private static readonly object _lock = new object();
        private static string _logDir;
        private static bool _enabled;
        private static bool _debug;
        private static int _retentionDays;

        public static string LogDirectory
        {
            get
            {
                if (_logDir != null) return _logDir;
                var baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return Path.Combine(baseDir, "logs");
            }
        }

        public static void Init(int retentionDays, bool debug)
        {
            _debug = debug;
            _retentionDays = retentionDays;
            var baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _logDir = Path.Combine(baseDir, "logs");
            try { Directory.CreateDirectory(_logDir); } catch { }
            _enabled = debug || retentionDays > 0;
            if (_enabled && !debug) Rotate();
            Info("日志初始化完成 (retentionDays=" + retentionDays + ", debug=" + debug + ")");
        }

        private static void Rotate()
        {
            try
            {
                if (_retentionDays <= 0) return;
                var cutoff = DateTime.Now.Date.AddDays(-_retentionDays);
                foreach (var f in Directory.GetFiles(_logDir, "LunchHelper_*.log"))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    var datePart = name.Length > "LunchHelper_".Length ? name.Substring("LunchHelper_".Length) : "";
                    if (DateTime.TryParse(datePart, out var d) && d < cutoff)
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }

        private static string TodayFile()
        {
            return Path.Combine(_logDir, "LunchHelper_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
        }

        private static void Write(string level, string msg)
        {
            if (!_enabled) return;
            var line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [" + level + "] " + msg;
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(TodayFile(), line + Environment.NewLine);
                }
            }
            catch { }
            if (_debug)
            {
                try { Console.WriteLine(line); } catch { }
            }
        }

        public static void Info(string m) { Write("INFO", m); }
        public static void Warn(string m) { Write("WARN", m); }
        public static void Error(string m) { Write("ERROR", m); }
        public static void Debug(string m) { if (_debug) Write("DEBUG", m); }
    }
}
