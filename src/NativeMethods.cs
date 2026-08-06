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
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace LunchHelper
{
    /// <summary>
    /// 集中存放与窗口/进程相关的 P/Invoke 声明。
    /// </summary>
    internal static class NativeMethods
    {
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

        // ---- 进程提权检测（用于 WebView2 在提权进程下 GPU 沙箱易失败导致空白的规避） ----
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenElevation = 20;

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_ELEVATION
        {
            public int TokenIsElevated;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, ref TOKEN_ELEVATION tokenInformation, int tokenInformationLength, out int returnLength);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// 判断当前进程是否以管理员/提权方式运行（高完整性级别）。
        /// </summary>
        public static bool IsProcessElevated()
        {
            try
            {
                using (var proc = Process.GetCurrentProcess())
                {
                    if (!OpenProcessToken(proc.Handle, TOKEN_QUERY, out IntPtr token))
                        return false;
                    try
                    {
                        var elev = new TOKEN_ELEVATION();
                        int retLen;
                        if (GetTokenInformation(token, TokenElevation, ref elev, Marshal.SizeOf(elev), out retLen))
                            return elev.TokenIsElevated != 0;
                        return false;
                    }
                    finally { CloseHandle(token); }
                }
            }
            catch { return false; }
        }

        // ---- 交互式桌面检测（WebView2 在非交互式窗口站/桌面下内容无法经 DWM 合成，导致页面空白） ----
        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDesktop(int dwThreadId);

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern IntPtr GetProcessWindowStation();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

        [DllImport("dwmapi.dll")]
        private static extern int DwmIsCompositionEnabled(out bool pfEnabled);

        private const int UOI_NAME = 2;

        /// <summary>当前线程所在桌面名称（交互式为 "Default"）。</summary>
        public static string GetCurrentDesktopName()
        {
            try
            {
                IntPtr hDesk = GetThreadDesktop(GetCurrentThreadId());
                if (hDesk == IntPtr.Zero) return "(未知)";
                var sb = new StringBuilder(256);
                if (GetUserObjectInformation(hDesk, UOI_NAME, sb, sb.Capacity, out _))
                    return sb.ToString();
                return "(读取失败)";
            }
            catch (Exception ex) { return "(异常:" + ex.Message + ")"; }
        }

        /// <summary>当前进程所在窗口站名称（交互式为 "WinSta0"）。</summary>
        public static string GetCurrentWindowStationName()
        {
            try
            {
                IntPtr hWinsta = GetProcessWindowStation();
                if (hWinsta == IntPtr.Zero) return "(未知)";
                var sb = new StringBuilder(256);
                if (GetUserObjectInformation(hWinsta, UOI_NAME, sb, sb.Capacity, out _))
                    return sb.ToString();
                return "(读取失败)";
            }
            catch (Exception ex) { return "(异常:" + ex.Message + ")"; }
        }

        /// <summary>
        /// 是否运行在用户可见的交互式桌面（WinSta0\Default）。
        /// 仅凭桌面名 "Default" 不够：提权/自动化程序可能把进程放进自有窗口站（其桌面也叫 "Default"），
        /// 此时 WebView2 内容与调试控制台都落在不可见窗口站。必须以窗口站名 "WinSta0" 判定交互式。
        /// </summary>
        public static bool IsOnInteractiveDesktop()
        {
            try
            {
                return string.Equals(GetCurrentWindowStationName(), "WinSta0", StringComparison.OrdinalIgnoreCase)
                    && GetCurrentDesktopName() == "Default";
            }
            catch { return false; }
        }

        /// <summary>DWM 桌面合成是否启用（WebView2 内容须经 DWM 呈现）。注意：非交互窗口站下该 API 仍可能返回 true。</summary>
        public static bool IsDwmCompositionEnabled()
        {
            try { return DwmIsCompositionEnabled(out bool ok) == 0 && ok; }
            catch { return false; }
        }
    }
}
