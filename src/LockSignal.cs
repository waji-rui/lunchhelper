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
    /// 锁屏状态信号：用于锁屏进程与守护进程之间传递“是否已正常解锁”。
    /// 正常解锁时写入标记文件；守护进程检测到锁屏退出后，若该标记存在则一同退出，
    /// 否则视为被异常结束，自动重启锁屏。
    /// </summary>
    internal static class LockSignal
    {
        private static string FlagPath
        {
            get
            {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return Path.Combine(dir, "LunchHelper.unlocked");
            }
        }

        public static void Clear()
        {
            try { if (File.Exists(FlagPath)) File.Delete(FlagPath); }
            catch { }
        }

        public static void SetUnlocked()
        {
            try { File.WriteAllText(FlagPath, "unlocked"); }
            catch { }
        }

        public static bool IsUnlocked()
        {
            try { return File.Exists(FlagPath); }
            catch { return false; }
        }
    }
}
