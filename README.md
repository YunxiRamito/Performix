# Performix

**License: GPL-3.0** —— 整仓以 [GNU GPL v3.0](LICENSE) 发布（含 GPL 派生代码，见
[docs/LICENSING.md](docs/LICENSING.md) §2；上游 PreySense 的 MIT 声明保留）。

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

## 机型适配

> 架构决策见 [docs/adr/0001-machine-provider.md](docs/adr/0001-machine-provider.md)（v2：**单一特权宿主** +
> **能力三态** + **机型数据驱动**）；设置里的服务页 v1 只做**只读检测与竞争提示**，见
> [docs/design/SERVICE-PAGE.md](docs/design/SERVICE-PAGE.md)。
> 这一节取代 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) §6「现在刻意不做品牌抽象层」的旧结论：
> 方向已改为「先适配 GHelper（ASUS 系）与 PreySense（Acer 系）已经支持过的机型，且机型之间强隔离」。

**为什么要按机型隔离：**

- **协议是底盘事实，不是品牌事实。** PH317-55 的 `0x410009` / `0x820009` / `0xC30009` 是这一台
  机器上 EC 接受的风扇行为字；同品牌另一代底盘连 WMI 方法签名都会换（`gmOutput` 是 UInt32 还是
  UInt64 都不一样，见 `AcerEcLightingDevice` 的签名矩阵）。把 A 机型的常量套到 B 机型上「试出来」，
  代价是一次不可回读的 EC 写。
- **隔离 = 接口族 + 机型数据。** provider 按**接口族**分（`Providers/AcerGamingWmi/`、
  `Providers/AsusAtk/`、`Providers/Generic/`）；同族机型的差异写**数据文件**
  （`data/machines/<brand>/<model>.yaml`），社区只提交数据，不改代码。品牌常量只准留在自己的目录。
- **UI 只问能力，不问品牌。** 能力没有 bool，只有三态 `Declared → Probed → Verified`；
  页面 / ViewModel 里出现 `if (brand == ...)` 即为缺陷（架构测试兜底，ADR §D2）。
  **没声明 → 隐藏；声明了但当前不可用 → 禁用 + 原因。**
- **特权边界只有宿主。** UI / HUD / 托盘以普通用户运行，硬件只由**单一特权宿主**经带 ACL 的
  命名管道操作；宿主是唯一写者，负责能力探测、写仲裁、失效保护（ADR D3-D5）。
- **探测不到 / 没验证就降级，不猜。** 识别串不匹配、方法签名不对、回读失败、检测到直接 EC I/O
  的工具 → 能力降为只读并告诉用户原因，不崩、也不硬写。

### 品牌-机型适配表

| 品牌 | 系列 / 机型 | 识别方式（SMBIOS / PCI / BIOS 串） | 模式 | 风扇 | 键盘灯 | GPU 超频 | MUX | 刷新率 | 依赖服务 | 状态 | 信任等级 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Acer | Predator Helios 300 · **PH317-55**（2021，RAV4_TLS，BIOS V1.14，i9-11900H + RTX 3070 Laptop） | `SMBIOS Type 172`（Acer 格式化字节，Major/Minor）+ `root\wmi:AcerGamingFunction`（实例 `ACPI\PNP0C14\APGe_0`） | ✅ 5 档 · 管道 cmd 32/33 · selector `0x0B` · 字节 `0x06/0x00/0x01/0x04/0x05` · cmd 33 回读确认 | ✅ 占空比 % · WMI `SetGamingFanSpeed`（`duty<<8 \| fanId`，1=CPU / 4=GPU）· 行为字 `0x410009` / `0x820009` / `0xC30009` · RPM 读回走管道 cmd 15（WMI 回读的是上次下发值） | ⚠ EC 直驱 4 区（type172 声明；UInt32 色 + `SetGamingLEDBehavior` + `GetGamingRgbKb` 回读）· **写回未验收** | ⚠ NVAPI 时钟偏移已实现（RTX 3070 Laptop）· **真机写入未验收** | ❌ 无（`ActiveGpuMuxMode = -1`，DDS 不可用） | ✅ Windows 显示 API（`DisplayManager` 枚举 / 切换），非 OEM 通道 | `PSSvc`（DisplayName `Predator Service`，Manual，必须 Running）· 官方 UI/agent 建议关，**服务不要停** | ✅ 已验证（本机） | `T2 维护者实机` |
| Acer | Predator 2021 同代其它型号（PH315-55 / PHN16-71 等，**具体型号未核实**） | 待核实（同 `AcerGamingFunction` 家族，type172 字节 / 方法签名可能不同） | 待核实 | 待核实 | 待核实 | 待核实 | 待核实 | 待核实 | 待核实（`PSSvc` 是否同源未核实） | 社区待验证（未在本项目实测） | `未验证` |
| Acer | Nitro 系列（AN515 / AN517 / AN16 等，**具体型号未核实**） | 待核实 | 待核实 | 待核实 | 待核实 | 待核实 | 待核实 | 待核实 | 待核实 | 社区待验证（未在本项目实测） | `未验证` |
| ASUS | ROG Zephyrus（G14 / G15 / G16 / G17 等） | 待核实（上游 GHelper 走 `\\.\ATKACPI` + ASUS ACPI-WMI，本项目未验证） | 待核实 | 待核实 | 待核实 | 待核实 | 待核实 | 待核实 | 待核实（Armoury Crate 相关服务需逐个核实） | **未在本项目实测，待验证** | `未验证` |
| ASUS | ROG Strix（G15 / G16 / G17 · Scar / Hero） | 待核实 | 待核实 | 待核实 | 待核实 | 待核实 | 待核实 | 待核实 | 待核实 | **未在本项目实测，待验证** | `未验证` |
| ASUS | TUF Gaming（A15 / A16 / F15 / F16 等） | 待核实 | 待核实 | 待核实 | 待核实 | 待核实 | 待核实 | 待核实 | 待核实 | **未在本项目实测，待验证** | `未验证` |
| ASUS | TUF Gaming · **A16 FA607PI**（Ryzen 9 7940HX + RTX 4070 Laptop，BIOS `FA607PI.311`） | BIOS 串 `FA607PI.311` + `root\wmi:AsusAtkWmi_WMNB`（实例 `ACPI\PNP0C14\ATK_0`，18 方法全 Implemented）+ 宿主驱动 `ASUS System Control Interface v3` 3.1.72.0 · SMBIOS 厂商 / 产品串未采集 | 未验证 · `DEVS` 写路径存在（ACPI 三参数 `WMNB(id,arg1,arg2)`，DSDT 命中 `0x0A1EC`）· 档位字节 / 回读未取得 · `powercfg` 只剩「平衡」 | 未验证 · 标准 WMI 无风扇（`Win32_Fan` / `CIM_Fan` 空）· 唯一热区 `TZ01` = 3552（≈82.1 °C）· 转速需 `DSTS` 或 `AsusHWMonitorWMI`（后者访问被拒） | ❓ 未探测（本次没采集；WMNB 18 个方法名无灯效关键字 ≠ 不支持） | 未验证 · NVAPI 路径（RTX 4070 Laptop，项目既有能力） | ❓ 未知（未探测） | 只读已探测 · 实际 2560×1600@165Hz（走 DisplayID）· `WmiMonitorListedSupportedSourceModes` 只报 60Hz → 禁用 EDID 枚举 · 切换未验证 | `ASUSOptimization` / `AsusCertService` 均 Disabled（不影响类存在）· 宿主驱动 `ASUS System Control Interface v3` 运行中 · `ArmourySocketServer` Disabled · G-Helper 两个计划任务在（见下句） | 只读探测完成（远程真机）· 写入未验证 | `T0 单机只读探测` |

该行数据来自远程只读探测（2026-11-13，ASUS TUF Gaming A16 FA607PI），写入行为未验证；数据源 `data/machines/asus-tuf-a16-fa607pi.yaml`，接口证据见 [docs/design/ASUS-ATK-FAMILY.md](docs/design/ASUS-ATK-FAMILY.md)。

**读表规则（改表前先读这几条）：**

- **这张表由数据文件生成。** 源头是 `data/machines/<brand>/<model>.yaml`，生成器
  `scripts\generate-machine-table.ps1` 待实现；在它实现之前，手工表必须与数据文件**逐项一致**，
  PR 改表必须同时改数据文件。手工只改 README = 错误。
- **「已验证」= 本仓库在这台机器上闭环过，证据可查**：`docs/FINDINGS-PH317-55.md`
  （协议 / 传感器 / 端到端）、`PerformixProbe` 的脱敏报告、对应单元测试。
- **「待验证」= 只有上游支持或社区报告，本项目没有实测。** 格子里写「待核实」= 连上游有没有做
  都还没核对，不是「上游说支持」的意思。
- **信任等级**：`T2 维护者实机` / `T1 ≥3 台独立社区验证` / `T0 社区单台脱敏报告` / `未验证`。
  只有 **T2** 才能把「写」能力标成 `Verified`；T0/T1 最多 `Probed`（可读，不自动写）。
- **⚠ 表示代码已实现、真机闭环记录缺失**（写回 / 读回没有归档）。不许因为代码在就把它当已验证。
- **没有匹配到 provider 的机器走 `Generic`**：它只提供 **GPU 传感器（NVML）+ Windows 电源模式
  （含 overlay scheme）**，不碰 OEM 写通道。`Generic` 不是「通用只读方案」：ACPI 热区很多机型是
  假值（PH317-55 的 TZ00 恒 60.1 °C），`powercfg` 在现代机型上常常只剩「平衡」。
  它不算「已适配」，也不进这张表。
- **型号没核实的只写系列，不编型号。** ROG Flow / ROG Ally 等细分系列未单列：范围以上游
  GHelper 支持列表为准，本项目未核实。
- **服务页 v1 只读**：它只负责检测与提示「谁在抢硬件」，不启停服务、不改启动类型
  （见 [docs/design/SERVICE-PAGE.md](docs/design/SERVICE-PAGE.md)）。

## 适配一款新机型

**工作流（顺序别跳）：**

1. **只读采集。** 在目标机上跑 `PerformixProbe`（见下），拿到传感器来源链、管道 / WMI 连通性、
   GPU 能力。这一步不写硬件。
2. **填模板。** 用下面的提示词让 AI 把能查的查出来、查不到的标「待核实」，产出**能直接映射到
   数据文件字段**的结构化答案。
3. **自己先脱敏。** 报告里不能有：MSDM（OEM 产品密钥）、SLIC、SMBIOS Type 1/2/3 的序列号与 UUID、
   MAC、用户名路径、主机名。只保留机型识别需要的厂商/产品串与能力证据。
4. **附证据 + 引导式行为验证。** 证据包括 `root\WMI` 的 MOF / 方法签名、ACPI DSDT/SSDT 表、
   逐模式切换回读记录、测试曲线 60 s 的转速/温度时间序列（测完自动恢复）。截图不算证据。
5. **提交「数据」PR，不是代码 PR。** 目标文件 `data/machines/<brand>/<model>.yaml`
   （接口族 + 机型差异 + 每项能力的状态/等级/证据 + 信任等级声明）；`data/` 目录本身待建。
   **不改代码**。
6. **维护者复核并定信任等级**：`T2 维护者实机` / `T1 ≥3 台独立验证` / `T0 社区单台` / `未验证`。
   只有 T2 才能把「写」能力标 `Verified`；README 表**由数据文件生成**（生成器待实现，在此之前手工表
   必须与数据一致），不手改。

**`--probe` 工作流（能力矩阵 JSON → 贴 issue）：**

```powershell
# 1) 只读自检：传感器来源链 + 管道/WMI 连通性；报告默认写到 %ProgramData%\PreySense\logs\
PerformixProbe.exe

# 2) 报告路径显式指定，方便改名后附到 issue
PerformixProbe.exe D:\probe-PH317-55.txt

# 3) 只读 GPU 能力：NVAPI 可编辑的时钟范围（不写）
PerformixProbe.exe --gpuoc
```

```powershell
# 写探针：只在“确认可回滚 + 知道当前值”时才跑，会真写 EC / WMI
PerformixProbe.exe --set-mode <目标hex> <当前hex>   # 管道 cmd 32 写 + cmd 33 回读 + 恢复原值
PerformixProbe.exe --rgb-test                        # ≤3 次 EC 写，自己恢复
PerformixProbe.exe --rgb-reset                       # 恢复 OEM 默认灯效（5 次 EC 写）
```

> 计划中的 `--probe --json` 会把上面这些合成一份**已脱敏**的能力矩阵 JSON（字段草案见 ADR 附录 B），
> 走宿主命令 `ProbeExport`；**该功能尚未实现**，实现前按上面命令收集，并**按下面的脱敏清单自己删**。
>
> **脱敏清单（导出前必须处理）：** MSDM（OEM 产品密钥）、SLIC、SMBIOS Type 1/2/3 的序列号与 UUID、
> MAC 地址、用户名路径（`C:\Users\<name>\…`）、主机名、任何账号/令牌。只保留厂商/产品串、
> 方法签名、能力矩阵与时间序列。

**给 AI 的填空模板（复制整段，把机型信息填进第一行）：**

```text
你是笔记本 OEM 硬件协议分析助手。目标机型：<品牌 型号 / 年份 / BIOS 版本>，OEM 应用：<名称 版本>。
请回答下面 18 个问题，最后把它们压缩成一份「机型数据文件草案」（见 Q17）。**每一项都要给证据**（WMI 类名与方法签名、命名管道名、HID VID/PID、
反编译符号名、实测日志行号、上游代码文件与 commit 都可以）。

铁律：
1) 不确定就写「待核实」，禁止猜测、禁止用同品牌其它型号的常量代替。
2) 区分「上游/社区说支持」和「本机实测通过」——两者必须分开写。
3) 任何写操作都要写「怎么写、怎么回读、失败/退出怎么回滚」；没有回读手段的写入要显式标注。
4) 不要输出从我方代码里抄来的结论冒充实测；引用来源要能定位。
5) 答案要能直接映射到数据文件字段（接口族、模式 id/语义、风扇 id/单位、能力状态与等级），
   不要只给散文式描述。
6) 报告里不要出现序列号、UUID、MAC、OEM 产品密钥、用户名路径、主机名；先脱敏再提交。

问题：
Q1  机型识别串：SMBIOS Type 1/Type 2（Manufacturer / Product / Version）、基板型号、BIOS 版本、
    是否有厂商自定义 SMBIOS 表（如 Acer Type 172）？分别的原始字节/字符串是什么？
Q2  OEM 应用走哪个硬件通道：ACPI-WMI（命名空间 + 类名 + 方法名与参数类型）、命名管道（路径 +
    帧格式）、HID（VID/PID/报告）、还是驱动 IOCTL？
Q3  性能模式：支持几档、每档的原始字节值、写入通道与方法、读回方式与读回值？是否要管理员？
Q4  风扇控制：写占空比还是 PWM 还是绝对转速？单位与范围？fanId 编号约定（哪个是 CPU / GPU）？
    每个风扇能否独立？写后能否回读真实转速（RPM），还是只回读上次下发值？
Q5  EC 是否有转速地板 / 最小占空比？低于地板的写入会被钳制还是忽略？高负载下 EC 会不会自己接管？
Q6  风扇曲线：点数、温度轴与占空比轴的取值范围、进入自定义模式需要先写什么、升降斜率 / 迟滞 /
    是否必须先关 OEM 服务？
Q7  RGB / 键盘灯：EC 分区直驱、HID 单键、还是软件合成？分区数 / 灯珠数、颜色字节序（RGB / BGR）、
    亮度与速度、动态效果是否硬件执行、能否回读、灯条 / Logo / 屏盖灯是否独立？
Q8  MUX / DDS：有没有？读取当前模式的接口、切换接口、是否需要重启、能否读回、失败表现？
Q9  刷新率：走 OEM 通道还是 Windows 显示 API？支持档位列表怎么拿？电池 / 插电是否不同策略？
Q10 CPU 功耗限制（PL1/PL2/Tau）与欠压：是否必须 MSR 驱动？不装驱动时还能做什么？
Q11 GPU 厂商与超频：NVIDIA / AMD / Intel？超频通道（NVAPI / ADL / 其它）？可编辑范围与步进？
    需要什么 DLL / 运行库？失败如何回滚？
Q12 依赖服务与进程清单：服务名 / 显示名 / 启动类型 / 它提供什么功能 / 停掉后哪些功能失效 /
    我们能否替代 / 替代用什么通道？
Q13 写入安全等级：逐项给出 WriteWithReadback（能回读校验）/ ReadOnly（不支持写）/ NoWrite
    （能写但风险未定、需要显式开关）的判断与理由。
Q14 回滚手段：恢复到哪个原始值、用什么命令或脚本、是否需要重启、退出 / 崩溃时怎么保证归还？
Q15 已知失败案例：社区报告的不兼容、蓝屏、EC 卡死、服务停用后遗症，以及触发条件。
Q16 来源与许可：结论来自哪些上游仓库 / 文档 / 帖子？分别是什么许可（GPL / MIT / 专有）？
    有没有不能搬的素材（字体、图标、厂商商标、逆向产物）？
Q17 接口族与数据文件：这台机器属于哪个接口族（Acer Gaming WMI / ASUS ATK / 其它）？
    输出一份 `data/machines/<brand>/<model>.yaml` 草案：识别串、模式列表（id + 语义标签
    quiet/balanced/max + 显示名 key）、风扇 id/单位/是否有转速地板、RGB 类型与分区、
    MUX、刷新率策略、依赖服务清单、每项能力的 `state`（Declared/Probed/Verified）与
    `safety`（ReadOnly/WriteWithReadback/NoWrite）、证据引用。
Q18 证据与脱敏：能提供哪些可归档证据（MOF / 方法签名、ACPI DSDT/SSDT、逐模式回读记录、
    60 s 转速温度时间序列）？报告里哪些字段涉及序列号 / UUID / MAC / 密钥 / 用户名路径 / 主机名，
    需要先脱敏？
```

**issue / PR 里至少要有这些字段：**

```text
机型 / 年份 / BIOS / EC 版本：
接口族（Acer Gaming WMI / ASUS ATK / 其它）：
识别串（SMBIOS 原始值；已去掉序列号 / UUID）：
探针报告（附件：sensor-probe-*.txt 或 --probe --json，已脱敏）：
数据文件草案（data/machines/<brand>/<model>.yaml；没有就贴能力矩阵 JSON）：
模式 / 风扇 / 灯效 / MUX / 刷新率：分别写「实测通过 / 未验证 / 不支持」，附证据
依赖服务清单：服务名 · 显示名 · 启动类型 · 它提供什么 · 停用影响
写入安全等级：每项一个等级（ReadOnly / WriteWithReadback / NoWrite）+ 回读强度 + 回滚手段
引导式行为验证：逐模式回读记录 + 60 s 转速 / 温度时间序列（测完已恢复原状）
证据清单：MOF / 方法签名、ACPI DSDT/SSDT（脱敏后）
信任等级声明：T0 社区单台 / T1 ≥3 台独立 / 请维护者复测
脱敏确认：已移除序列号 / UUID / MAC / OEM 产品密钥 / 用户名路径 / 主机名
许可来源：参考了哪些仓库、什么许可
```

**不许做的事：** 拿 A 品牌的常量去 B 品牌试写；在没有回读的机器上批量写；把「上游支持过」
当成「本机已验证」；把 T0/T1 写成 T2；没真机验收就把表里的状态改掉；只改 README 不改数据文件；
提交未脱敏报告；把「停 OEM 服务」当成优化（服务页 v1 只读；`PSSvc` 就是反面案例）。

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
├ LICENSES/                    # 上游许可原文（PreySense MIT / GHelper GPL-3.0）
├ third_party/                 # 运行时依赖的许可原文
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
5. **命名空间仍是 `PreySense.*`**，`GHelperFanCurve` 这个类型名也保持上游原名（方便对照
   seerge/g-helper 的来源）；许可切换不涉及改名，统一改名时按
   [docs/LICENSING.md](docs/LICENSING.md) §5 的引用点清单一处不漏。

## 待办

- [ ] 原创品牌图标（替换 `assets/branding/` 与 `src/Performix.App/Assets/` 里的占位图）
- [x] 收录 `GHelperFanCurve.cs`（来源注释保留），整仓改为 GPL-3.0 分发
- [ ] 第二轮：`PreySense.*` 命名空间统一改名
- [ ] 刷新 `docs/` 里的历史路径引用

## 文档

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) —— 分层、数据流、约束、未来扩展
- [docs/LICENSING.md](docs/LICENSING.md) —— 许可边界与未收录内容
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) —— 第三方组件与许可原文
- 其余 `docs/*.md`：UI 设计规则、页面树、WinUI 迁移、按键事件来源、更新设计、
  设置信息架构、用户决策、R3 接管、PH317-55 逆向发现

## 许可

项目本体 **GPL-3.0**（见 [LICENSE](LICENSE)）：含派生自 seerge/g-helper 的
`GHelperFanCurve.cs`，因此整仓按 GPL-3.0 分发；上游 PreySense 的 MIT 授权声明保留
（[LICENSES/PreySense-MIT.txt](LICENSES/PreySense-MIT.txt)）。
本项目**不包含** Acer 商标图形；商标、ASUS 字体、NvAPIWrapper 的 LGPL 等 GPL 之外的约束，
逐条落在 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 与
[docs/LICENSING.md](docs/LICENSING.md) §3。
