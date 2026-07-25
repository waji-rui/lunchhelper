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
using System.Runtime.InteropServices;

namespace LunchHelper
{
    /// <summary>
    /// 调试模式下分配并将控制台窗口置顶。
    /// </summary>
    internal static class ConsoleHelper
    {
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        public static void ShowConsole()
        {
            try
            {
                AllocConsole();
                NativeMethods.BringConsoleToTop();
            }
            catch (Exception ex)
            {
                Logger.Error("分配控制台失败: " + ex.Message);
            }
        }
    }
}
