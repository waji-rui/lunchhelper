# LunchHelper

> 面向纯触控设备（无键盘鼠标）的轻量级锁屏助手：手动（或到点）锁定屏幕，倒计时结束后自动解锁；支持应急密码解锁与防撬锁。

- **许可证**：GNU General Public License v3.0（GPL-3.0）。本仓库代码均以此为许可发布，可自由使用、修改、再分发，但须以相同许可证开源。详见 [`LICENSE`](LICENSE)。
- **运行环境**：Windows 10 / 11 64 位。系统级依赖为 **.NET Framework 4.8**（Windows 10 1903+ 与 Windows 11 已预装；个别精简系统若缺失，首次运行会由系统引导下载安装）；配置界面渲染依赖系统已装的 **Microsoft Edge WebView2 Runtime**（Win11 预装、多数 Win10 已装，缺失时配置页会给出提示而非白屏）。
- **依赖立场**：标准库与 Windows 系统能力之外，仅引入**一个**经 GPL-3.0 兼容审查的第三方组件 —— `Microsoft.Web.WebView2`（MIT 许可）。详见 [第三方组件与署名](#第三方组件与署名) 与 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。
- **语言**：C# / WinForms。发布形态为**便携 zip 包**：`LunchHelper.exe` + WebView2 运行所需的几个 DLL（`Microsoft.Web.WebView2.*.dll`、`WebView2Loader.dll` 及 `runtimes/`） + `plugins/`，解压到任意目录即可运行；WebView2 以 Evergreen 方式复用系统已装的 WebView2 Runtime，不打包浏览器引擎本身。

---

## 启动方式

直接运行 `LunchHelper.exe`（无参数）进入**配置界面**；带参数启动可进入其它模式：

| 命令 | 模式 | 说明 |
|------|------|------|
| `LunchHelper.exe` | 配置界面 | 设置锁定时长、应急密码、日志天数、标语；含「恢复默认」「关于」 |
| `LunchHelper.exe -lock` | 控制（锁屏）模式 | 全屏置顶锁屏，自动拉起守护进程 |
| `LunchHelper.exe -debug` | 调试模式 | 配置界面 + 置顶控制台 + 详细日志（且不自动删除日志） |

> `-debug` 与 `-lock` 面向不同场景；调试模式用于排查问题，不进入锁屏。

---

## 功能要点

### 配置项（默认值）
- **锁定时长**：10（秒），≥1
- **应急解锁密码**：`000000`（仅允许 6 位纯数字；配置文件内只保存其 PBKDF2-HMAC-SHA256 哈希 + 每安装随机盐，不存明文）。盐可阻止预计算/彩虹表，但 6 位 PIN 空间仅 100 万，离线暴破约需数分钟至数小时（取决于迭代次数与硬件）；真正的防护来自**在线 5 秒锁死**——跑完 100 万次尝试约需 58 天。
- **日志保存上限天数**：14（天），≥0；为 0 时不保存任何日志
- **锁屏标语**：默认空（空时锁屏显示“设备已锁定”）
- 提供「恢复默认配置」按钮一键还原；「关于」展示软件信息

### 锁屏界面（黑底白字、全屏置顶）
- 顶部：实时系统时间
- 中部：大字号锁屏标语
- 标语下方：倒计时“xx秒后将自动解锁屏幕”
- 倒计时上方：「应急解锁」按钮
- 点击「应急解锁」→ 标语隐藏、按钮变「返回」、中部变为数字触摸键盘，上方提示“请输入应急解锁密码以解锁”
- 输入正确密码 → 立即退出解锁
- 输入错误 → 提示变为“密码错误，请重试”，并锁定键盘 5 秒（防暴破，每 5 秒限一次尝试）
- 点「返回」→ 回到主锁屏（倒计时继续）
- **倒计时归零：无论处于何种状态，锁屏界面总是退出解锁**

### 防撬锁
- 全屏置顶 + 低级键盘钩子屏蔽 Win / Win+L / Win+D / Win+R / Alt+Tab / Alt+Esc / Ctrl+Esc / Ctrl+Shift+Esc / Alt+F4 等快捷键
- 不修改任务管理器注册表：真正的 `uiAccess` 置顶窗口已绘制在任务管理器之上，且键盘钩子已拦截 `Ctrl+Shift+Esc`；移除注册表禁用逻辑以避免触发安全软件 HIPS 拦截
- 守护进程（`-guardian`，由锁屏自动拉起）：监视锁屏进程，若被异常结束则自动重启；若已正常解锁则一同退出（进程间通过解锁标记文件协调，避免“解锁后又被重启”的死循环）
- **已知限制（软锁的系统级边界）**：本程序为「软锁」，以下行为普通前台程序无法在「输入层面」拦截，属 Windows 设计限制、非本程序缺陷；但借助 `uiAccess="true"` 的**最强置顶**，锁屏全屏窗口会绘制在它们之上，使其不可见且不可用，实践中锁屏不破：
  - `Ctrl+Alt+Del`（SAS 安全序列）及由此打开的**安全桌面 / 登录界面**：位于一切窗口（含 uiAccess）之上，真正无法覆盖，是唯一的硬例外。
  - 屏幕边缘滑动手势：从最右滑出**操作中心**、最左滑出**任务视图**、最底滑出**开始菜单**（由 Windows 外壳 explorer/DWM 在合成器层面识别）。`uiAccess` 置顶窗口会盖在它们之上，即使手势触发、面板也被挡在黑窗之后；前提是 `uiAccess` 已生效（exe 经代码签名或置于 ACL 正确的 `Program Files`）。
  - 已做的尽力缓解：低级键盘钩子屏蔽 Win/Alt+Tab/Ctrl+Esc 等退出/切换类按键。锁屏为覆盖所有显示器的全屏置顶窗口，本身已挡住桌面与其它程序，普通触摸/点击无法落到窗口之外。
  - 如需在输入层面彻底禁用边缘手势，需改用 Windows「指定访问 / 展台模式（Assigned Access / Kiosk）」等系统级方案，超出本工具范围。

---

## 部署与 uiAccess 提示

锁屏窗口的「超级置顶」依赖 `uiAccess="true"` 的应用程序清单，可置顶于几乎所有窗口之上。但 Windows 对此有严格的启动要求；**当前发布的 Release 为未签名构建（`uiAccess="false"`）**，因此：

| 放置 / 启动方式 | 能否盖任务管理器 | 说明 |
|---|---|---|
| 任意目录双击（普通用户） | ❌ 仅普通置顶 | 能覆盖桌面与大部分窗口，但会被任务管理器（High IL）覆盖 |
| `C:\Program Files\LunchHelper\` 双击 | ✅ 可覆盖 | 受保护目录 + `uiAccess` 清单，Windows 授予强置顶（无需签名、无 UAC 弹窗） |
| 任意目录「以管理员身份运行」 | ✅ 可覆盖 | 管理员 High IL 与任务管理器同级；配置里「UI Access 超级置顶」开关控制是否自动申请 UAC 提权 |

### 具体规则

- **当前发布（便携 zip，未签名）**：解压到任意目录双击即用，退化为普通置顶窗口（已能覆盖桌面与大部分程序）；要获得最强置顶，把解压目录放到 `Program Files` 下，或开启「UI Access 超级置顶」开关以管理员运行。
- **Program Files 路径**：放到 `C:\Program Files\LunchHelper\` 后双击，无需 UAC 即可获得 uiAccess 强置顶（前提是系统默认 UAC 策略 `EnableSecureUIAPaths=1`，绝大多数机器如此）。
- **管理员运行**：开启「UI Access 超级置顶」后，每次启动会弹一次 UAC 申请提权，提权后即获强置顶。

> 已知限制：`Ctrl+Alt+Del` 安全序列打开的安全桌面位于一切窗口（含 uiAccess）之上，真正无法覆盖，属 Windows 设计限制。

---

## 发布（贡献者）

发布形态为**便携 zip 包**（非安装器、非单 exe）。CI（`.github/workflows/build-and-sign.yml`）在打 `v*` 标签或手动触发时，于 GitHub Windows runner 上 `msbuild` 构建，再用 `pack.ps1` 把以下内容打包为 `LunchHelper-<version>.zip` 并挂到 GitHub Release：

- `LunchHelper.exe`
- WebView2 运行所需的 DLL：`Microsoft.Web.WebView2.Core.dll`、`Microsoft.Web.WebView2.WinForms.dll`、`Microsoft.Web.WebView2.Wpf.dll`、`WebView2Loader.dll`（及 `runtimes/`）
- `plugins/`（示例插件）
- `LICENSE`、`README.md`、`THIRD-PARTY-NOTICES.md`

用户下载 zip 解压即用，无需安装。

### 代码签名状态
本项目追求**完全免费**的发布路径。曾申请 SignPath Foundation 免费签名，但因项目知名度 / 外部信号不足被拒；**目前以未签名形态发布**，`uiAccess` 保持 `false`。强置顶改由「Program Files 受保护目录」或「管理员运行」实现（见上文）。SignPath 申请保留，待项目知名度提升后可重试；届时在 CI 中恢复“构建前将 `uiAccess` 改为 `true`”的步骤即可。本地开发期体验 uiAccess 置顶可用仓库内 `SelfSignTest.ps1` 做自签名部署到 Program Files（仅本机有效）。

---

## 构建（仅开发者 / 贡献者需要）

> **普通用户无需构建**：直接到 Releases 下载已编译好的 `LunchHelper.exe`，双击即可在 Windows 10/11 64 位上运行（系统自带 .NET Framework 4.8，无需安装运行时）。以下说明只面向**想从源码构建**的开发者或贡献者——构建环境依赖为「带 .NET 桌面开发工作负载的 Visual Studio（2017 及以后任意版本，含 2022/2026）」，这是 Windows 桌面（WinForms）项目的标准要求。

### 方式一：Visual Studio（推荐）
用任意版本 Visual Studio（Community 免费版即可，需安装“.NET 桌面开发”工作负载）打开 `LunchHelper.csproj`，菜单“生成 → 生成解决方案”。输出在 `bin\Release\LunchHelper.exe`。

### 方式二：build.bat
双击 `build.bat`。脚本通过 `vswhere` 自动定位本机任意版本的 Visual Studio（包括 VS2026/2022/2019 及自定义安装路径）并调用其 MSBuild，找不到时给出清晰提示。

> 说明：本工程为经典 .csproj，目标框架 .NET Framework 4.8，使用 WinForms 与系统自带能力；唯一的第三方 NuGet 包为 `Microsoft.Web.WebView2`（MIT 许可，GPL-3.0 兼容），其署名见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

---

## 文件结构

```
LunchHelper/
├── LICENSE                 # GPL-3.0 完整文本
├── README.md               # 本文档
├── LunchHelper.csproj      # 工程文件（.NET Framework 4.8 / WinForms）
├── app.manifest            # 含 uiAccess 清单（当前默认 false，未签名发布）
├── build.bat               # 一键构建脚本（Windows）
├── pack.ps1                # 构建后打包发布 zip（CI 与本地共用）
├── plugins/                # 插件目录（含示例插件 Com.Example.Demo）
├── src/
│   ├── Program.cs          # 入口：参数解析、单实例互斥、模式分发、拉起守护
│   ├── NativeMethods.cs    # 窗口/控制台相关 P/Invoke
│   ├── LockSignal.cs       # 锁屏↔守护进程的解锁信号（标记文件）
│   ├── ConsoleHelper.cs    # 调试模式控制台分配与置顶
│   ├── ConfigManager.cs    # 配置读写、默认值、PBKDF2-HMAC-SHA256 密码哈希（每安装随机盐）
│   ├── Logger.cs           # 日志（logs/ 按天滚动、调试模式详细且不删除）
│   ├── AntiTamper.cs       # 键盘钩子屏蔽快捷键（Win/Alt+Tab/Ctrl+Esc 等）
│   ├── Guardian.cs         # 守护进程（监视并重启锁屏）
│   ├── NumericPad.cs       # 自绘大字号数字触摸键盘
│   ├── AboutForm.cs        # 关于窗口
│   ├── ConfigForm.cs       # 触控配置界面
│   └── LockForm.cs         # 锁屏主窗体（状态机、倒计时、防暴破）
├── config.json             # 运行时生成（与 exe 同目录）
└── logs/                   # 日志目录（运行时生成）
```

---

## 第三方组件与署名

本项目在标准库与 Windows 系统能力之外，仅引入一个第三方组件，其许可与署名见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)：

| 组件 | 版本 | 许可 | 用途 |
|---|---|---|---|
| Microsoft.Web.WebView2 | 1.0.4078.44 | MIT（GPL-3.0 兼容） | 配置界面离线 HTML 渲染（Evergreen，依赖系统 WebView2 Runtime） |
| Material Design 3 设计令牌 | — | Google Material Design 规范 | 配置界面颜色角色 / 形状 / 字体 / 动效令牌数值（仅采用公开常量，未包含或改编其库代码） |

> 除 WebView2 外，本项目**不打包、不改编**任何第三方库源码；组件（开关、按钮、卡片等）均为自行实现，符合“零第三方源码依赖”的立场。

---

## 免责声明

本程序按“原样”提供，**不提供任何担保**（含适销性与特定用途适用性的暗示担保）。使用本程序锁屏期间请自留可解锁方式（牢记应急密码），因使用本软件导致的任何后果由使用者自行承担。
