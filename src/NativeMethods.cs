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
using System.Drawing;
using System.Runtime.InteropServices;

namespace LunchHelper
{
    /// <summary>
    /// 集中存放与窗口/进程相关的 P/Invoke 声明。
    /// </summary>
    internal static class NativeMethods
    {
        public const int SW_RESTORE = 9;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x0001;
        private const uint SWP_NOSIZE = 0x0002;

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll")]
        public static extern bool AllocConsole();

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        /// <summary>
        /// 将控制台窗口置顶（调试模式使用）。
        /// </summary>
        public static void BringConsoleToTop()
        {
            IntPtr hwnd = GetConsoleWindow();
            if (hwnd != IntPtr.Zero)
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        }

        // ---- DWM 现代标题栏/圆角 ----
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        /// <summary>
        /// 将窗口标题栏设为深色模式（Win10 1809+ / Win11）。失败则静默忽略。
        /// </summary>
        public static void EnableDarkTitleBar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            int value = 1;
            // 20 = Win10 1809+；19 = 更早的 Win10
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
        }

        /// <summary>
        /// 将窗口设为圆角（Win11 有效，Win10 静默忽略）。
        /// </summary>
        public static void EnableRoundedCorners(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            int value = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
        }

        // ---- 读取 Windows 强调色（设置→个性化→颜色） ----
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetColorizationColor(out uint pcrColorization, out bool pfOpaqueBlend);

        /// <summary>
        /// 读取系统当前强调色（用户在「设置→个性化→颜色」所选，含“从背景自动选取”）。
        /// 失败则回退系统高亮色。返回 ARGB 强制不透明，供 MD3 深色主题作为强调色（仅调用系统 API，无第三方库）。
        /// </summary>
        public static Color GetAccentColor()
        {
            try
            {
                if (DwmGetColorizationColor(out uint color, out _) == 0)
                {
                    byte r = (byte)((color >> 16) & 0xFF);
                    byte g = (byte)((color >> 8) & 0xFF);
                    byte b = (byte)(color & 0xFF);
                    return Color.FromArgb(255, r, g, b);
                }
            }
            catch { }
            return SystemColors.Highlight;
        }

        // ---- 读取 Windows“显示动画”系统设置 ----
        private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out bool pvParam, uint fWinIni);

        /// <summary>
        /// 读取 Windows“显示动画”总开关（对应「设置→辅助功能→视觉效果→动画效果」）。
        /// 返回 false 时，应用内所有过渡/动画都应禁用以跟随系统偏好；读取失败保守返回 true（展示动画）。
        /// </summary>
        public static bool AreAnimationsEnabled()
        {
            try
            {
                return SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out bool enabled, 0) && enabled;
            }
            catch { return true; }
        }
    }
}
