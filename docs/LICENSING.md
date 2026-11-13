# 许可边界（Licensing）

本文件只回答三件事：**本项目是什么许可、仓库里有什么许可内容、什么被刻意排除**。
授权全文与逐组件义务见仓库根目录的 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

## 1. 本项目：GPL-3.0（已采用）

> **结论：Performix 整仓采用 GNU GPL v3.0。**（`LICENSE` = 项目声明 + GPL-3.0 全文）
> 原先「推荐 MIT + 重写 GPL 派生文件」的方案**已被否决**：`GHelperFanCurve.cs` 的来源无法
> 诚实抹掉，重写成本高于接受 copyleft，且本项目本身是社区替代实现，GPL 与定位一致。

决定链条：

1. `app/Fork/PreySense.Core/Mode/GHelperFanCurve.cs` 文件头自认
   「Ported from seerge/g-helper」，且 8 点曲线默认表、16 字节布局、归一化规则与上游逐项相同
   → 属 GPL-3.0 衍生作品，无法以 MIT 分发。
2. GPL-3.0 是传染性许可：只要仓库里包含它，**整仓分发即受 GPL-3.0 约束**。
3. 该文件被其它模块大量引用（清单见 §5），删掉/重写都会破坏功能与测试基线
   （584 通过 / 31 跳过）。因此选择**收录并整仓 GPL-3.0**，同时保留来源标注。

具体落地：

- [LICENSE](../LICENSE)：顶部是 Performix 项目声明（`Copyright (C) 2026 Performix contributors`
  + GPL-3.0 标准 "This program is free software…" 段落），其后是**逐字节的 GPL-3.0 官方全文**
  （Copyright (C) 2007 Free Software Foundation）。
- 上游 PreySense 的 **MIT 版权与许可声明保留**：`LICENSE` 的「Upstream attribution」段 +
  [LICENSES/PreySense-MIT.txt](../LICENSES/PreySense-MIT.txt) 全文。GPL 允许并入 MIT 代码，
  但 MIT 要求保留原声明 —— 两者不冲突，MIT 声明不因换许可而删除。
- `GHelperFanCurve.cs` 头部的 `Ported from seerge/g-helper` 注释**保留原样**，作为来源证据。
- 反向义务：改成 GPL-3.0 后，**不得再把本项目以 MIT/闭源方式发布**；分发二进制时需提供
  对应源码（或书面 offer）。商用可以，但下游一改也必须开源。

## 2. 仓库内许可内容

| 内容 | 许可 | 原文位置 |
| --- | --- | --- |
| Performix 自身源码（源自 PreySense） | **GPL-3.0** | `LICENSE` |
| PreySense 上游代码（MIT 部分，声明保留） | MIT | `LICENSES/PreySense-MIT.txt` + `LICENSE` 声明段 |
| `GHelperFanCurve.cs`（派生自 seerge/g-helper） | **GPL-3.0** | `LICENSES/GHelper-GPL-3.0.txt` |
| NvAPIWrapper.Net（NuGet，LGPL-3.0，未修改） | LGPL-3.0 | `third_party/NvAPIWrapper/LICENSE.txt` |
| Microsoft Windows App SDK | Microsoft Software License Terms | `third_party/Microsoft.WindowsAppSDK/license.txt` + `NOTICE.txt` |
| CommunityToolkit.Mvvm / MSTest / .NET BCL | MIT | 包内随附（NuGet 还原） |

GPL-3.0 与 LGPL-3.0 兼容（GPL-3.0 §13 路径），组合分发合法；LGPL 义务的落地方式
（**可直接替换 DLL**）：`NvAPIWrapper.dll` 以独立程序集形式输出到程序目录，与 Performix
程序集之间只走公开 API；用官方或自编译的同版本 DLL 覆盖该文件即可生效，无需重新编译
Performix。Performix 不做静态链接、IL 合并或混淆，也不阻止该替换。

## 3. 明确排除的内容

### 3.1 不含 Acer 商标素材

旧仓库里所有 Predator 爪形标志（`app\branding\predex-logo.png`、`app\branding\predex.ico`、
`Assets\appicon.ico`、`Assets\predator-mark.png`、WPF 的 `predex.ico` / `appicon.svg`）
**都没有搬进来**。当前在用的 `src/Performix.App/Assets/appicon.ico`、
`src/Performix.App/Assets/performix-mark.png`、`src/Performix.Legacy.Wpf/appicon.ico`
与 `assets/brand/performix*` 都是**自绘的码表图标**：蓝色超椭圆底 + 白色码表弧与指针，
由 `assets/brand/build-icons.py` 从母版 `assets/brand/gauge-26b/performix-icon-4k.png` 生成，
与 Acer 视觉标识无关。`assets/branding/performix-logo.png`、`assets/branding/performix.ico`
是上一代的几何占位图（圆角方块 + 字母 P），只留作历史对照，构建不再引用。

「Predator」「Acer Predator Sense」「PSSvc」等字样仅用于**指代被替换的 OEM 产品/服务**（合理提及），
不代表任何背书或商标授权。**GPL-3.0 §7 明确不授予商标权**，改用 GPL 不改变这一条。

### 3.2 已收录 GPL 派生代码：`GHelperFanCurve.cs`

`src/Performix.Core/Mode/GHelperFanCurve.cs` **现在收录在仓库里**，是本次许可切换的核心：

- 来源：`seerge/g-helper`（GPL-3.0），对应 `app/AppConfig.cs:301-310`（hex 序列化）、
  `:323-357`（默认曲线表）、`app/AsusACPI.cs:723-765`（`FixFanCurve` 归一化）。
- 处理方式：内容一字未改，文件头 `Ported from seerge/g-helper` 注释保留；
  GPL-3.0 原文作为 `LICENSES/GHelper-GPL-3.0.txt` 随仓库分发。
- 后果：整仓按 GPL-3.0 分发（§1）。原先的「原创重写该文件」待办**已取消**。
- 引用点清单见 §5（保留，用于后续核对来源与签名一致性）。

### 3.3 不含专有字体 / 反编译产物

| 内容 | 原因 |
| --- | --- |
| `Performix.Legacy.Wpf/Resources/Font.otf`（ASUS ROG 字体） | 版权串：未经 ASUS 许可不得再分发（GPL 不豁免） |
| `decompiled/`、`_types.txt`、`_strings_utf16.txt`、`reverse/intel-oc/decompiled-xtu/` 等 | 专有软件逆向产物 |
| `ghelper-ref/`、`predator-sense-ref/`、`PreySense/` 等参考目录 | 第三方参考仓库，不随本仓库分发（只取许可原文进 `LICENSES/`） |

### 3.4 不含第三方参考工程

旧仓库里的 `ghelper-ref/`、`predator-sense-ref/`、`PreySense/`、`winappcli-ref/`、
`reverse-skill/`、`deepseek-stack/`、`win-dev-skills/` 等目录一律未搬入。

### 3.5 不确定项 / 待复核

| 项 | 状态 | 风险 |
| --- | --- | --- |
| `src/Performix.Legacy.Wpf/Resources/icons8-*.png` | Icons8 免费许可，署名/链接未补齐 | 低（仅 WPF 回滚版使用，不进 WinUI 主程序） |
| ASUS ROG 字体 `Font.otf` | 已排除，未收录 | 若未来要加，必须先取得 ASUS 许可 |
| Acer 商标图形 | 已排除，用占位图 | 若未来做正式品牌，须自行设计或取得授权 |
| 旧仓库其它未审计残留（逆向笔记里的片段） | 未搬入，但本轮未逐条复核 | 中（发布前需再跑一遍 grep 审计） |
| `LICENSES/GHelper-GPL-3.0.txt` 与 g-helper 当前版本是否同步 | 取旧仓库 `ghelper-ref/LICENSE`（675 行官方全文） | 低（GPL-3.0 正文自 2007 年起未变） |

## 4. 发布前检查清单

- [x] `LICENSE` 是 GPL-3.0 全文 + 项目声明，且 PreySense MIT 署名保留
- [x] `LICENSES/PreySense-MIT.txt`、`LICENSES/GHelper-GPL-3.0.txt` 存在
- [x] `GHelperFanCurve.cs` 收录，头部来源注释未删，`THIRD-PARTY-NOTICES.md` 写明派生关系
- [ ] `assets/branding/` 与 `src/*/Assets/` 里没有 OEM 商标图形（现役图标是自绘码表，见 `assets/brand/build-icons.py`）
- [ ] 没有 `decompiled/`、`*_strings*`、`*_types.txt` 之类的逆向产物
- [ ] 没有第三方字体、图标集的未授权副本（Segoe 字体只引用不打包）
- [ ] `third_party/` 下的许可原文与实际依赖版本一致
- [ ] 分发二进制时随附源码（或 GPL-3.0 §6 的书面 offer），并说明可替换 `NvAPIWrapper.dll`

## 5. `GHelperFanCurve` 引用点清单（保留，来源标注依赖）

> 生成方式：在仓库内搜索 `GHelperFanCurve` / `GHelperCurveFrom`（排除 `bin/`、`obj/`）。
> 该文件已收录，构建不再受阻；清单保留用于：改类型名/命名空间时（第二轮改名）一处不漏，
> 以及核对「派生范围只有这一个文件」。

**类型/成员使用**

| 文件 | 行 | 用法 |
| --- | --- | --- |
| `src/Performix.Core/Mode/ContinuousFanCurve.cs` | 15, 16, 67, 109 | 字段类型 `GHelperFanCurve`、`Interpolate` 参数 |
| `src/Performix.Core/Mode/FanCurveProfile.cs` | 202, 203, 221, 223–226 | `GHelperCurveFrom` / `Default` / `PointCount` / `FixTemperatures` / `FixDuties` |
| `src/Performix.App/Pages/FansPage.xaml.cs` | 468, 469 | `GHelperFanCurve.Default(_mode)` |
| `src/Performix.Legacy.Wpf/Views/FanCurveWindow.xaml.cs` | 94, 136, 173, 179, 180, 238, 248, 258, 259 | `PointCount` / 曲线绘制与解析 |
| `tests/Performix.Tests/ContinuousFanCurveTests.cs` | 9, 11, 20, 30, 33, 43, 52, 61, 70, 75, 77, 81, 85, 91, 98 | 测试直接构造 `GHelperFanCurve` |

**注释/命名引用（不阻塞构建，改名时一并处理）**

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
