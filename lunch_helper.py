#!/usr/bin/env python3
"""
LunchHelper - 午休锁屏助手
=======================

启动方式:
  python lunch_helper.py              → 配置界面
  python lunch_helper.py -lock        → 控制模式（锁屏）
  python lunch_helper.py -debug       → 调试模式

依赖:
  - Python 3.13+
  - tkinter (Python 自带)
  - uiaccess_helper.dll (编译后的 C 辅助 DLL，本项目提供源码)

参考项目:
  - killtimer0/uiaccess  (https://github.com/killtimer0/uiaccess)
  - ADeltaX Blog         (https://blog.adeltax.com/window-z-order-in-windows-10/)
"""

import sys
import os
import ctypes
import ctypes.wintypes
import logging
import configparser
from pathlib import Path
from datetime import datetime, timedelta
from logging.handlers import TimedRotatingFileHandler
import tkinter as tk
from tkinter import messagebox

# ========================================================================
# 常量定义
# ========================================================================

APP_NAME = "LunchHelper"
APP_VERSION = "1.0.0"
APP_AUTHOR = "LunchHelper Team"
APP_DESCRIPTION = "午休锁屏助手 - 定时锁定屏幕，休息一下"

# 单实例互斥体名称
MUTEX_NAME = "Global\\LunchHelper_SingleInstance_Mutex"

# DLL 文件名
DLL_NAME = "uiaccess_helper.dll"

# 配置文件
CONFIG_FILE = "lunch_helper.ini"

# 日志目录
LOG_DIR = "logs"

# 默认配置
DEFAULT_CONFIG = {
    "lock_duration": "10",           # 锁定时长（秒）
    "emergency_password": "000000",  # 应急解锁密码（6位数字）
    "log_retention_days": "14",      # 日志保存天数
    "lock_slogan": "",               # 锁屏标语
}

# 应急解锁尝试冷却时间（秒）
UNLOCK_COOLDOWN = 5

# ========================================================================
# 路径工具
# ========================================================================

def get_app_dir():
    """获取应用程序所在目录（兼容打包后的 exe）"""
    if getattr(sys, 'frozen', False):
        return Path(sys.executable).parent
    return Path(__file__).resolve().parent

def get_config_path():
    return get_app_dir() / CONFIG_FILE

def get_log_dir():
    return get_app_dir() / LOG_DIR

def get_dll_path():
    return get_app_dir() / DLL_NAME

# ========================================================================
# Windows API (ctypes 定义)
# ========================================================================

# 互斥体相关
kernel32 = ctypes.windll.kernel32
CreateMutexW = kernel32.CreateMutexW
CreateMutexW.argtypes = [ctypes.c_void_p, ctypes.wintypes.BOOL, ctypes.wintypes.LPCWSTR]
CreateMutexW.restype = ctypes.wintypes.HANDLE

CloseHandle = kernel32.CloseHandle
CloseHandle.argtypes = [ctypes.wintypes.HANDLE]
CloseHandle.restype = ctypes.wintypes.BOOL

GetLastError = kernel32.GetLastError
GetLastError.restype = ctypes.wintypes.DWORD

ERROR_ALREADY_EXISTS = 183

# Shell 相关（提权）
shell32 = ctypes.windll.shell32
ShellExecuteW = shell32.ShellExecuteW
ShellExecuteW.argtypes = [
    ctypes.wintypes.HWND, ctypes.wintypes.LPCWSTR, ctypes.wintypes.LPCWSTR,
    ctypes.wintypes.LPCWSTR, ctypes.wintypes.LPCWSTR, ctypes.wintypes.INT
]
ShellExecuteW.restype = ctypes.wintypes.HINSTANCE

SW_SHOW = 5

# 用户相关
IsUserAnAdmin = shell32.IsUserAnAdmin
IsUserAnAdmin.restype = ctypes.wintypes.BOOL

# 控制台相关
AllocConsole = kernel32.AllocConsole
AllocConsole.restype = ctypes.wintypes.BOOL

FreeConsole = kernel32.FreeConsole
FreeConsole.restype = ctypes.wintypes.BOOL

AttachConsole = kernel32.AttachConsole
AttachConsole.argtypes = [ctypes.wintypes.DWORD]
AttachConsole.restype = ctypes.wintypes.BOOL

ATTACH_PARENT_PROCESS = 0xFFFFFFFF

# 虚拟桌面尺寸
user32 = ctypes.windll.user32
GetSystemMetrics = user32.GetSystemMetrics
GetSystemMetrics.argtypes = [ctypes.c_int]
GetSystemMetrics.restype = ctypes.c_int

SM_XVIRTUALSCREEN = 76
SM_YVIRTUALSCREEN = 77
SM_CXVIRTUALSCREEN = 78
SM_CYVIRTUALSCREEN = 79

# 显示器枚举
EnumDisplayMonitors = user32.EnumDisplayMonitors
MONITORENUMPROC = ctypes.WINFUNCTYPE(ctypes.wintypes.BOOL, ctypes.wintypes.HMONITOR,
                                      ctypes.wintypes.HDC, ctypes.POINTER(ctypes.wintypes.RECT),
                                      ctypes.wintypes.LPARAM)

# ========================================================================
# 单实例检查
# ========================================================================

def check_single_instance():
    """检查是否为唯一实例，返回 (是否唯一, 互斥体句柄)"""
    handle = CreateMutexW(None, False, MUTEX_NAME)
    if not handle:  # ctypes converts NULL to None
        return False, None
    if GetLastError() == ERROR_ALREADY_EXISTS:
        CloseHandle(handle)
        return False, None
    return True, handle

# ========================================================================
# 管理员提权
# ========================================================================

def run_as_admin(extra_args=None):
    """以管理员权限重新启动当前程序"""
    if getattr(sys, 'frozen', False):
        exe_path = sys.executable
        args = list(sys.argv[1:])
    else:
        exe_path = sys.executable
        script = os.path.abspath(sys.argv[0])
        args = [script] + sys.argv[1:]

    if extra_args:
        args.extend(extra_args)

    params = ' '.join(f'"{a}"' for a in args)
    
    ret = ShellExecuteW(None, "runas", exe_path, params, None, SW_SHOW)
    if not ret or ret <= 32:
        return False
    return True

def is_admin():
    return IsUserAnAdmin()

# ========================================================================
# 配置管理
# ========================================================================

class ConfigManager:
    """配置文件读写管理"""

    def __init__(self, config_path):
        self.config_path = Path(config_path)
        self.config = configparser.ConfigParser()
        self._load()

    def _load(self):
        """加载配置文件，不存在则创建默认配置"""
        if self.config_path.exists():
            self.config.read(self.config_path, encoding='utf-8')
        
        if not self.config.has_section('Settings'):
            self.config.add_section('Settings')
        
        # 确保所有键都存在
        for key, default in DEFAULT_CONFIG.items():
            if not self.config.has_option('Settings', key):
                self.config.set('Settings', key, default)

    def save(self):
        """保存配置到文件"""
        self.config_path.parent.mkdir(parents=True, exist_ok=True)
        with open(self.config_path, 'w', encoding='utf-8') as f:
            self.config.write(f)

    def get(self, key, default=None):
        return self.config.get('Settings', key, fallback=default)

    def set(self, key, value):
        self.config.set('Settings', key, str(value))

    def get_int(self, key, default=0):
        return self.config.getint('Settings', key, fallback=default)

    def reset_to_default(self):
        """恢复默认配置"""
        for key, default in DEFAULT_CONFIG.items():
            self.config.set('Settings', key, default)

    def validate(self):
        """Validate config, returns (is_valid, error_list)"""
        errors = []
        
        # Lock duration
        duration = self.get_int('lock_duration', 0)
        if duration < 1:
            errors.append("锁定时长必须大于 0 秒")
        elif duration > 86400:
            errors.append("锁定时长不能超过 86400 秒（24小时）")

        # Emergency password
        password = self.get('emergency_password', '')
        if not password.isdigit() or len(password) != 6:
            errors.append("应急密码必须为 6 位纯数字")

        # Log retention days
        days = self.get_int('log_retention_days', -1)
        if days < 0:
            errors.append("日志保存天数必须大于等于 0")

        return len(errors) == 0, errors

# ========================================================================
# 日志管理
# ========================================================================

class LogManager:
    """日志管理器"""

    def __init__(self, log_dir, retention_days=14, debug=False):
        self.log_dir = Path(log_dir)
        self.retention_days = retention_days
        self.debug = debug
        self.logger = None
        self._setup()

    def _setup(self):
        """设置日志系统"""
        self.log_dir.mkdir(parents=True, exist_ok=True)

        self.logger = logging.getLogger(APP_NAME)
        self.logger.setLevel(logging.DEBUG if self.debug else logging.INFO)

        # 清除已有处理器
        self.logger.handlers.clear()

        # 日志格式
        formatter = logging.Formatter(
            '[%(asctime)s] [%(levelname)s] %(message)s',
            datefmt='%Y-%m-%d %H:%M:%S'
        )

        # 文件处理器（按天轮转），retention_days=0 时不写文件
        if self.retention_days != 0:
            log_file = self.log_dir / 'lunch_helper.log'
            file_handler = TimedRotatingFileHandler(
                log_file,
                when='midnight',
                interval=1,
                backupCount=999,
                encoding='utf-8'
            )
            file_handler.suffix = '%Y%m%d'
            file_handler.setFormatter(formatter)
            file_handler.setLevel(logging.DEBUG if self.debug else logging.INFO)
            self.logger.addHandler(file_handler)

        # 控制台处理器（仅调试模式）
        if self.debug:
            console_handler = logging.StreamHandler(sys.stdout)
            console_handler.setFormatter(formatter)
            console_handler.setLevel(logging.DEBUG)
            self.logger.addHandler(console_handler)

        self.logger.info("=" * 50)
        self.logger.info(f"{APP_NAME} v{APP_VERSION} started")
        self.logger.info(f"Log directory: {self.log_dir}")
        self.logger.info(f"Debug mode: {'ON' if self.debug else 'OFF'}")
        self.logger.info(f"Log retention days: {self.retention_days}")

    def clean_old_logs(self):
        """Clean up expired logs"""
        if self.retention_days <= 0:
            self.logger.info("Log retention is 0, cleaning all log files")
            for f in self.log_dir.glob('lunch_helper.log*'):
                try:
                    f.unlink()
                    self.logger.debug(f"Deleted: {f}")
                except Exception as e:
                    self.logger.error(f"Failed to delete log file {f}: {e}")
            return

        cutoff = datetime.now() - timedelta(days=self.retention_days)
        self.logger.info(f"Cleaning logs older than {cutoff.strftime('%Y-%m-%d')}")

        for f in self.log_dir.glob('lunch_helper.log.*'):
            # TimedRotatingFileHandler produces files like: lunch_helper.log.20260101
            # Path.suffix returns only the last extension, so we work with f.name
            try:
                date_str = f.name.split('.')[-1]  # last part after all dots
                if len(date_str) == 8 and date_str.isdigit():
                    file_date = datetime.strptime(date_str, '%Y%m%d')
                    if file_date < cutoff:
                        f.unlink()
                        self.logger.debug(f"Deleted expired log: {f}")
            except (ValueError, Exception) as e:
                self.logger.error(f"Failed to process log file {f}: {e}")

    def get_logger(self):
        return self.logger

# ========================================================================
# UIAccess DLL 封装
# ========================================================================

class UIAccessHelper:
    """UIAccess 辅助 DLL 的 Python 封装"""

    def __init__(self):
        self.dll = None
        self._loaded = False

    def load(self):
        """Load DLL"""
        dll_path = str(get_dll_path())
        try:
            self.dll = ctypes.CDLL(dll_path)
            self._loaded = True
            return True
        except OSError as e:
            print(f"Warning: Unable to load UIAccess helper DLL ({dll_path}): {e}")
            print("Will use normal topmost mode (cannot cover system windows like Task Manager)")
            self._loaded = False
            return False

    def is_loaded(self):
        return self._loaded

    def set_debug_mode(self, debug):
        """设置 DLL 调试模式"""
        if not self._loaded:
            return
        try:
            self.dll.SetDebugMode(ctypes.c_bool(debug))
        except Exception:
            pass

    def prepare_for_uiaccess(self):
        """
        准备 UIAccess 权限。
        注意：如果成功获取 UIAccess，此函数不会返回（进程会重启）。
        """
        if not self._loaded:
            return -1  # DLL not loaded
        try:
            return self.dll.PrepareForUIAccess()
        except Exception as e:
            print(f"PrepareForUIAccess call failed: {e}")
            return -1

    def install_keyboard_hook(self):
        """安装键盘钩子"""
        if not self._loaded:
            return False
        try:
            return self.dll.InstallKeyboardHook()
        except Exception:
            return False

    def remove_keyboard_hook(self):
        """卸载键盘钩子"""
        if not self._loaded:
            return
        try:
            self.dll.RemoveKeyboardHook()
        except Exception:
            pass

    def is_elevated(self):
        """检查是否管理员权限"""
        if not self._loaded:
            return is_admin()
        try:
            return self.dll.IsElevated()
        except Exception:
            return is_admin()

    def get_uiaccess_status(self):
        """获取 UIAccess 状态"""
        if not self._loaded:
            return False
        try:
            return self.dll.GetUIAccessStatus()
        except Exception:
            return False

    def run_lock_screen(self, duration, password, slogan, debug=False):
        """
        调用 DLL 的 RunLockScreen 函数。
        该函数阻塞直到锁屏退出。
        返回值: 0=超时自动解锁, 1=密码正确解锁, -1=错误
        """
        if not self._loaded:
            return -1
        try:
            self.dll.RunLockScreen.argtypes = [
                ctypes.c_int,           # durationSeconds
                ctypes.c_wchar_p,       # password
                ctypes.c_wchar_p,       # slogan
                ctypes.c_bool,          # debug
            ]
            self.dll.RunLockScreen.restype = ctypes.c_ulong
            return self.dll.RunLockScreen(
                ctypes.c_int(duration),
                ctypes.c_wchar_p(password),
                ctypes.c_wchar_p(slogan if slogan else ""),
                ctypes.c_bool(debug)
            )
        except Exception as e:
            print(f"RunLockScreen failed: {e}")
            return -1

# ========================================================================
# 虚拟桌面尺寸获取
# ========================================================================

def get_virtual_screen_rect():
    """获取所有显示器的虚拟桌面矩形"""
    x = GetSystemMetrics(SM_XVIRTUALSCREEN)
    y = GetSystemMetrics(SM_YVIRTUALSCREEN)
    w = GetSystemMetrics(SM_CXVIRTUALSCREEN)
    h = GetSystemMetrics(SM_CYVIRTUALSCREEN)
    return (x, y, w, h)

def get_all_monitor_rects():
    """获取所有显示器的矩形列表"""
    rects = []

    def callback(hMonitor, hdc, pRect, data):
        rect = pRect.contents
        rects.append((rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top))
        return True

    EnumDisplayMonitors(None, None, MONITORENUMPROC(callback), 0)
    return rects

# ========================================================================
# 配置界面
# ========================================================================

class ConfigWindow:
    """配置界面 GUI"""

    def __init__(self, config_manager):
        self.config = config_manager
        self._should_launch_lock = False
        self.root = tk.Tk()
        self.root.title(f"{APP_NAME} v{APP_VERSION} - 配置")
        self.root.resizable(False, False)
        
        # 窗口居中
        self.root.update_idletasks()
        w, h = 520, 460
        sw = self.root.winfo_screenwidth()
        sh = self.root.winfo_screenheight()
        x = (sw - w) // 2
        y = (sh - h) // 2
        self.root.geometry(f"{w}x{h}+{x}+{y}")
        
        self.root.configure(bg='#f0f0f0')
        self._build_ui()

    def _build_ui(self):
        """构建界面"""
        # 标题
        title_frame = tk.Frame(self.root, bg='#2c3e50', height=60)
        title_frame.pack(fill=tk.X)
        title_frame.pack_propagate(False)
        
        title_label = tk.Label(title_frame, text=f"{APP_NAME} - 配置中心",
                               font=('Microsoft YaHei', 16, 'bold'),
                               fg='white', bg='#2c3e50')
        title_label.pack(expand=True)

        # 配置区域
        main_frame = tk.Frame(self.root, bg='#f0f0f0', padx=30, pady=20)
        main_frame.pack(fill=tk.BOTH, expand=True)

        # 锁定时长
        row = 0
        tk.Label(main_frame, text="锁定时长（秒）：", font=('Microsoft YaHei', 11),
                 bg='#f0f0f0', anchor='w').grid(row=row, column=0, sticky='w', pady=8)
        self.duration_var = tk.StringVar(value=self.config.get('lock_duration', '10'))
        self.duration_entry = tk.Entry(main_frame, textvariable=self.duration_var,
                                       font=('Microsoft YaHei', 11), width=15,
                                       justify='center')
        self.duration_entry.grid(row=row, column=1, sticky='w', padx=(10, 0), pady=8)
        tk.Label(main_frame, text="（建议 30-600 秒）", font=('Microsoft YaHei', 9),
                 bg='#f0f0f0', fg='gray').grid(row=row, column=2, sticky='w', padx=(5, 0))

        # 应急密码
        row += 1
        tk.Label(main_frame, text="应急解锁密码：", font=('Microsoft YaHei', 11),
                 bg='#f0f0f0', anchor='w').grid(row=row, column=0, sticky='w', pady=8)
        self.password_var = tk.StringVar(value=self.config.get('emergency_password', '000000'))
        self.password_entry = tk.Entry(main_frame, textvariable=self.password_var,
                                       font=('Microsoft YaHei', 11), width=15,
                                       justify='center', show='*')
        self.password_entry.grid(row=row, column=1, sticky='w', padx=(10, 0), pady=8)
        tk.Label(main_frame, text="（6 位数字）", font=('Microsoft YaHei', 9),
                 bg='#f0f0f0', fg='gray').grid(row=row, column=2, sticky='w', padx=(5, 0))

        # 日志保存天数
        row += 1
        tk.Label(main_frame, text="日志保存天数：", font=('Microsoft YaHei', 11),
                 bg='#f0f0f0', anchor='w').grid(row=row, column=0, sticky='w', pady=8)
        self.log_days_var = tk.StringVar(value=self.config.get('log_retention_days', '14'))
        self.log_days_entry = tk.Entry(main_frame, textvariable=self.log_days_var,
                                       font=('Microsoft YaHei', 11), width=15,
                                       justify='center')
        self.log_days_entry.grid(row=row, column=1, sticky='w', padx=(10, 0), pady=8)
        tk.Label(main_frame, text="（0 = 不保存）", font=('Microsoft YaHei', 9),
                 bg='#f0f0f0', fg='gray').grid(row=row, column=2, sticky='w', padx=(5, 0))

        # 锁屏标语
        row += 1
        tk.Label(main_frame, text="锁屏标语：", font=('Microsoft YaHei', 11),
                 bg='#f0f0f0', anchor='w').grid(row=row, column=0, sticky='w', pady=8)
        self.slogan_var = tk.StringVar(value=self.config.get('lock_slogan', ''))
        self.slogan_entry = tk.Entry(main_frame, textvariable=self.slogan_var,
                                     font=('Microsoft YaHei', 11), width=30)
        self.slogan_entry.grid(row=row, column=1, columnspan=2, sticky='w', padx=(10, 0), pady=8)

        # 按钮区域
        btn_frame = tk.Frame(main_frame, bg='#f0f0f0')
        btn_frame.grid(row=row + 1, column=0, columnspan=3, pady=20)

        # 保存配置
        save_btn = tk.Button(btn_frame, text="保存配置", font=('Microsoft YaHei', 11),
                             bg='#27ae60', fg='white', activebackground='#2ecc71',
                             activeforeground='white', relief='flat',
                             padx=20, pady=6, cursor='hand2',
                             command=self._on_save)
        save_btn.pack(side=tk.LEFT, padx=5)

        # 启动锁屏
        lock_btn = tk.Button(btn_frame, text="启动锁屏", font=('Microsoft YaHei', 11),
                             bg='#2980b9', fg='white', activebackground='#3498db',
                             activeforeground='white', relief='flat',
                             padx=20, pady=6, cursor='hand2',
                             command=self._on_start_lock)
        lock_btn.pack(side=tk.LEFT, padx=5)

        # 恢复默认
        reset_btn = tk.Button(btn_frame, text="恢复默认", font=('Microsoft YaHei', 11),
                              bg='#e67e22', fg='white', activebackground='#f39c12',
                              activeforeground='white', relief='flat',
                              padx=20, pady=6, cursor='hand2',
                              command=self._on_reset)
        reset_btn.pack(side=tk.LEFT, padx=5)

        # 关于
        about_btn = tk.Button(btn_frame, text="关于", font=('Microsoft YaHei', 11),
                              bg='#8e44ad', fg='white', activebackground='#9b59b6',
                              activeforeground='white', relief='flat',
                              padx=20, pady=6, cursor='hand2',
                              command=self._on_about)
        about_btn.pack(side=tk.LEFT, padx=5)

        # 状态栏
        self.status_var = tk.StringVar(value="就绪")
        status_bar = tk.Label(self.root, textvariable=self.status_var,
                              font=('Microsoft YaHei', 9), bg='#ecf0f1',
                              anchor='w', relief=tk.SUNKEN)
        status_bar.pack(side=tk.BOTTOM, fill=tk.X)

    def _on_save(self):
        """保存配置"""
        self.config.set('lock_duration', self.duration_var.get())
        self.config.set('emergency_password', self.password_var.get())
        self.config.set('log_retention_days', self.log_days_var.get())
        self.config.set('lock_slogan', self.slogan_var.get())

        valid, errors = self.config.validate()
        if not valid:
            messagebox.showerror("配置错误", "\n".join(f"  {e}" for e in errors))
            return

        self.config.save()
        self.status_var.set("配置已保存")
        messagebox.showinfo("提示", "配置保存成功！")

    def _on_start_lock(self):
        """启动锁屏（先保存配置）"""
        self.config.set('lock_duration', self.duration_var.get())
        self.config.set('emergency_password', self.password_var.get())
        self.config.set('log_retention_days', self.log_days_var.get())
        self.config.set('lock_slogan', self.slogan_var.get())

        valid, errors = self.config.validate()
        if not valid:
            messagebox.showerror("配置错误", "\n".join(f"  {e}" for e in errors))
            return

        self.config.save()
        self._should_launch_lock = True
        self.root.quit()  # 退出 mainloop，在 run() 中启动锁屏

    def _on_reset(self):
        """恢复默认配置"""
        if messagebox.askyesno("确认", "确定要恢复默认配置吗？"):
            self.config.reset_to_default()
            self.duration_var.set(DEFAULT_CONFIG['lock_duration'])
            self.password_var.set(DEFAULT_CONFIG['emergency_password'])
            self.log_days_var.set(DEFAULT_CONFIG['log_retention_days'])
            self.slogan_var.set(DEFAULT_CONFIG['lock_slogan'])
            self.status_var.set("已恢复默认配置")

    def _on_about(self):
        """显示关于信息"""
        about_text = (
            f"{APP_NAME} v{APP_VERSION}\n\n"
            f"{APP_DESCRIPTION}\n\n"
            f"功能特性:\n"
            f"  - 全屏锁屏，UIAccess 超级置顶\n"
            f"  - 可配置倒计时锁定时长\n"
            f"  - 应急密码解锁（防暴力破解）\n"
            f"  - 多显示器支持\n"
            f"  - 键盘快捷键拦截\n"
            f"  - 完善的日志系统\n\n"
            f"启动方式:\n"
            f"  直接启动 → 配置界面\n"
            f"  -lock     → 控制模式\n"
            f"  -debug    → 调试模式\n\n"
            f"参考项目:\n"
            f"  killtimer0/uiaccess\n"
            f"  ADeltaX Blog\n\n"
            f"技术栈: Python 3.13 + C (DLL) + tkinter\n"
        )
        messagebox.showinfo("关于", about_text)

    def run(self):
        self.root.mainloop()
        # 如果用户点击了「启动锁屏」，则退出 mainloop 后启动锁屏
        if self._should_launch_lock:
            self.root.destroy()
            launch_lock_screen()

# ========================================================================
# 锁屏界面
# ========================================================================

class LockScreen:
    """Full-screen lock screen (fallback, tkinter-based)"""

    def __init__(self, config, log_manager, uiaccess_helper, debug=False):
        self.config = config
        self.logger = log_manager.get_logger()
        self.uiaccess = uiaccess_helper
        self.debug = debug

        self.lock_duration = config.get_int('lock_duration', 10)
        self.emergency_password = config.get('emergency_password', '000000')
        self.slogan = config.get('lock_slogan', '')

        self.remaining = self.lock_duration
        self.windows = []
        self.password_input = ""
        self.unlock_attempts = 0
        self.cooldown_remaining = 0
        self.cooldown_active = False
        self._refocus_blocked = False  # prevent FocusOut infinite loop

        self.logger.info(f"Lock screen started - duration: {self.lock_duration}s, slogan: '{self.slogan}'")

        # Create hidden root window (Toplevel requires Tk root)
        self.root = tk.Tk()
        self.root.withdraw()  # hide root window

        self._create_windows()

    def _create_windows(self):
        """Create full-screen windows for each monitor"""
        monitors = get_all_monitor_rects()
        self.logger.info(f"Detected {len(monitors)} monitor(s)")

        if not monitors:
            monitors = [(0, 0, 1920, 1080)]

        for i, (x, y, w, h) in enumerate(monitors):
            win = self._create_single_window(x, y, w, h, i)
            self.windows.append(win)

    def _create_single_window(self, x, y, w, h, index):
        """Create a single monitor's full-screen window"""
        win = tk.Toplevel(self.root)
        win.title("LunchHelper")
        win.configure(bg='black')

        # Fullscreen positioning
        win.geometry(f"{w}x{h}+{x}+{y}")

        # Window attributes
        win.overrideredirect(True)       # borderless
        win.attributes('-topmost', True)  # always on top
        win.attributes('-fullscreen', False)
        win.attributes('-alpha', 1.0)

        # Prevent window from being closed
        win.protocol('WM_DELETE_WINDOW', lambda: None)

        # Get focus (no grab_set to avoid focus deadlock with automation)
        win.after(100, lambda: win.focus_force())

        # Bind event to prevent window from losing focus
        win.bind('<FocusOut>', lambda e: self._refocus(win))

        # Build content
        self._build_content(win, w, h)

        # Start timers
        self._start_timers(win, index)

        self.logger.debug(f"Monitor {index}: {w}x{h}+{x}+{y}")
        return win

    def _build_content(self, win, w, h):
        """构建锁屏窗口内容"""
        # 顶部：系统时间
        time_frame = tk.Frame(win, bg='black')
        time_frame.pack(pady=(20, 10))
        
        time_label = tk.Label(time_frame, text="", font=('Consolas', 18),
                              fg='#888888', bg='black')
        time_label.pack()
        win.time_label = time_label

        # 中间弹性空间
        tk.Frame(win, bg='black').pack(expand=True)

        # 标语区域
        slogan_frame = tk.Frame(win, bg='black')
        slogan_frame.pack()
        win.slogan_frame = slogan_frame

        slogan_label = tk.Label(slogan_frame, text=self.slogan if self.slogan else "",
                                font=('Microsoft YaHei', 36, 'bold'),
                                fg='white', bg='black', wraplength=w - 100)
        slogan_label.pack()
        win.slogan_label = slogan_label

        # 倒计时
        countdown_label = tk.Label(win, text="", font=('Consolas', 24),
                                   fg='#cccccc', bg='black')
        countdown_label.pack(pady=10)
        win.countdown_label = countdown_label

        # 应急解锁按钮
        unlock_btn = tk.Button(win, text="应急解锁", font=('Microsoft YaHei', 14, 'bold'),
                               bg='#c0392b', fg='white', activebackground='#e74c3c',
                               activeforeground='white', relief='flat',
                               padx=30, pady=10, cursor='hand2',
                               command=lambda: self._enter_unlock_mode(win))
        unlock_btn.pack(pady=10)
        win.unlock_btn = unlock_btn

        # 应急解锁面板（初始隐藏）
        unlock_panel = tk.Frame(win, bg='black')
        win.unlock_panel = unlock_panel

        # 密码提示
        win.prompt_label = tk.Label(unlock_panel, text="请输入应急解锁密码以解锁",
                                    font=('Microsoft YaHei', 14),
                                    fg='#f39c12', bg='black')
        win.prompt_label.pack(pady=10)

        # 密码显示（掩码）
        win.password_display = tk.Label(unlock_panel, text="",
                                        font=('Consolas', 28, 'bold'),
                                        fg='white', bg='#222222',
                                        width=10, height=2)
        win.password_display.pack(pady=10)

        # 数字键盘
        keypad_frame = tk.Frame(unlock_panel, bg='black')
        keypad_frame.pack(pady=10)
        win.keypad_frame = keypad_frame

        self._build_keypad(win, keypad_frame)

        # 返回按钮
        return_btn = tk.Button(unlock_panel, text="返回", font=('Microsoft YaHei', 12),
                               bg='#7f8c8d', fg='white', activebackground='#95a5a6',
                               activeforeground='white', relief='flat',
                               padx=30, pady=8, cursor='hand2',
                               command=lambda: self._exit_unlock_mode(win))
        return_btn.pack(pady=10)
        win.return_btn = return_btn

        # 底部弹性空间（保存引用，供 _enter_unlock_mode 使用）
        win.bottom_spacer = tk.Frame(win, bg='black')
        win.bottom_spacer.pack(expand=True)

    def _build_keypad(self, win, frame):
        """构建数字键盘"""
        win.keypad_buttons = []

        keys = [
            ('1', 0, 0), ('2', 0, 1), ('3', 0, 2),
            ('4', 1, 0), ('5', 1, 1), ('6', 1, 2),
            ('7', 2, 0), ('8', 2, 1), ('9', 2, 2),
            ('⌫', 3, 0), ('0', 3, 1), ('', 3, 2),
        ]

        btn_w, btn_h = 5, 2
        btn_font = ('Microsoft YaHei', 16, 'bold')

        for text, row, col in keys:
            if text == '':
                # 空占位
                placeholder = tk.Frame(frame, bg='black', width=80, height=60)
                placeholder.grid(row=row, column=col, padx=3, pady=3)
                placeholder.grid_propagate(False)
                continue

            if text == '⌫':
                btn = tk.Button(frame, text=text, font=btn_font,
                                bg='#555555', fg='white',
                                activebackground='#777777', activeforeground='white',
                                relief='flat', width=btn_w, height=btn_h,
                                cursor='hand2',
                                command=lambda w=win: self._on_keypress(w, 'backspace'))
            else:
                btn = tk.Button(frame, text=text, font=btn_font,
                                bg='#333333', fg='white',
                                activebackground='#555555', activeforeground='white',
                                relief='flat', width=btn_w, height=btn_h,
                                cursor='hand2',
                                command=lambda w=win, t=text: self._on_keypress(w, t))

            btn.grid(row=row, column=col, padx=3, pady=3)
            win.keypad_buttons.append(btn)

    def _on_keypress(self, win, value):
        """处理键盘输入"""
        if self.cooldown_active:
            return

        if value == 'backspace':
            self.password_input = self.password_input[:-1]
        else:
            self.password_input += value
            if len(self.password_input) > 6:
                self.password_input = self.password_input[:6]

        # 更新显示
        win.password_display.config(text='*' * len(self.password_input))

        # 自动检查（6位输满）
        if len(self.password_input) == 6:
            self._check_password(win)

    def _check_password(self, win):
        """Check password"""
        if self.password_input == self.emergency_password:
            self.logger.info("Emergency password correct, unlocking")
            self._cleanup_and_exit()
        else:
            self.unlock_attempts += 1
            self.logger.warning(f"Wrong password (attempt #{self.unlock_attempts})")
            win.prompt_label.config(text=f"Wrong password, retry in {UNLOCK_COOLDOWN} second(s)")
            win.password_display.config(text="")
            self.password_input = ""
            self._start_cooldown(win)

    def _start_cooldown(self, win):
        """Start cooldown (global, all windows' keypads disabled)"""
        self.cooldown_active = True
        self.cooldown_remaining = UNLOCK_COOLDOWN

        # Disable all windows' keypad buttons
        self._set_all_keypads_state('disabled')

        def tick():
            self.cooldown_remaining -= 1
            if self.cooldown_remaining > 0:
                win.prompt_label.config(
                    text=f"Wrong password, retry in {self.cooldown_remaining} second(s)")
                win.after(1000, tick)
            else:
                self.cooldown_active = False
                self._set_all_keypads_state('normal')
                # Update prompt on all windows
                for w in self.windows:
                    try:
                        w.prompt_label.config(text="Enter emergency unlock password")
                    except Exception:
                        pass
                self.logger.info("Cooldown ended, ready for retry")

        win.after(1000, tick)

    def _set_all_keypads_state(self, state):
        """Set all windows' keypad button states"""
        for w in self.windows:
            try:
                for btn in w.keypad_buttons:
                    btn.config(state=state)
            except Exception:
                pass

    def _enter_unlock_mode(self, win):
        """Enter emergency unlock mode"""
        self.logger.info("Entering emergency unlock mode")

        # 隐藏标语
        win.slogan_frame.pack_forget()
        win.unlock_btn.pack_forget()

        # 显示解锁面板（放在 bottom_spacer 之前）
        win.unlock_panel.pack(expand=True, before=win.bottom_spacer)
        win.password_display.config(text="")
        win.prompt_label.config(text="请输入应急解锁密码以解锁")
        self.password_input = ""

        if not self.cooldown_active:
            self._set_all_keypads_state('normal')

    def _exit_unlock_mode(self, win):
        """Exit emergency unlock mode"""
        self.logger.info("Exiting emergency unlock mode")

        # Hide unlock panel
        win.unlock_panel.pack_forget()

        # Show slogan and button
        win.slogan_frame.pack(before=win.countdown_label)
        win.unlock_btn.pack(before=win.countdown_label)

        self.password_input = ""

    def _refocus(self, win):
        """Re-focus window (debounced, avoid infinite loop)"""
        if self._refocus_blocked:
            return
        self._refocus_blocked = True
        try:
            win.focus_force()
            win.lift()
        except Exception:
            pass
        # 解锁 refocus（给 tkinter 时间处理完事件）
        win.after(200, self._unblock_refocus)

    def _unblock_refocus(self):
        self._refocus_blocked = False

    def _start_timers(self, win, index):
        """Start timers (only countdown on first window to avoid double-counting)"""
        self._update_time(win)
        if index == 0:
            self._update_countdown(win)
        self._keep_topmost(win)

    def _update_time(self, win):
        """Update system time display"""
        now = datetime.now().strftime('%Y-%m-%d %H:%M:%S')
        win.time_label.config(text=now)
        win.after(1000, lambda: self._update_time(win))

    def _update_countdown(self, win):
        """Update countdown"""
        if self.remaining > 0:
            win.countdown_label.config(
                text=f"{self.remaining} second(s) until auto-unlock")
            self.remaining -= 1
            win.after(1000, lambda: self._update_countdown(win))
        else:
            self.logger.info("Countdown reached zero, auto-unlocking")
            self._cleanup_and_exit()

    def _keep_topmost(self, win):
        """Keep window topmost"""
        try:
            win.lift()
            win.attributes('-topmost', True)
        except Exception:
            pass
        win.after(500, lambda: self._keep_topmost(win))

    def _cleanup_and_exit(self):
        """Cleanup and exit"""
        self.logger.info("Lock screen exiting")
        
        # 卸载键盘钩子
        self.uiaccess.remove_keyboard_hook()

        # 关闭所有窗口
        try:
            self.root.destroy()
        except Exception:
            pass

        # Use os._exit to ensure clean exit (skip finally/atexit that may hang)
        os._exit(0)

    def run(self):
        """Run lock screen"""
        # Install keyboard hook
        if self.uiaccess.install_keyboard_hook():
            self.logger.info("Keyboard hook installed")
        else:
            self.logger.warning("Keyboard hook install failed, some shortcuts may not be intercepted")

        # Enter main loop (using root window's mainloop)
        try:
            self.root.mainloop()
        except KeyboardInterrupt:
            self.logger.info("Interrupt signal received")
        finally:
            self._cleanup_and_exit()

# ========================================================================
# 主程序入口
# ========================================================================

def launch_lock_screen():
    """启动锁屏流程"""
    config = ConfigManager(get_config_path())
    valid, errors = config.validate()
    if not valid:
        print("Config errors:")
        for e in errors:
            print(f"  {e}")
        print("Please run the config window to fix configuration.")
        sys.exit(1)

    is_debug = '--debug' in sys.argv or '-debug' in sys.argv

    # Logging
    retention_days = 0 if is_debug else config.get_int('log_retention_days', 14)
    log_manager = LogManager(get_log_dir(), retention_days, debug=is_debug)
    logger = log_manager.get_logger()

    # Clean old logs (only in non-debug mode; clean_old_logs handles retention_days=0 correctly)
    if not is_debug:
        log_manager.clean_old_logs()

    # Load UIAccess DLL
    uiaccess = UIAccessHelper()
    dll_loaded = uiaccess.load()
    if dll_loaded:
        uiaccess.set_debug_mode(is_debug)
        logger.info("UIAccess helper DLL loaded")
    else:
        logger.warning("UIAccess helper DLL not loaded, falling back to tkinter lock screen")

    # Debug mode: skip UIAccess elevation
    if is_debug:
        logger.info("Debug mode: skipping UIAccess elevation")
    else:
        # Check admin rights
        if not is_admin():
            logger.info("Not running as admin, requesting elevation...")
            if run_as_admin(extra_args=['-lock']):
                logger.info("Admin elevation requested, current process exiting")
                sys.exit(0)
            else:
                logger.warning("Admin elevation denied or failed, continuing anyway")

        # Try to get UIAccess
        if dll_loaded:
            logger.info("Attempting to acquire UIAccess...")
            result = uiaccess.prepare_for_uiaccess()
            if result == 0:
                logger.info("UIAccess ready")
            elif result == 5:
                logger.warning("Admin rights required for UIAccess")
            elif result == 1168:
                logger.warning("winlogon.exe token not found")
            else:
                logger.warning(f"PrepareForUIAccess returned error: {result}")

    # Try DLL-based lock screen first
    if dll_loaded:
        duration = config.get_int('lock_duration', 10)
        password = config.get('emergency_password', '000000')
        slogan = config.get('lock_slogan', '')

        logger.info(f"Starting lock screen via DLL: duration={duration}s, password=******, slogan='{slogan}'")

        result = uiaccess.run_lock_screen(duration, password, slogan, is_debug)
        logger.info(f"Lock screen exited, result: {result}")
        sys.exit(0)
    else:
        # Fallback: tkinter-based lock screen
        logger.info("DLL not available, using tkinter fallback lock screen")
        lock_screen = LockScreen(config, log_manager, uiaccess, debug=is_debug)
        lock_screen.run()

def main():
    """主入口"""
    # 解析命令行参数
    args = sys.argv[1:]
    
    is_lock = '-lock' in args or '--lock' in args
    is_debug = '-debug' in args or '--debug' in args

    if is_debug:
        # Debug mode: enable console
        if not sys.stdout:
            AllocConsole()
        try:
            AttachConsole(ATTACH_PARENT_PROCESS)
        except Exception:
            pass
        print("=" * 50)
        print(f"{APP_NAME} v{APP_VERSION} - Debug Mode")
        print("=" * 50)
        launch_lock_screen()
    elif is_lock:
        # Control mode
        launch_lock_screen()
    else:
        # Config window
        is_unique, mutex_handle = check_single_instance()
        if not is_unique:
            messagebox.showerror("Error", f"{APP_NAME} is already running!\nPlease close the existing instance first.")
            sys.exit(1)

        config = ConfigManager(get_config_path())
        app = ConfigWindow(config)
        app.run()

if __name__ == '__main__':
    main()