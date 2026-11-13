# Third-party notices

Performix 本体以 MIT 许可发布（见 [LICENSE](LICENSE)）。本文件列出仓库里直接携带或运行时依赖的
第三方组件，以及各自的许可与义务。许可原文放在 `third_party/` 下，能离线核对。

> 判定原则：**没有许可原文、或来源不可追溯的代码/素材不进入本仓库。**
> 因许可问题被排除的内容，见 [docs/LICENSING.md](docs/LICENSING.md)。

---

## 1. PreySense（MIT，上游代码来源）

本项目的 Core / Overlay / App / Legacy.Wpf / Probe / Tests 源码来自 PreySense
（原仓库内部代号 Predex）的 fork 分支，按 MIT 许可使用与再分发。上游 copyright 行保留在
[LICENSE](LICENSE) 里。

```
MIT License

Copyright (c) 2026 PreySense contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 2. NvAPIWrapper.Net 0.8.1.101（LGPL-3.0）

- 用途：NVIDIA NVAPI 的托管封装。`Performix.Core` 通过 NuGet 引用，用于 GPU 时钟/功耗偏移、
  风扇与温度读取、D3D/DDS 状态。
- 许可：GNU Lesser General Public License v3.0
- Copyright (C) 2017-2020 Soroush Falahati
- 上游：<https://github.com/falahati/NvAPIWrapper>
- NuGet：<https://www.nuget.org/packages/NvAPIWrapper.Net>
- 许可原文：[third_party/NvAPIWrapper/LICENSE.txt](third_party/NvAPIWrapper/LICENSE.txt)

**LGPL-3.0 合规声明（针对动态链接的 .NET 库）**

1. 本仓库**不修改** NvAPIWrapper 源码，也未提交其编译产物；构建时按 NuGet 原包还原，
   `NvAPIWrapper.dll` 原样输出到程序目录。
2. `NvAPIWrapper.Net` 是独立程序集，与 Performix 程序集之间只经由公开 API 调用，
   **可被替换**：用同版本（或兼容）的官方构建产物覆盖输出目录里的 `NvAPIWrapper.dll`
   即可，Performix 无需重新编译。Performix 未使用任何会阻止该替换的技术措施（无静态链接、
   无 IL 合并、无混淆）。
3. 如需重新编译 NvAPIWrapper，上游源码与构建脚本在
   <https://github.com/falahati/NvAPIWrapper>，按 LGPL-3.0 条款公开。
4. 若修改 NvAPIWrapper 源码，修改部分必须以 LGPL-3.0 公开。

## 3. Microsoft Windows App SDK 1.6.250205002（Microsoft 许可）

- 用途：WinUI 3 / Windows App Runtime（`Performix.App` 唯一的 UI 框架依赖）。
- 许可：Microsoft Software License Terms（NuGet 包内 `license.txt`）
- 许可原文：[third_party/Microsoft.WindowsAppSDK/license.txt](third_party/Microsoft.WindowsAppSDK/license.txt)
- 第三方声明：[third_party/Microsoft.WindowsAppSDK/NOTICE.txt](third_party/Microsoft.WindowsAppSDK/NOTICE.txt)
- 说明：`Performix.App` 以 `WindowsAppSDKSelfContained=true` 打包运行库到输出目录，
  属于官方支持的再分发方式，不修改任何 Microsoft 二进制。

### Segoe Fluent Icons / Segoe MDL2 Assets

- WinUI 界面通过 `FontFamily="Segoe Fluent Icons"`（回退 `Segoe MDL2 Assets`）引用系统字体
  绘制图标字形（`Performix.App/Glyphs.cs` 等）。
- **不随仓库分发该字体文件**。字体随 Windows 授权，仅在装机系统上通过字体族名引用；
  这是 Windows 应用的标准用法。若要在非 Windows 平台或未授权环境渲染，需自行取得授权。

## 4. CommunityToolkit.Mvvm 8.4.2（MIT）

- 用途：`Performix.App` / `Performix.Legacy.Wpf` 的 MVVM（源生成器）依赖。
- 许可：MIT。Copyright (c) .NET Foundation and Contributors。
- 上游：<https://github.com/CommunityToolkit/dotnet>

## 5. MSTest 4.0.2（MIT）

- 用途：`tests/Performix.Tests` 测试框架。
- 许可：MIT。Copyright (c) .NET Foundation and Contributors。
- 上游：<https://github.com/microsoft/testfx>

## 6. .NET 运行时与 BCL 包（MIT）

`System.Management` 10.0.2、`System.ServiceProcess.ServiceController` 10.0.0 等
`Microsoft.NETCore.App` / `Microsoft.WindowsDesktop.App` 组件均按 MIT 许可，
Copyright (c) .NET Foundation and Contributors。

## 7. Icons8 图标（Legacy WPF，待复核）

`src/Performix.Legacy.Wpf/Resources/icons8-*.png` 来自 Icons8 的免费图标集，
按 Icons8 免费许可使用（通常要求署名或链接回 Icons8）。
**待办**：如后续保留 WPF 旧界面素材，补齐署名/链接，或替换为原创图标。
当前它们只被 WPF 回滚版使用，不进入 WinUI 主程序。

## 8. 被刻意排除的第三方内容

| 内容 | 原因 |
| --- | --- |
| `GHelperFanCurve.cs`（疑似派生自 seerge/g-helper，GPL-3.0） | 未收录，待原创重写；见 [docs/LICENSING.md](docs/LICENSING.md) |
| `Performix.Legacy.Wpf/Resources/Font.otf`（ASUS ROG 字体） | 版权串声明未经 ASUS 许可不得再分发 |
| Acer Predator 爪形标志（`predex-logo.png` / `predex.ico` / `appicon.ico` / `predator-mark.png`） | Acer 商标，未获授权；改用生成的占位图 |
| `decompiled/`、`*_types.txt`、`*_strings_utf16.txt`、`reverse/**/decompiled-*` 等反编译产物 | 专有软件逆向物，不进入公开仓库 |
