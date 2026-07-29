# 第三方组件与署名（Third-Party Notices）

本项目以 **GNU General Public License v3.0（GPL-3.0）** 发布。除标准库与 Windows 系统自带能力外，唯一引入的第三方组件如下。

---

## 1. Microsoft.Web.WebView2

- **用途**：配置界面的离线 HTML 渲染引擎（Evergreen 模式，依赖系统已装的 Edge WebView2 Runtime）。
- **许可**：MIT License（与 GPL-3.0 兼容）。
- **版本**：1.0.4078.44（NuGet 包；x64 原生 `WebView2Loader.dll` 由包自动注入）。
- **版权**：© Microsoft Corporation. All rights reserved.

**MIT License 文本摘要**：

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions: The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED.

完整 MIT 文本见包内 `LICENSE` 或 https://opensource.org/licenses/MIT 。

---

## 2. Material Design 3 设计令牌（Design Tokens）

- **用途**：配置界面（`src/ConfigPage.html` 的 `:root` 与 C# 动态配色）所采用的精确颜色角色、形状尺度、字体比例与动效令牌数值。
- **来源**：Google **Material Design 3** 官方 baseline 颜色（source `#6750A4`）与 token 体系；数值取自 Material Design 规范及 `material-color-utilities` / `material-web` 公开常量。
- **许可**：Material Design 规范与令牌本身遵循 [Google APIs Terms of Service](https://developers.google.com/terms) 及 Material Design 的品牌使用准则；其开源参考实现（`material-web`、`material-color-utilities`）为 Apache-2.0。本项目**仅采用其公开的常量数值作为设计基准**，未包含或改编上述库的源代码，组件仍为自行实现。

> 说明：本项目不打包、不改编任何 Material Design 的开源库代码；WebView2 是唯一的二进制/库级第三方依赖。
