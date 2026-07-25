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
using System.Windows.Forms;

namespace LunchHelper
{
    /// <summary>
    /// 程序入口：解析启动参数、单实例互斥、分发运行模式、拉起守护进程。
    ///   无参数        -> 配置界面
    ///   -lock         -> 控制（锁屏）模式
    ///   -debug        -> 调试模式（配置界面 + 置顶控制台 + 详细日志且不自动删除）
    ///   -guardian pid -> 守护进程（由锁屏自动拉起，普通用户不应手动使用）
    /// </summary>
    static class Program
    {
        private const string AppMutexName = "Global\\LunchHelper_App_Instance";
        private const string GuardianMutexName = "Global\\LunchHelper_Guardian_Instance";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool debug = Contains(args, "-debug");
            bool lockMode = Contains(args, "-lock");
            bool guardian = Contains(args, "-guardian");

            if (guardian)
            {
                Guardian.Run(args);
                return;
            }

            // 单实例：仅对用户启动的模式（配置 / 锁屏）做互斥约束
            using (var mutex = new Mutex(true, AppMutexName, out bool created))
            {
                if (!created)
                {
                    // 互斥体已存在：可能是另一存活实例，也可能是被异常结束（防撬锁重启）遗留的“废弃”互斥体。
                    if (AnotherInstanceAlive())
                    {
                        FocusExisting();
                        return;
                    }
                    // 无其它存活实例 -> 视为废弃互斥体，尝试接管其所有权后继续运行。
                    try { mutex.WaitOne(); }
                    catch (AbandonedMutexException) { /* 上一个持有者已被终止，正常接管 */ }
                }

                if (lockMode)
                {
                    int retention = ConfigManager.Load().LogRetentionDays;
                    Logger.Init(retention, debug);
                    Logger.Info("启动锁屏模式" + (debug ? "（调试）" : ""));

                    int guardianPid = LaunchGuardian();
                    try
                    {
                        Application.Run(new LockForm(debug, guardianPid));
                    }
                    finally
                    {
                        AntiTamper.Cleanup();
                        Logger.Info("锁屏模式退出");
                    }
                }
                else
                {
                    if (debug)
                    {
                        ConsoleHelper.ShowConsole();
                        Logger.Init(ConfigManager.Load().LogRetentionDays, true);
                        Logger.Info("启动配置界面（调试模式，控制台已置顶）");
                    }
                    else
                    {
                        Logger.Init(ConfigManager.Load().LogRetentionDays, false);
                        Logger.Info("启动配置界面");
                    }
                    try
                    {
                        Application.Run(new ConfigForm(debug));
                    }
                    finally
                    {
                        Logger.Info("配置界面退出");
                    }
                }
            }
        }

        private static bool Contains(string[] args, string target)
        {
            foreach (var a in args)
                if (string.Equals(a, target, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// 拉起守护进程（仅当不存在时）。返回守护进程 pid；失败返回 -1。
        /// </summary>
        internal static int LaunchGuardian()
        {
            try
            {
                var self = Process.GetCurrentProcess().MainModule.FileName;
                var psi = new ProcessStartInfo
                {
                    FileName = self,
                    Arguments = "-guardian " + Process.GetCurrentProcess().Id,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var p = Process.Start(psi);
                if (p != null)
                {
                    Logger.Debug("守护进程已拉起 pid=" + p.Id);
                    return p.Id;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("启动守护进程失败: " + ex.Message);
            }
            return -1;
        }

        /// <summary>
        /// 尝试将已运行的实例主窗口置于前台。
        /// </summary>
        private static void FocusExisting()
        {
            try
            {
                var current = Process.GetCurrentProcess();
                foreach (var p in Process.GetProcessesByName(current.ProcessName))
                {
                    if (p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero)
                    {
                        NativeMethods.SetForegroundWindow(p.MainWindowHandle);
                        break;
                    }
                }
            }
            catch { }
        }

        private static bool AnotherInstanceAlive()
        {
            try
            {
                var self = Process.GetCurrentProcess();
                foreach (var p in Process.GetProcessesByName(self.ProcessName))
                {
                    if (p.Id != self.Id && p.MainWindowHandle != IntPtr.Zero)
                        return true;
                }
            }
            catch { }
            return false;
        }
    }
}
