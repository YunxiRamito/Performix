# Performix 架构

> 版本对应 v0.1.0-alpha.1（第一版：Acer Predator PH317-55 实测逆向）。
> 本文描述**现状**，不是理想态。标注「刻意不做」的地方是有意的取舍。

## 1. 定位

Performix 是「厂商性能控制中心的替代前端」：不修改固件、不注入驱动，走厂商自己的
WMI / EC / HID / 管道通道读写状态。产品目标见 [README](../README.md)。

设计约束（按优先级）：

1. **不碰固件语义**：协议常量、帧结构、cmd 号、EC 写入顺序保持逆向实测值。
2. **默认不装内核驱动**：`PREYSENSE_NO_DRIVER`（见 `Directory.Build.props`），
   驱动/SCM 路径编译为 fail-fast stub。
3. **可失败但不可乱写**：所有硬件写入先经过能力探测与 gate，失败要可回滚（`Restore-AutoFan.ps1`）。
4. **UI 可回滚**：WinUI 主程序与 WPF 旧界面同时存在于仓库，切换入口是 `edition.txt`。

## 2. 工程分层

```
┌───────────────────────────────────────────────────────────────┐
│ Performix.App (WinUI 3, AssemblyName=Performix)               │
│  Shell/  Pages/  Views/  Services/  Styles/                   │
│  + 链接编译 Legacy.Wpf 的 ViewModels/*.cs 与若干 Services      │
└───────────────┬───────────────────────────────────────────────┘
                │ ProjectReference
┌───────────────▼──────────────┐   ┌────────────────────────────┐
│ Performix.Overlay (WinForms) │   │ Performix.Legacy.Wpf       │
│  HUD 分层窗口 / OSD / 图表    │   │  WPF 旧界面（回滚/参考）    │
└───────────────┬──────────────┘   └─────────────┬──────────────┘
                │ ProjectReference               │
┌───────────────▼────────────────────────────────▼──────────────┐
│ Performix.Core                                                 │
│  Hardware/  Mode/  Rgb/  Gpu/  Display/  Battery/  Services/   │
│  Configuration/  Diagnostics/  Driver/  Logging/  Input/       │
└───────────────────────────────────────────────────────────────┘
                ▲
                │ 引用（只读）
        tests/Performix.Tests（MSTest）
        src/Performix.Probe（控制台自检）
```

### Performix.Core —— 全部硬件事实都在这里

| 子目录 | 职责 |
| --- | --- |
| `Hardware/` | Ene / Acer EC、WMI（`AcerWmi`）、PSSvc 命名管道、HID 设备发现、CPU telemetry、功耗限制控制器、模式应用事务 |
| `Mode/` | 性能模式模型（Eco/Quiet/Balanced/Performance/Beast）、风扇调度器与曲线插值、EC 风扇策略、Windows 电源策略、Intel RAPL |
| `Rgb/` | Acer HID / EC 灯效协议、OEM USB 键盘协议、静态/动态/软件灯效引擎 |
| `Gpu/` | NVAPI（NvAPIWrapper）封装、NVML、NvDDS 显示状态、时钟/功耗偏移 |
| `Display/` | 刷新率与显示模式 |
| `Configuration/` | `preysense.json` 配置模型 + 原子写入 + schema |
| `Services/` | 遥测采集/协调、硬件操作 gate、前台进程感知、内存监控 |
| `Driver/` | 能力说明与参数模型；**no-driver fork 下不实际使用驱动** |

Core 不引用任何 UI 程序集，可在无 GUI 情况下被 Probe 与测试直接驱动。

### Performix.App —— WinUI 3 外壳

- 单窗口托盘面板：`Shell/ShellWindow.xaml`（320 DIP 固定宽 + 自定义 40 DIP 标题栏）。
- 页面：Home / Tuning / Fans / Lighting / Overlay / Settings / Theme。
- **ViewModel 复用**：`Performix.App.csproj` 通过 `<Compile Include="..\Performix.Legacy.Wpf\ViewModels\*.cs" />`
  链接 WPF 版 ViewModel（同一份状态机，不复制），并用 `Compat/WpfShims.cs` 提供
  `Dispatcher` / `Application.Current` 等 WPF 兼容面。
- 与 WPF 的差异集中在壳层：`Services/AppearanceService`（Mica/Acrylic/强调色，
  状态写 `%LOCALAPPDATA%\Predex\appearance.json`）、`TrayIconService`（Win32 托盘）、
  `ShellUiState`、`AcerHotkeyWatcher`、`KeyboardHookService`。

### Performix.Legacy.Wpf —— 回滚路径

WPF 完整前端（`AssemblyName` 仍是 `Predex`，因为资源字典用
`pack://application:,,,/Predex;component/...` 寻址）。发布产物在 `dist\Predex.exe`，
用于 WinUI 版出问题时一键切回。除「保持可构建、可回滚」外不接受新功能。
细节见 [src/Performix.Legacy.Wpf/README.md](../src/Performix.Legacy.Wpf/README.md)。

### Performix.Overlay / Performix.Probe

- Overlay：WinForms 分层透明窗口 HUD，采样由 Core 的遥测协调器供给，只读。
- Probe：控制台自检（温度 / 转速 / 传感器来源链 / 风扇曲线 dry-run），
  **验收不需要启动 GUI**。

## 3. 数据流

```
EC / WMI / PSSvc pipe / NVAPI / NVML / ACPI
        │  轮询（默认 1 s；HUD 同一节拍）
        ▼
TelemetryCoordinator ──► TelemetrySnapshot（不可变 record）
        │                        │
        │                        ├─► Performix.Overlay（图表/HUD 文字）
        │                        └─► ViewModels（属性通知）─► WinUI / WPF 页面
        ▼
HardwareOperationGate（串行化高风险写：模式切换、灯效提交、风扇写入）
        ▼
ProfileManager / FanScheduler / 灯效引擎 ──► EC / HID / WMI 写入
```

写入路径的共同约定：

- 只经由 `Hardware/` 下的封装，UI 层不直接 P/Invoke。
- 每次写入前做能力探测；失败返回显式错误而不是静默吞掉。
- 模式切换是事务（`AcerProfileApplyTransaction`），失败按相反顺序回滚。
- 关闭程序/Ctrl-C 不负责恢复硬件 —— 由 `scripts\Restore-AutoFan.ps1` 显式归还风扇控制权。

## 4. 状态与配置

| 状态 | 位置 | 说明 |
| --- | --- | --- |
| 硬件配置 | `preysense.json`（exe 目录） | 原子写（临时文件 + replace），schema 见根目录 `preysense.schema.json` |
| 外观状态 | `%LOCALAPPDATA%\Predex\appearance.json` | 仅 WinUI 外壳 |
| 外壳 UI 状态 | `%LOCALAPPDATA%\Predex\shell-ui.json` | 窗口密度/最后页面等 |
| 默认入口 | `edition.txt`（仓库根） | `winui` / `wpf`，由 `Rollback-ToWpf.ps1` 改写 |
| 日志 | exe 目录 `*.log` | `*.log` 已忽略，不进仓库 |

> 文件名/目录名沿用 Predex / preysense 是兼容面（装机用户已有配置、计划任务、
> 单实例 mutex 都指向它们），不属于本轮改名范围。

## 5. 构建与测试策略

- 统一版本号在 `Directory.Build.props`；`Version` 带预发布后缀，`AssemblyVersion` /
  `FileVersion` 保持四段数字。
- 测试（`tests/Performix.Tests`）以**纯逻辑 + 源码扫描**为主：协议字节往返、调度器状态机、
  配置迁移、本地化表完整性、XAML 约定检查。硬件测试通过
  `PREYSENSE_RUN_HARDWARE_TESTS=1` 显式开启，默认跳过（基线 584 通过 / 31 跳过）。
- 源码扫描类测试会读仓库里的 `.xaml`/`.cs`（定位仓库根靠 `Performix.slnx` 标记文件）；
  调整目录结构时这些测试要同步改路径。
- WinUI 构建必须 `-p:Platform=x64`；`EnableMsixTooling=true` 负责产出 `resources.pri`
  （非打包 WinUI 应用缺它会启动即崩）。

## 6. 未来扩展（跨品牌）

**长期目标**：同一套 UI/调度能力支持 Acer / ASUS / Lenovo / MSI 等品牌。
**当前状态**：没有品牌抽象层 —— 这是刻意的。

### 为什么现在不抽象

第一版的实际输入只有一台 PH317-55。此时抽 `IBrandBackend`，接口形状必然是被
Acer 的事实「拟合」出来的，第二个品牌落地时大概率要推翻重写，同时把现有可用的
写入路径搞复杂。经验判断：**抽象要等第二个实现出现后再抽**，用两份实现逼出真正的接缝。

### 现在保留的可扩展点（已经存在的接缝）

- UI/ViewModel 与硬件访问已经分离：页面只跟 ViewModel 打交道，ViewModel 只跟
  `IHardwareService` 与 Core 的门面打交道（测试里用 `MockHardwareService` 完整替换硬件层）。
- 遥测是快照模型（`TelemetrySnapshot`），来源链（PSSvc 管道 → WMI → ACPI → NVML fallback）
  已经按「能力分级」组织，加品牌来源不需要改 UI。
- 配置模型（`preysense.json`）按能力分区而不是按品牌分区，风扇/灯效/功耗各自独立，
  品牌特有字段可以后加默认值而不破老配置。
- no-driver fork 把「特权写」集中到了极少数封装点，跨品牌时这些点就是需要替换的位置。

### 第二个品牌落地时的计划

1. 先写与 Acer 实现**并列**的新品牌实现（不复用 Acer 的类名/常量），保持各自独立可测。
2. 出现 3 处以上重复形状后，再抽最小的抽象：
   - `IHardwareBackend`（能力探测 + 遥测读取）
   - `IFanControlBackend` / `ILightingBackend` / `IPowerLimitBackend`（写路径）
   - 品牌适配层放在 `Core/Hardware/<Brand>/`，不放进 UI。
3. UI 侧只依赖抽象，品牌差异通过能力位（`SupportsX`）与本地化文案表达，不走 if(brand)。
4. 每个品牌必须有：Probe 可读的自检输出、硬件测试开关、恢复脚本路径。
5. 文档同步：`docs/FINDINGS-<MODEL>.md` 记录该机型的实测结论，禁止把 A 品牌的
   常量套到 B 品牌上「试出来」。

### 明确不做的扩展

- 不为了「将来可能支持」而在 Core 里加万能配置项或反射插件系统。
- 不把 Acer 专用协议常量泛化命名（那会掩盖事实，增加跨品牌时的误用风险）。
- 不在本轮改 `PreySense.*` 命名空间（会炸 XAML/测试，单独一轮做）。
