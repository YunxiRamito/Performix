# 许可边界（Licensing）

本文件只回答三件事：**本项目是什么许可、仓库里有什么许可内容、什么被刻意排除**。
授权全文与逐组件义务见仓库根目录的 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

## 1. 本项目：MIT

Performix 本体按 MIT 发布（[LICENSE](../LICENSE)）。上游 PreySense 的版权行保留，
另加 Performix contributors。允许商用、闭源再分发、修改，保留版权与许可声明即可。

## 2. 仓库内许可内容

| 内容 | 许可 | 原文位置 |
| --- | --- | --- |
| Performix 自身源码（源自 PreySense） | MIT | `LICENSE` |
| NvAPIWrapper.Net（NuGet，LGPL-3.0，未修改） | LGPL-3.0 | `third_party/NvAPIWrapper/LICENSE.txt` |
| Microsoft Windows App SDK | Microsoft Software License Terms | `third_party/Microsoft.WindowsAppSDK/license.txt` + `NOTICE.txt` |
| CommunityToolkit.Mvvm / MSTest / .NET BCL | MIT | 包内随附（NuGet 还原） |

LGPL 义务的落地方式（**可直接替换 DLL**）：`NvAPIWrapper.dll` 以独立程序集形式输出到程序目录，
与 Performix 程序集之间只走公开 API；用官方或自编译的同版本 DLL 覆盖该文件即可生效，
无需重新编译 Performix。Performix 不做静态链接、IL 合并或混淆，也不阻止该替换。

## 3. 明确排除的内容

### 3.1 不含 Acer 商标素材

旧仓库里所有 Predator 爪形标志（`app\branding\predex-logo.png`、`app\branding\predex.ico`、
`Assets\appicon.ico`、`Assets\predator-mark.png`、WPF 的 `predex.ico` / `appicon.svg`）
**都没有搬进来**。当前 `assets/branding/performix-logo.png`、`assets/branding/performix.ico`、
`src/Performix.App/Assets/appicon.ico`、`src/Performix.App/Assets/performix-mark.png` 是
脚本生成的几何占位图（圆角方块 + 字母 P），与 Acer 视觉标识无关。
替换为原创图标前，不得把它们当成正式品牌素材发布。

「Predator」「Acer Predator Sense」「PSSvc」等字样仅用于**指代被替换的 OEM 产品/服务**（合理提及），
不代表任何背书或商标授权。

### 3.2 不含 GPL 派生代码：`GHelperFanCurve.cs` 未收录

`app\Fork\PreySense.Core\Mode\GHelperFanCurve.cs` 的文件头自认
「Ported from seerge/g-helper（app/AppConfig.cs、app/AsusACPI.cs）」，且 8 点曲线默认表与
上游逐项相同，审计判定为疑似 GPL-3.0 衍生作品。**本仓库不收录该文件**（也无任何替代实现）。

影响：`Performix.Core` 中 `ContinuousFanCurve` / `FanCurveProfile` /
`Performix.App\Pages\FansPage.xaml.cs` / `Performix.Legacy.Wpf\Views\FanCurveWindow.xaml.cs` /
`tests\Performix.Tests\ContinuousFanCurveTests.cs` 等仍引用该类型，**在原创重写落地前构建不通过**
（删掉该文件后 `dotnet build Performix.slnx -c Release` 在 Core 报 5 个 `CS0246`
「未能找到类型 GHelperFanCurve」，其余项目因依赖 Core 无法继续）。
引用点清单见本文 §5。重写要求：独立设计数据结构与默认表来源（默认表需能追溯到实测数据或
厂商公开资料），不得逐行翻译上游实现。

### 3.3 不含专有字体 / 反编译产物

| 内容 | 原因 |
| --- | --- |
| `Performix.Legacy.Wpf/Resources/Font.otf`（ASUS ROG 字体） | 版权串：未经 ASUS 许可不得再分发 |
| `decompiled/`、`_types.txt`、`_strings_utf16.txt`、`reverse/intel-oc/decompiled-xtu/` 等 | 专有软件逆向产物 |
| `ghelper-ref/`、`predator-sense-ref/`、`PreySense/` 等参考目录 | 第三方参考仓库，不随本仓库分发 |

### 3.4 不含第三方参考工程

旧仓库里的 `ghelper-ref/`、`predator-sense-ref/`、`PreySense/`、`winappcli-ref/`、
`reverse-skill/`、`deepseek-stack/`、`win-dev-skills/` 等目录一律未搬入。

## 4. 发布前检查清单

- [ ] `assets/branding/` 与 `src/*/Assets/` 里没有 OEM 商标图形（当前是占位图）
- [ ] 没有 GPL/AGPL 派生文件（当前 `GHelperFanCurve.cs` 已排除，重写后再加）
- [ ] 没有 `decompiled/`、`*_strings*`、`*_types.txt` 之类的逆向产物
- [ ] 没有第三方字体、图标集的未授权副本
- [ ] `third_party/` 下的许可原文与实际依赖版本一致

## 5. `GHelperFanCurve` 引用点清单（待重写后清零）

> 生成方式：在新仓库内搜索 `GHelperFanCurve` / `GHelperCurveFrom`（排除 `bin/`、`obj/`）。
> 这些是「删掉该文件后」编译受阻的调用点；注释里提到 GHelper 的地方（如
> `Performix.Core\Mode\FanScheduler.cs`、`Configuration\PreySenseConfig.cs`）附在后面供重写时参考。

**类型/成员使用（构建阻塞）**

| 文件 | 行 | 用法 |
| --- | --- | --- |
| `src/Performix.Core/Mode/ContinuousFanCurve.cs` | 15, 16, 67, 109 | 字段类型 `GHelperFanCurve`、`Interpolate` 参数 |
| `src/Performix.Core/Mode/FanCurveProfile.cs` | 202, 203, 221, 223–226 | `GHelperCurveFrom` / `Default` / `PointCount` / `FixTemperatures` / `FixDuties` |
| `src/Performix.App/Pages/FansPage.xaml.cs` | 468, 469 | `GHelperFanCurve.Default(_mode)` |
| `src/Performix.Legacy.Wpf/Views/FanCurveWindow.xaml.cs` | 94, 136, 173, 179, 180, 238, 248, 258, 259 | `PointCount` / 曲线绘制与解析 |
| `tests/Performix.Tests/ContinuousFanCurveTests.cs` | 9, 11, 20, 30, 33, 43, 52, 61, 70, 75, 77, 81, 85, 91, 98 | 测试直接构造 `GHelperFanCurve` |

**注释/命名引用（不阻塞构建，重写时一并改名）**

| 文件 | 行 |
| --- | --- |
| `src/Performix.App/Controls/FanCurveCanvas.cs` | 14, 486, 546, 608 |
| `src/Performix.App/Pages/FansPage.xaml.cs` | 7, 218, 467 |
| `src/Performix.Core/Configuration/PreySenseConfig.cs` | 390, 412 |
| `src/Performix.Core/Mode/ContinuousFanCurve.cs` | 9, 66 |
| `src/Performix.Core/Mode/FanCurveProfile.cs` | 220 |
| `src/Performix.Core/Mode/FanScheduler.cs` | 6 |
| `src/Performix.Legacy.Wpf/ViewModels/MainViewModel.Fans.cs` | 211 |
| `src/Performix.Legacy.Wpf/Views/FanCurveWindow.xaml.cs` | 121, 246, 262 |
| `tests/Performix.Tests/ContinuousFanCurveTests.cs` | 5, 51, 60, 75, 91 |
| `tests/Performix.Tests/FanSchedulerTests.cs` | 6 |
