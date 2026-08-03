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
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace LunchHelper
{
    /// <summary>
    /// 程序入口：解析启动参数、按需自提权、单实例互斥、分发运行模式、拉起守护进程。
    ///   无参数        -> 配置界面
    ///   -lock         -> 控制（锁屏）模式
    ///   -debug        -> 调试模式（配置界面 + 置顶控制台 + 详细日志且不自动删除）
    ///   -guardian pid -> 守护进程（由锁屏自动拉起，普通用户不应手动使用）
    ///   -elevated     -> 内部标记：表示已尝试过提权，避免自提权时无限重拉
    ///
    /// 按需自提权（on-demand self-elevation）：
    ///   uiAccess 置顶需“可信签名 +（受保护目录 OR 提权）”。
    ///   锁屏/守护模式若发现当前既未提权、也不在受保护目录（如从 Downloads 双击），
    ///   会自动以管理员重启用自身（弹一次 UAC）以获取 uiAccess。
    ///   受保护目录用“当前用户能否写入 exe 所在目录”自适应判定（不硬编码路径，
    ///   因此 D:\Program Files\ 或任何管理员写保护目录都能正确识别）。
    ///   配置/调试模式不需要 uiAccess，不触发。
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

            // restartApp 拉起的新进程会先等待旧进程退出，避免单实例互斥冲突
            int waitPid = ExtractRestartWaitPid(ref args);
            if (waitPid > 0)
            {
                try
                {
                    var oldProc = Process.GetProcessById(waitPid);
                    oldProc.WaitForExit(8000);
                }
                catch { }
            }

            bool debug = Contains(args, "-debug");
            bool lockMode = Contains(args, "-lock");
            bool guardian = Contains(args, "-guardian");

            // 日志尽早初始化：提权降级、插件清理/安装等启动早期步骤都可能产生需要排查的日志，
            // 必须在这些逻辑之前就绪，错误才能落到 release/logs。
            Logger.Init(ConfigManager.Load().LogRetentionDays, debug);

            // 按需自提权：仅当配置了「启用 UI Access」时才尝试。
            // uiAccess 生效 = 可信签名 +（受保护目录 OR 提权）。
            // 若当前既未提权、也不在受保护目录（如从 Downloads 双击），则自动以管理员
            // 重启用自身（仅弹一次 UAC），从而拿到 uiAccess；已尝试过提权（-elevated）
            // 则不再重复拉起，避免死循环。配置/调试模式不需要 uiAccess，不触发。
            // 用户取消 UAC：静默降级为普通锁屏（不弹窗、不退出）。
            bool enableUiAccess = ConfigManager.Load().EnableUiAccess;
            if (enableUiAccess && NeedsUiAccess(lockMode, guardian) && !Contains(args, "-elevated") && !HasUiAccessPrivilege())
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = Application.ExecutablePath,
                        Arguments = string.Join(" ", args) + " -elevated",
                        Verb = "runas",
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    return; // 当前非提权实例退出，由提权副本继续
                }
                catch (Win32Exception)
                {
                    // 用户取消 UAC：静默降级为普通锁屏（不弹窗、不退出）。
                    // 不 return，让控制流自然落到下方的普通锁屏流程。
                    Logger.Warn("用户取消提权，降级为普通锁屏（不盖任务管理器）");
                }
            }

            if (guardian)
            {
                Guardian.Run(args);
                return;
            }

            // 插件延迟生效：先清理待卸载（删除 .uninstall 目录），再应用待安装（解压 .pending 包）。
            // 二者都在重启时执行，使“卸载/安装”在用户点击后于下次启动时真正生效（可撤销窗口期内只是标记）。
            try { PluginHost.CleanupUninstall(); }
            catch (Exception ex) { Logger.Error("清理待卸载插件失败: " + ex.Message); }
            try { PluginHost.ApplyPendingInstalls(); }
            catch (Exception ex) { Logger.Error("应用待安装插件失败: " + ex.Message); }

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
                        Logger.Info("启动配置界面（调试模式，控制台已置顶）");
                    }
                    else
                    {
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

        /// <summary>解析并移除 `-restartwait <pid>` 参数（由 restartApp 用于避免单实例冲突）。</summary>
        private static int ExtractRestartWaitPid(ref string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-restartwait", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(args[i + 1], out int pid))
                {
                    var list = new System.Collections.Generic.List<string>(args);
                    list.RemoveAt(i + 1);
                    list.RemoveAt(i);
                    args = list.ToArray();
                    return pid;
                }
            }
            return -1;
        }

        /// <summary>
        /// 锁屏 / 守护模式需要 uiAccess 置顶；配置 / 调试模式不需要。
        /// </summary>
        private static bool NeedsUiAccess(bool lockMode, bool guardian) => lockMode || guardian;

        /// <summary>
        /// 当前进程是否已具备 uiAccess 特权：已提权，或位于受保护目录。
        /// </summary>
        private static bool HasUiAccessPrivilege() => IsElevated() || IsInSecureLocation();

        /// <summary>
        /// 是否以管理员身份运行（提权）。提权本身即可满足 uiAccess 的位置条件。
        /// </summary>
        private static bool IsElevated()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        /// <summary>
        /// 自适应判定 exe 是否位于“受保护目录”：尝试在当前 exe 目录写入一个临时文件。
        /// 若当前（标准）用户无权写入，说明该目录仅管理员可写 —— 即 Windows 认可的
        /// 受保护目录（如 C:\Program Files、D:\Program Files 或任何管理员写保护目录）。
        /// 不硬编码路径，因此在所有电脑、任意盘符下都能正确识别。
        /// </summary>
        private static bool IsInSecureLocation()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
                if (string.IsNullOrEmpty(exeDir)) return false;
                string probe = Path.Combine(exeDir, ".lh_writetest_" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.WriteByte(0);
                }
                // 能写入 -> 标准用户可写 -> 不是受保护目录
                try { File.Delete(probe); }
                catch { /* 删除失败也无妨，仅判定用 */ }
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // 无权写入 -> 管理员写保护 -> 视为受保护目录
                return true;
            }
            catch
            {
                // 其它异常（目录不存在等）-> 保守判定为非受保护
                return false;
            }
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
