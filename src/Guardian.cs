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
using System.Threading;

namespace LunchHelper
{
    /// <summary>
    /// 守护进程：监视锁屏进程，若被异常结束则自动重启；若锁屏已正常解锁则一同退出。
    /// 由锁屏进程以 `-guardian &lt;pid&gt;` 拉起，单实例。普通用户无需手动启动。
    /// </summary>
    internal static class Guardian
    {
        private const string MutexName = "Global\\LunchHelper_Guardian_Instance";
        private static Mutex _mutex;

        public static void Run(string[] args)
        {
            bool created = false;
            try
            {
                _mutex = new Mutex(true, MutexName, out created);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("创建守护互斥体失败: " + ex.Message);
                return;
            }
            if (!created)
            {
                // 已存在守护进程，直接退出，避免重复
                try { _mutex?.Dispose(); } catch { }
                return;
            }

            int pid = -1;
            bool found = false;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-guardian", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(args[i + 1], out int p))
                {
                    pid = p;
                    found = true;
                    break;
                }
            }
            if (!found || pid <= 0)
            {
                // 缺少合法 pid 时不应无限拉起锁屏，直接退出
                try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); } catch { }
                return;
            }

            Logger.Init(ConfigManager.Load().LogRetentionDays, false);
            Logger.Info("守护进程启动，监视锁屏进程 pid=" + pid);

            while (true)
            {
                Thread.Sleep(1000);

                if (!ProcessExists(pid))
                {
                    if (LockSignal.IsUnlocked())
                    {
                        Logger.Info("检测到锁屏已正常解锁，守护进程退出");
                        break;
                    }

                    try
                    {
                        var self = Process.GetCurrentProcess().MainModule.FileName;
                        var psi = new ProcessStartInfo
                        {
                            FileName = self,
                            Arguments = "-lock",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        var p = Process.Start(psi);
                        if (p != null)
                        {
                            pid = p.Id;
                            Logger.Warn("锁屏进程被结束，已重新启动 (pid=" + pid + ")");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("重启锁屏失败: " + ex.Message);
                    }
                }
            }

            Logger.Info("守护进程结束");
            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Dispose(); } catch { }
        }

        private static bool ProcessExists(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return true; }
            catch { return false; }
        }
    }
}
