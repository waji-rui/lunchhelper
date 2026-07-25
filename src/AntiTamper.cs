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
using System.Windows.Forms;

namespace LunchHelper
{
    /// <summary>
    /// 防撬锁：低级键盘钩子屏蔽退出/切换类快捷键（含 Ctrl+Shift+Esc 任务管理器快捷键）。
    /// 真正的 uiAccess 置顶窗口覆盖所有普通窗口（含任务管理器），故不再通过注册表禁用任务管理器，
    /// 以免触发第三方安全软件的 HIPS 拦截弹窗。
    /// 注意：Ctrl+Alt+Del（SAS 安全序列）属系统级，普通程序无法拦截，这是 Windows 的设计限制。
    /// </summary>
    internal static class AntiTamper
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_TAB = 0x09;
        private const int VK_MENU = 0x12;   // Alt
        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_F4 = 0x73;

        private static IntPtr _hook = IntPtr.Zero;
        private static LowLevelKeyboardProc _proc;
        private static readonly object _lock = new object();

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, Delegate lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        public static void Enable()
        {
            lock (_lock)
            {
                if (_hook != IntPtr.Zero) return;
                _proc = HookCallback;
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            }
        }

        public static void Cleanup()
        {
            lock (_lock)
            {
                if (_hook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hook);
                    _hook = IntPtr.Zero;
                }
                _proc = null;
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                int vk = Marshal.ReadInt32(lParam);
                bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                bool win = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

                // 直接拦截 Win 键，连带阻止所有 Win+* 组合
                if (vk == VK_LWIN || vk == VK_RWIN) return (IntPtr)1;
                // Alt 系列：Alt+Tab / Alt+Esc / Alt+F4
                if (alt && (vk == VK_TAB || vk == VK_ESCAPE || vk == VK_F4)) return (IntPtr)1;
                // Ctrl+Esc（开始菜单）
                if (ctrl && vk == VK_ESCAPE) return (IntPtr)1;
                // Ctrl+Shift+Esc（任务管理器）
                if (ctrl && shift && vk == VK_ESCAPE) return (IntPtr)1;
                // 其余 Win 组合键
                if (win && vk != VK_LWIN && vk != VK_RWIN) return (IntPtr)1;
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }
    }
}
