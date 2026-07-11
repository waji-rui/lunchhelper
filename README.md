# LunchHelper - 午休锁屏助手

定时锁定屏幕，让你安心午休，不再担心误触或他人操作电脑。

## 功能特性

- **全屏锁屏**：利用 `CreateWindowInBand` API + UIAccess 令牌窃取，将窗口置于 UIACCESS band，覆盖任务管理器、开始菜单、触摸手势等一切系统窗口
- **无需数字签名**：通过 winlogon.exe 令牌窃取技术获取 UIAccess，无需购买代码签名证书
- **无 UAC 也可运行**：`CreateWindowInBand` 是底层 API，即使 UAC 禁用也能将窗口置于 UIACCESS band
- **AttachThreadInput 焦点窃取**：无论由何种方式启动，锁屏窗口都能正确获取焦点，解决自动化程序启动时按钮无响应的问题
- **定时解锁**：可配置锁定时长，倒计时归零自动解锁
- **应急解锁**：6 位数字密码解锁，5 秒冷却防暴力破解
- **多显示器**：自动检测并覆盖所有显示器
- **键盘拦截**：低级键盘钩子拦截 Alt+F4、Alt+Tab、Win 键等系统快捷键
- **防撬锁**：单实例运行、窗口无边框、无关闭按钮、进程保护
- **触屏友好**：大按钮设计，适合纯触屏设备使用
- **配置管理**：图形化配置界面，支持恢复默认
- **日志系统**：完善的日志记录，支持按天轮转和自动清理

## 启动方式

| 方式 | 说明 |
|------|------|
| 直接运行 `LunchHelper.exe` | 进入配置界面 |
| `LunchHelper.exe -lock` | 控制模式（锁屏） |
| `LunchHelper.exe -debug` | 调试模式（控制台 + 详细日志） |

## 系统要求

| 项目 | 要求 |
|------|------|
| 操作系统 | Windows 10 64 位 |
| 权限 | 需要管理员权限（UIAccess 功能需要） |
| Python（仅源码运行） | Python 3.13+ |

## 目录结构

```
LunchHelper/
├── lunch_helper.py          # 主程序（Python）
├── uiaccess_helper.c         # UIAccess 辅助 DLL 源码（C）
├── uiaccess_helper.dll       # 编译后的 DLL
├── build_dll.bat             # DLL 编译脚本
├── build_exe.bat             # EXE 打包脚本
├── build_all.bat             # 一键构建脚本
├── README.md                 # 本文件
├── lunch_helper.ini          # 配置文件（运行时生成）
└── logs/                     # 日志目录（运行时生成）
    └── lunch_helper.log
```

## 配置说明

配置文件位于程序同目录下的 `lunch_helper.ini`：

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `lock_duration` | 10 | 锁定时长（秒），建议 30-600 |
| `emergency_password` | 000000 | 应急解锁密码（6 位数字） |
| `log_retention_days` | 14 | 日志保存天数，0 表示不保存 |
| `lock_slogan` | （空） | 锁屏标语，显示在锁屏中央 |

## 构建指南

### 前置条件

1. **Python 3.13+** — [python.org](https://www.python.org/downloads/)
2. **C 编译器**（二选一）：
   - [Visual Studio Build Tools](https://visualstudio.microsoft.com/downloads/)（推荐）
   - [MinGW-w64](https://www.mingw-w64.org/)
3. **PyInstaller** — `pip install pyinstaller`

### 步骤

```batch
:: 1. Compile C DLL (choose one)
build_dll.bat msvc     :: use MSVC
build_dll.bat mingw    :: use MinGW

:: 2. Package EXE
build_exe.bat          :: single-file EXE
build_exe.bat folder   :: folder mode

:: Or one-click build
build_all.bat
```

### 构建注意事项

1. **路径中不要包含 `&` 符号**：批处理中 `&` 是命令分隔符，项目路径若包含 `&` 会导致脚本异常。建议路径只使用字母、数字、下划线、空格或短横线。
2. **以管理员权限运行**：`build_dll.bat` 编译本身不需要管理员，但编译出的 DLL 在运行 `-lock` 模式时会被用于获取 UIAccess，因此最终运行 `LunchHelper.exe -lock` 时需要管理员权限。
3. **编译命令**：`build_dll.bat` 已链接 `user32.lib`、`advapi32.lib`、`shell32.lib`、`comctl32.lib`、`gdi32.lib`（MinGW 对应 `-luser32 -ladvapi32 -lshell32 -lcomctl32 -lgdi32`）。
4. **DLL 路径**：`build_exe.bat` 使用绝对路径 `%CD%\%DLL_NAME%` 将 DLL 打包进 EXE，避免 PyInstaller 因工作目录变化而找不到文件。

## 技术原理

### 架构概览

```
lunch_helper.py (Python)
  │
  ├── Config mode: tkinter GUI (配置界面)
  │
  └── Lock mode: calls uiaccess_helper.dll
       │
       ├── PrepareForUIAccess()   → token theft from winlogon.exe
       ├── InstallKeyboardHook()  → WH_KEYBOARD_LL hook
       └── RunLockScreen()        → CreateWindowInBand + Win32 UI
            │
            ├── Enumerate monitors
            ├── Create per-monitor fullscreen windows
            ├── Win32 message loop (countdown, keypad, password)
            └── Return when unlocked (0=timeout, 1=password)
```

### 锁屏窗口技术栈

**C DLL 负责全部锁屏 UI**：纯 Win32 API 创建窗口和控件，不依赖 Python tkinter。这解决了两个关键问题：

1. **焦点问题**：使用 `AttachThreadInput` 从任何前台窗口窃取焦点，即使由自动化程序启动也能正确响应触摸/点击
2. **UIAccess 窗口**：使用 `CreateWindowInBand` (user32.dll ordinal 2488) 直接将窗口放入 `ZBID_UIACCESS` band，覆盖系统级 UI

### CreateWindowInBand 与 Z-Band

Windows 10 将窗口分为多个 Z-Order Band：

| Band | 值 | 示例 |
|------|------|------|
| ZBID_DESKTOP | 0 | 普通应用窗口 |
| ZBID_UIACCESS | 1 | UIAccess 窗口（本程序使用） |
| ZBID_SYSTEM_TOOLS | 13 | 任务管理器、触摸手势 |

`CreateWindowInBand(ZBID_UIACCESS)` 直接将窗口放入 band 1，无需依赖 `SetWindowPos(HWND_TOPMOST)` 的 band 穿越机制。

### 为什么无需数字签名和 UAC

1. **UIAccess 令牌**：通过复制 winlogon.exe 的 SYSTEM 令牌并设置 `TokenUIAccess=1` 获取
2. **CreateWindowInBand**：这是一个底层 API，即使令牌窃取失败（如 UAC 禁用），仍可尝试将窗口放入 UIACCESS band
3. **降级策略**：如果 `CreateWindowInBand` 不可用，自动回退到 `CreateWindowEx` + `WS_EX_TOPMOST`

## 借物表 / 参考项目

| 名称 | 链接 | 说明 |
|------|------|------|
| **killtimer0/uiaccess** | [GitHub](https://github.com/killtimer0/uiaccess) | 通过 System 令牌获取 UIAccess 的核心实现，本项目 DLL 的核心参考 |
| **ADeltaX Blog** | [blog.adeltax.com](https://blog.adeltax.com/window-z-order-in-windows-10/) | Windows 10 窗口 Z 序原理详解 |
| **ADeltaX/MobileShell** | [GitHub](https://github.com/ADeltaX/MobileShell) | CreateWindowInBand 私有 API 示例 |
| **julielele (CSDN)** | [博客](https://blog.csdn.net/julielele/article/details/131173525) | Python 获取 UIAccess 的参考实现 |
| **小流汗黄豆 (CSDN)** | [博客](https://blog.csdn.net/weixin_42112038/article/details/126329951) | 令牌窃取实现窗口超级置顶的方法 |
| **爱编程的叶一笑 (CSDN)** | [博客](https://blog.csdn.net/qq_59942146/article/details/130894663) | 自签名证书 + 清单文件实现 UIAccess |
| **涟幽516 (CSDN)** | [博客](https://blog.csdn.net/qq_59075481/article/details/144597294) | 如何正确获取 UIAccess 的总结 |
| **Microsoft Docs** | [learn.microsoft.com](https://learn.microsoft.com/zh-cn/windows/win32/winauto/uiauto-securityoverview) | 辅助技术的安全注意事项 |
| **阿里云先知社区** | [xz.aliyun.com](https://xz.aliyun.com/t/4126) | 如何滥用 Access Tokens UIAccess 绕过 UAC |

## 第三方库

| 库 | 用途 | 许可证 |
|----|------|--------|
| **tkinter** | Python 标准 GUI 库（Python 自带） | Python 许可证 |
| **ctypes** | Python 标准库，调用 Windows API | Python 许可证 |
| **configparser** | Python 标准库，INI 配置文件解析 | Python 许可证 |
| **logging** | Python 标准库，日志系统 | Python 许可证 |
| **PyInstaller** | 打包工具（仅构建时使用） | GPL |

## 许可证

本项目仅供个人学习和合法使用。请勿用于非法用途。

## 免责声明

- 本程序会拦截系统快捷键，包括 Alt+F4、Win 键等，倒计时结束后自动恢复
- Ctrl+Alt+Delete 安全桌面属于内核级保护，无法在用户态拦截
- 如遇紧急情况，可强制重启电脑（长按电源键）
- 使用本程序造成的数据丢失或工作中断，开发者不承担任何责任