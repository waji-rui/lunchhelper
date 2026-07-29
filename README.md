# LunchHelper

> 面向纯触控设备（无键盘鼠标）的轻量级锁屏助手：手动（或到点）锁定屏幕，倒计时结束后自动解锁；支持应急密码解锁与防撬锁。

- **许可证**：GNU General Public License v3.0（GPL-3.0）。本仓库代码均以此为许可发布，可自由使用、修改、再分发，但须以相同许可证开源。详见 [`LICENSE`](LICENSE)。
- **运行环境**：Windows 10 / 11 64 位。系统级依赖为 **.NET Framework 4.8**（Windows 10 1903+ 与 Windows 11 已预装；个别精简系统若缺失，首次运行会由系统引导下载安装）；配置界面渲染依赖系统已装的 **Microsoft Edge WebView2 Runtime**（Win11 预装、多数 Win10 已装，缺失时配置页会给出提示而非白屏）。
- **依赖立场**：标准库与 Windows 系统能力之外，仅引入**一个**经 GPL-3.0 兼容审查的第三方组件 —— `Microsoft.Web.WebView2`（MIT 许可）。详见 [第三方组件与署名](#第三方组件与署名) 与 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。
- **语言**：C# / WinForms，单文件可执行（`LunchHelper.exe`；WebView2 以 Evergreen 方式使用系统运行时，不打包引擎）。

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

锁屏窗口的「超级置顶」依赖 `uiAccess="true"` 的应用程序清单，可置顶于几乎所有窗口之上。但该特权有严格的 Windows 启动要求，因此分两种构建：

| 构建类型 | manifest | 双击运行 | 能否盖任务管理器 |
|---|---|---|---|
| **本地 / 未签名预览版** | `uiAccess="false"`（默认） | ✅ 任意目录可启动 | ❌ 仅普通置顶 |
| **SignPath 签名 Release** | `uiAccess="true"`（CI 自动开启） | 需放 `Program Files` 或开启开关后 UAC 提权 | ✅ 可覆盖 |

### 具体规则

- **默认构建**：`uiAccess="false"`，从 `Downloads` / 桌面 / U 盘双击都能直接运行，退化为普通置顶窗口（仍能覆盖桌面与大部分窗口）。
- **签名 Release**：GitHub Actions 会在构建前把 manifest 改成 `uiAccess="true"`；此时要获得完整超级置顶，还需满足：
  1. exe 位于受保护目录（如 `C:\Program Files\LunchHelper\`）—— 双击即生效，无 UAC；或
  2. 以管理员身份运行 —— 配置界面中的「UI Access 超级置顶」开关控制是否自动申请 UAC 提权。

### 推荐用法

- 想“双击即用、不弹 UAC”：使用默认构建，或将签名版放入 `Program Files`。
- 想“从任意位置都获得最强置顶”：使用签名版，并开启「UI Access 超级置顶」开关（每次启动会弹一次 UAC）。

---

## 发布与代码签名（贡献者）

本程序追求**完全免费**的发布路径，使 `uiAccess="true"` 真正生效（即锁屏能盖住一切普通窗口）。Windows 授予 uiAccess 的两个条件之一：**exe 经可信证书代码签名**（详见上文「部署与 uiAccess 提示」）。下面的路径不花一分钱：

1. **首个 Release（未签名即可）**：先把代码推到 GitHub 公共仓库并发布第一个 Release（`LunchHelper.exe` 未签名也能跑，只是退化为普通置顶）。这一步是为了满足 SignPath 对「已有开源发布历史」的要求。
2. **申请 SignPath Foundation 免费签名**：到 https://signpath.io/open-source 提交申请（开源免费，证书署名 SignPath Foundation，私钥存于其 HSM）。
3. **CI 自动签名**：仓库已包含 `.github/workflows/build-and-sign.yml`——在 GitHub 托管的 Windows runner 上 `msbuild` 构建，经 `actions/upload-artifact` 上传产物，再调用官方 `signpath/github-action-submit-signing-request` 提交签名，最后把**签名后的 exe** 挂到 GitHub Release。使用前需在仓库 Settings 配置 SignPath 的 API Token 与 Organization ID（详见该工作流文件顶部的注释）。
4. **InnoSetup 安装器**：`installer/LunchHelper.iss` 把签名后的 exe 装入 `C:\Program Files\LunchHelper\`（默认 UAC 策略下受保护目录同样可授予 uiAccess），免费且兼容。用 `iscc installer\LunchHelper.iss` 构建安装包（注意先替换脚本里的 `<your-username>` 与 AppId GUID）。

> 本地开发期想先体验 uiAccess 置顶效果，可用仓库内的 `SelfSignTest.ps1` 做自签名 + 部署到 Program Files（仅本机有效，非分发用途）。

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
├── app.manifest            # 含 uiAccess=true 的应用程序清单
├── build.bat               # 一键构建脚本（Windows）
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
