# Performix

跨品牌笔记本性能调度替换工具（长期目标）。

把厂商那套「装一堆后台服务 + 一个又大又慢的控制中心」的做法替换掉：直接用厂商自己在用的
WMI / EC / HID / 管道通道读写风扇、功耗、灯效和显示模式，给用户一个轻量、可审计的替代前端。

> 当前第一版（v0.1.0-alpha.1）实现来自 **Acer Predator PH317-55** 的实测逆向。
> 代码里的 Acer 专用路径、协议常量、cmd 号都照原样保留 —— 它们是被验证过的硬件事实。
> 架构上要能扩展到其它品牌，见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) 的「未来扩展」。
> **现在刻意不做**品牌抽象层：只有第二个品牌真正落地时再抽，避免空抽象。

## 这是什么 / 不是什么

- 是：风扇曲线与 EC 策略、性能模式切换（CPU PL / GPU 功耗与频率偏移）、灯效、显示刷新率、
  托盘/浮窗遥测、开机恢复的**替代实现**。
- 不是：固件修改、驱动注入、超频工具、破解。当前是 **no-driver fork**
  （`PREYSENSE_NO_DRIVER`）：驱动/SCM 相关代码全部编成 fail-fast stub，不装服务、不发 IOCTL。
- 不做：打包安装器、自动更新（暂无 release feed）、任何需要联网的默认行为。

## 仓库结构

```
Performix/
├ Directory.Build.props        # 统一版本号 0.1.0-alpha.1 / AssemblyVersion 0.1.0.1 + no-driver 开关
├ Performix.slnx
├ src/
│  ├ Performix.Core/           # 硬件、配置、模式、灯效、遥测（无 UI）
│  ├ Performix.App/            # WinUI 3 主程序（AssemblyName = Performix）
│  ├ Performix.Legacy.Wpf/     # WPF 旧界面，仅作回滚与参考（AssemblyName 仍是 Predex）
│  ├ Performix.Overlay/        # 浮窗 HUD（WinForms 分层窗口）
│  └ Performix.Probe/          # 控制台自检（AssemblyName = PerformixProbe）
├ tests/Performix.Tests/       # MSTest
├ scripts/                     # 构建 / 启动 / 回滚 / 恢复脚本
├ docs/                        # 设计与逆向记录
├ assets/branding/             # 占位品牌素材（原创图标待补，见「待办」）
├ third_party/                 # 第三方许可原文
├ THIRD-PARTY-NOTICES.md
└ .github/workflows/build.yml
```

## 构建

工具链固定为 **.NET 10 SDK**（目标框架 `net10.0-windows` / `net10.0-windows10.0.22621.0`）。
本仓库**不提交**便携 SDK（约 818 MB）。两种用法二选一：

1. `PERFORMIX_DOTNET` 环境变量指向任意 `dotnet.exe`（推荐）：

   ```powershell
   $env:PERFORMIX_DOTNET = "D:\tools\dotnet-sdk\dotnet.exe"
   & $env:PERFORMIX_DOTNET --version
   ```

2. 把便携 SDK 放到仓库内的默认位置 `<repo>\tools\dotnet-sdk\dotnet.exe`。
   `tools/` 已在 `.gitignore` 里，不会被提交。`scripts\*.ps1` 会先读 `PERFORMIX_DOTNET`，
   没设再用这个默认路径。

```powershell
# 全部项目
& $env:PERFORMIX_DOTNET build Performix.slnx -c Release

# 测试（基线：584 通过 / 31 跳过 / 0 失败）
& $env:PERFORMIX_DOTNET test tests\Performix.Tests\Performix.Tests.csproj -c Release

# WinUI 主程序（必须 x64 平台）
& $env:PERFORMIX_DOTNET build src\Performix.App\Performix.App.csproj -p:Platform=x64 -nodeReuse:false
```

发布（产物落在被忽略的 `dist\` / `dist-winui\`）：

```powershell
pwsh -File scripts\build-fork.ps1        # WPF 回滚版 + Probe 自检工具 -> dist\
pwsh -File scripts\build-winui.ps1       # WinUI 主程序 -> dist-winui\
pwsh -File scripts\Run-GUI.ps1           # 启动（默认入口；-Edition wpf 走回滚版）
```

> CI（`.github/workflows/build.yml`）在 `windows-latest` 上装 .NET 10 SDK，构建
> Core / Overlay / Probe / Tests / App（含 Legacy.Wpf 依赖），不跑任何硬件测试。

## 产品名与程序集

| 项目 | 目录 | AssemblyName | 说明 |
| --- | --- | --- | --- |
| Performix.App | `src/Performix.App` | `Performix` | WinUI 3 主程序 |
| Performix.Probe | `src/Performix.Probe` | `PerformixProbe` | 控制台自检 |
| Performix.Legacy.Wpf | `src/Performix.Legacy.Wpf` | `Predex` | WPF 旧界面，**仅作回滚与参考**；Pack URI 依赖这个程序集名，不改 |
| Performix.Core / Overlay / Tests | `src/…`、`tests/…` | 同项目名 | 库与测试 |

## 已知技术债

1. **C# 命名空间仍是 `PreySense.*`**。这一轮只改项目名/目录名/程序集名和界面文案；
   改命名空间会同时炸掉 XAML `x:Class`、`clr-namespace`、资源字典 Pack URI 和全部测试，
   要单独一轮统一做。
2. **内部标识与运行期状态仍是 Predex / preysense**：单实例 mutex、Named Event、
   `%LOCALAPPDATA%\Predex\appearance.json` / `shell-ui.json`、配置文件名 `preysense.json`、
   计划任务名、驱动/服务名 `PreySenseDriver`。这些是兼容面，不在改名范围内；
   界面文案已经全部是 Performix。
3. **品牌图标是占位素材**：`assets/branding/performix-logo.png` / `performix.ico` 是脚本生成的
   纯几何占位图（圆角方块 + 字母 P），不是 OEM 商标图形。等原创图标。
4. `docs/` 下的文档写于旧仓库，里面出现的 `app\Fork\...`、`app\experiments\Predex.WinUI\...`
   是历史路径，未逐篇改写。
5. **构建目前被许可审计挡住**：`Performix.Core\Mode\GHelperFanCurve.cs` 判定为疑似派生自
   GPL-3.0 的 [seerge/g-helper](https://github.com/seerge/g-helper)，本轮**未收录**，
   需原创重写后才能重新构建。影响面与引用清单见 [docs/LICENSING.md](docs/LICENSING.md)。

## 待办

- [ ] 原创品牌图标（替换 `assets/branding/` 与 `src/Performix.App/Assets/` 里的占位图）
- [ ] 原创实现替换 `GHelperFanCurve.cs`（8 点风扇曲线模型 + 归一化 + 默认表），恢复构建
- [ ] 第二轮：`PreySense.*` 命名空间统一改名
- [ ] 刷新 `docs/` 里的历史路径引用

## 文档

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) —— 分层、数据流、约束、未来扩展
- [docs/LICENSING.md](docs/LICENSING.md) —— 许可边界与未收录内容
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) —— 第三方组件与许可原文
- 其余 `docs/*.md`：UI 设计规则、页面树、WinUI 迁移、按键事件来源、更新设计、
  设置信息架构、用户决策、R3 接管、PH317-55 逆向发现

## 许可

项目本体 MIT（见 [LICENSE](LICENSE)）：上游 PreySense 的授权行保留，另加 Performix contributors。
本项目**不包含** Acer 商标图形，也**不包含** GPL 派生代码；第三方组件见
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
