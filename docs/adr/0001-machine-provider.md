# ADR-0001：机型适配层（Machine Provider）

> 决策记录，不是实现说明。实现细节、页面设计与语言包格式另见
> [../design/SERVICE-PAGE.md](../design/SERVICE-PAGE.md) 和 [../ARCHITECTURE.md](../ARCHITECTURE.md)。

## 状态

**已接受（Accepted）· 尚未实现。** 日期：2026-11-13。

本版是**外部评审后的修订版（v2）**，推翻了初版的三处设计，实现时以本版为准：

| 初版（作废） | 本版（生效） | 原因 |
| --- | --- | --- |
| 进程内 `SemaphoreSlim` 做单写者仲裁 | **单一特权宿主进程独占硬件**，UI/HUD/托盘是非特权客户端（带 ACL 的命名管道），仲裁在宿主内 | 进程内锁管不到另一个进程；直接 EC I/O 的工具（WinRing0 类）会绕过一切锁 |
| 服务页 v1 就做启停 + 改启动类型 | **v1 只做竞争检测 + 提示 + 只读观察**，全功能版进附录 | 停服务的代价（PSSvc 断了模式写入）远大于想象，不做「看着能回滚」的冒险 |
| 机型差异用代码 / 每个机型一个子目录 | **机型是数据不是代码**：provider 按**接口族**分，机型差异写数据文件，社区提交数据 | 否则每个机型都要发版；社区贡献只能改代码，无法规模化 |

- 决策范围：整仓（`Performix.Core` / `Performix.App` / `Performix.Overlay` / `Performix.Probe` / 新宿主 / 测试 / 文档 / 数据）。
- 取代：[../ARCHITECTURE.md](../ARCHITECTURE.md) §6「现在刻意不做品牌抽象层」的旧结论。
- 修订：**宿主形态见 [ADR-0002](0002-privileged-host.md)**（2026-11-13 外部评审后改为自动启动的
  Windows 服务 + 命名管道 IPC；本文 D3 第 1 条、D10 一行、备选 J、步骤 0 已同步加注）。
- 相关：设置「服务」页（[../design/SERVICE-PAGE.md](../design/SERVICE-PAGE.md)）、README 的「机型适配」表、
  许可边界（[../LICENSING.md](../LICENSING.md)）。

```
状态演进：Proposed → Accepted v1 → Accepted v2（本文，外部评审修订） → Implemented（步骤 0-4 完成） → Amended（第二个真机验证后，见 §7 步骤 5）
```

---

## 1. 背景

### 1.1 现状：只有一个品牌、一台被验证的机器

- 唯一的实测机是 **Acer Predator PH317-55（2021）**，协议、传感器来源、端到端行为记录在
  [../FINDINGS-PH317-55.md](../FINDINGS-PH317-55.md)。
- 现有代码虽然分层（`Core/Hardware`、`Mode`、`Rgb`、`Gpu`、`Display`），但**品牌事实没有边界**：
  Acer 的 WMI 类名、方法签名、管道 cmd 号、风扇行为字散落在 `Hardware/*.cs` 里，
  `Performix.App` 里也有厂商名与 Acer 专用逻辑（`Services/AcerHotkeyWatcher.cs`、
  `Services/TurboKeyToggle.cs`、`Services/ShellServices.cs` 的模式字节、`Pages/SettingsPage.xaml.cs`
  的 Acer 服务开关等）。
- 现在**没有** `IHardwareProvider`、没有能力查询层、没有宿主/写仲裁；跨品牌扩展的「未来计划」
  写在 ARCHITECTURE §6，只是文档，没有代码约束。
- 当前 `Performix.App` 的 `app.manifest` 是 `requireAdministrator`：**UI 进程自己就是特权的**，
  硬件写入直接发生在 UI 进程里。这是本 ADR 要改掉的第一个结构问题（见 D3）。

### 1.2 新的产品方向（已拍板）

1. 先适配 **GHelper（ASUS 系）与 PreySense（Acer 系）已经支持过的机型**。
2. **机型之间必须隔离**：不同机型不同实现，避免逻辑打架。
3. 设置里要有**按品牌设计的「服务」页**：检测本机 OEM 服务 → 提示竞争 → 用我们的实现替代其功能。
   **v1 只做检测与提示**（见 D11）。
4. README 要有**品牌-机型适配表**，并且**由数据文件生成**（见 D9），不手工维护。
5. 语言包开放外部文件（外壳 JSON + ICU MessageFormat，见 [SERVICE-PAGE 附录 B](../design/SERVICE-PAGE.md)）。

### 1.3 许可前提变了：整仓 GPL-3.0，可以合法移植 GHelper

- 本项目**整体 GPL-3.0**：`LICENSE`（项目声明 + GPL-3.0 全文）、`LICENSES/GHelper-GPL-3.0.txt`、
  `GHelperFanCurve.cs`（派生自 seerge/g-helper）都在仓库里；[../LICENSING.md](../LICENSING.md) §1 记录了这个决定。
- 因此 **直接移植 GHelper 的 ASUS ACPI / EC 代码在法律上是成立的**（GPL → GPL），不需要额外授权。
  **这跟之前 MIT 时期的约束完全不同**：那时 GPL 代码只能重写，现在可以搬，
  代价是必须履行出处与许可义务（见 D12）。
- 但「能搬」不等于「随便搬」：搬进来的是别人的**结构**（单例、全局设置、WinForms UI），
  我们的接口不能被它带着走。

### 1.4 为什么现在必须定这件事

- 服务页要实现「检测本机 OEM 服务」，就必须回答「这台机器是哪个品牌、哪些服务是它的」——
  这就是机型识别。没有识别层，服务页只能写死 Acer 的服务名，第二个品牌进来时逻辑必打架。
- 移植 GHelper 会带来第二套 ASUS 的写通道（`\\.\ATKACPI` ioctl + ASUS WMI），
  和 Acer 的管道 / WMI 写通道并存；**两个 provider 同时驱动 EC 是这份 ADR 要防的第一件事**。
- 我们自己的历史已经演示过「直写硬件」的代价：旧版曾通过 LibreHardwareMonitor 装过 WinRing0
  内核驱动（`Overlay/WinRing0Cleanup.cs` 就是清理它的代码）。**不做直接 EC 端口 I/O** 这条红线
  不是洁癖，是被杀软拦过、被真实风险教育过的结论。

---

## 2. 决策总览

一句话：**单一特权宿主独占硬件，UI 只做非特权客户端；宿主内「识别 → 能力 → 实现」三层 +
单写者仲裁 + 失效保护；能力三态（Declared/Probed/Verified）；机型差异是数据不是代码；
UI 只认能力与语义，不认品牌。**

```
┌──────────── 非特权（普通用户） ─────────────┐   ┌──────── 特权（提权/服务）─────────┐
│ Performix.App（WinUI 壳）                  │   │ Performix.Host                    │
│ Performix.Overlay（HUD）                   │◄─►│  · 唯一持有硬件访问权             │
│ 托盘 / 快捷键 / 诊断 UI                    │命名│  · IHardwareArbiter 单写者        │
│                                            │管道│  · IMachineProfile 识别           │
│ 只显示、只发命令；不碰 WMI / EC / NVAPI    │ACL │  · ICapabilitySet（三态）         │
└────────────────────────────────────────────┘   │  · IHardwareProvider（Acer/ASUS/Generic）│
                                                 │  · 失效保护 / 审计日志 / 看门狗   │
                                                 └───────────────┬──────────────────┘
                                                                 │ ACPI-WMI / 命名管道 / ATKACPI / NVAPI
                                                                 ▼
                                                               硬件 / EC
```

- **唯一特权边界**：宿主。UI 进程不再 `requireAdministrator`。
- **唯一写者**：宿主内的 `IHardwareArbiter`；客户端永远不并行写。
- **唯一真相**：能力三态 + 运行时可用性；UI 按能力渲染，按语义排序模式。
- **唯一数据源**：`data/machines/*.yaml`（机型数据）→ README 表 + 能力初始声明 + 服务清单。
- **唯一红线**：不做直接 EC 端口 I/O（见 D3）。

---

## 3. 背景约束（决策不能违反的硬条件）

| # | 约束 | 来源 |
| --- | --- | --- |
| C1 | 协议常量、帧结构、cmd 号、EC 写入顺序是实测事实，重构时**一字不改** | [../ARCHITECTURE.md](../ARCHITECTURE.md) §1 |
| C2 | no-driver fork（`PREYSENSE_NO_DRIVER`）：不装驱动、不发 IOCTL；驱动路径是 fail-fast stub | `Directory.Build.props` |
| C3 | 失败可回滚；不允许「写后不管」 | `Restore-AutoFan.ps1`、`AcerProfileApplyTransaction` |
| C4 | UI 层不直接 P/Invoke 硬件；硬件访问只经宿主协议（本 ADR 之后不再是 `IHardwareService` 直连硬件） | 本 ADR D3；[../WINUI3-MIGRATION.md](../WINUI3-MIGRATION.md) §5 |
| C5 | 整仓 GPL-3.0；移植必须留出处与许可 | [../LICENSING.md](../LICENSING.md)、`THIRD-PARTY-NOTICES.md` §2 |
| C6 | 320 DIP 窄窗 UI；**能力没声明 → 隐藏**，**声明了但当前不可用 → 禁用 + 原因** | [../SETTINGS-IA-v2.md](../SETTINGS-IA-v2.md)、[../UI-DESIGN-RULES.md](../UI-DESIGN-RULES.md) |
| C7 | 不为「将来可能支持」加万能配置项 / 反射插件 / 空抽象 | [../ARCHITECTURE.md](../ARCHITECTURE.md) §6 |
| C8 | **永远不做直接 EC 端口 I/O**（不装 WinRing0 类驱动、不碰 0x60/0x64 之外的端口方案） | 本 ADR D3；`Overlay/WinRing0Cleanup.cs` 的历史 |
| C9 | 宿主协议不暴露任意命令面：**不接受任意服务名、任意寄存器地址、任意路径** | 本 ADR D3（防本地提权 / DoS） |

---

## 4. 决策

### D1 三层模型：识别 → 能力 → 实现（模式列表由 provider 提供）

**决策**：机型适配拆成三层，职责不重叠。

| 层 | 接口 | 做什么 | **不做什么** |
| --- | --- | --- | --- |
| 识别 | `IMachineProfile` | 用 SMBIOS / BIOS / PCI / WMI 签名判断「这是不是我的机型」，返回匹配强度 | 不写硬件、不探测能力、不持有状态 |
| 能力 | `ICapabilitySet` | 回答「这台机器上能不能做、怎么做、安全等级多少、当前可不可用」 | 不含品牌名、不含写逻辑、不含 UI 文案 |
| 实现 | `IHardwareProvider` | 按**接口族**实现（Acer Gaming WMI / ASUS ATK）；只实现能力声明为可写的项 | 不判断机型（数据+识别层已给答案）、不画 UI、不碰 GPU 调校（D8） |

**识别规则**（顺序固定，禁止模糊匹配）：

1. **精确匹配**：SMBIOS 厂商 + 产品串（+ 需要时 BIOS 版本区间）命中 `ExactMatch`。
2. **系列匹配**：产品串命中已知系列前缀（同一底盘家族），`FamilyMatch`，
   但**能力上限降一档**（探测通过后才允许写；探测失败按只读）。
3. **兜底**：都不命中 → `GenericProvider`。
4. **歧义**：两个 profile 同时 `ExactMatch` → 视为不匹配，落 `Generic` + 日志告警（宁可只读，不可猜）。
5. **运行时反悔**：识别成功但能力探测失败（方法签名不对 / 调用被拒绝）→ 该能力本次会话降级，
   UI 显示原因；**不崩、不重试风暴**。

**模式列表由 provider 提供，不是全局固定五档**：

- 「节能/静音/均衡/性能/狂暴」是 **Acer 的词汇**；ASUS 是 `Silent / Balanced / Turbo / Manual`
  （GHelper 的语义）。把它们硬编码成全局五档，ASUS 落地时必然错位。
- provider 返回 `ModeDescriptor` 列表：`id`（该机型原始字节/字符串）+ `displayNameKey`（显示名 i18n key）
  + **语义标签**（`Eco` / `Quiet` / `Balanced` / `Performance` / `Max` / `Manual`）+ 是否支持热切换。
- UI 只按**语义**排序与画 tile，不认 id；「狂暴」按钮映射到该机型的 `Max` 语义项。
- Acer 的实测映射保留在数据文件里：`0x06 Eco / 0x00 Quiet / 0x01 Balanced / 0x04 Performance / 0x05 Turbo`
  （见 `ShellServices.cs`；注意用户界面上的「狂暴」= `Max` 语义，不保证所有品牌都有同名字）。

**Acer 已有的识别事实**（移植时照抄，不要另发明）：

- `SMBIOS Type 172` 的 Acer 格式化字节（`AcerEcLightingCapabilities.FromSmbios`，
  Major/Minor + 键盘/灯条声明位）。
- `root\wmi:AcerGamingFunction` 实例与方法签名矩阵（`AcerEcLightingDevice.Discover` 的
  `MatchesShape`：`gmInput` 是 UInt8 数组还是 UInt32/UInt64 标量、`gmOutput` 是 UInt32 还是 UInt64）。
- `PSSvc` 管道 `\\.\pipe\predatorsense_service_namedpipe` 的存在性（辅助信号，不作为唯一依据）。

> 规则：识别只产出 `MachineIdentity`（不可变 record）；能力探测产出新的 `CapabilitySet`。
> 两者在宿主内持久化缓存时都受 D6 的缓存键约束（机型 + BIOS + 驱动版本），BIOS 变化必须重探。

### D2 UI 只问能力不问品牌，并用架构测试守住

**决策**：

1. UI 只读「能力快照」（经宿主协议下发），**不允许** `if (brand == ...)`。
2. UI 程序集（`Performix.App` / `Performix.Overlay` 以及未来全部 UI 程序集）**不允许出现厂商名**。
   源码扫描测试：
   - 扫描 `src/Performix.App/**/*.cs` 与 `*.xaml`、`src/Performix.Overlay/**/*.cs`（跳过 `bin/`、`obj/`）。
   - 词表：`Acer`、`ASUS`、`Asus`、`ROG`、`TUF`、`Predator`、`Nitro`、`Nvidia`、`NVAPI`、
     `Intel` / `AMD`（`Intel` 在 CPU 遥测里可能是真实含义，按需换成具体类型名白名单）。
   - 命中 → 测试失败。
3. 现状**不满足**这条规则（`AcerHotkeyWatcher`、`TurboKeyToggle`、`ShellServices` 的模式字节、
   `SettingsPage` 的 Acer 服务开关、`LocalizationService` 里的 Acer 文案等）。因此第一步是
   一个**已检入的基线清单** `tests/architecture/brand-baseline.txt`：
   - 规则：**基线只能缩，不能长**。新出现的厂商名一律失败；基线里的每一条都要有对应清理任务。
   - 清理顺序：文案 → 服务页入口 → 热键/模式字节 → 最后才是 `AcerHotkeyWatcher` 这类真实现。
4. UI 不引用 `Providers.Acer` 命名空间：provider 内部类型 `internal`，宿主内组装；
   另加一条测试扫 `Performix.App.csproj` 的 `Compile Include` / `ProjectReference` 不含品牌目录。
5. **数据驱动**：UI 的机型/能力展示只读宿主下发的数据（来源 D9 的 `data/machines/*.yaml`），
   不写代码分支。

### D3 进程与权限模型：单一特权宿主 + 带 ACL 的命名管道

**这是初版完全没写的部分，也是最重要的一条。**

**决策**：

1. **单一特权宿主**（`Performix.Host`）独占硬件。**宿主形态已由 [ADR-0002](0002-privileged-host.md)
   修订为自动启动的 Windows 服务**（本节原定的「按需启动提权进程」作废）；服务配置、Session 0、
   管道安全、消息格式、版本协商、多会话租约等**实现细节见 ADR 0002**。不装驱动、不建驱动服务，
   保持 no-driver fork 的定位（C2）；宿主协议不变，只换了宿主启动方式。
2. **UI / HUD / 托盘全部是非特权客户端**：
   - `Performix.App` 去掉 `app.manifest` 的 `requireAdministrator`，以普通用户运行；
   - 提权只发生在**安装/升级**（安装程序）与显式的一次性排障工具；常驻部分由服务持特权，
     UI 永远不持特权（实现见 [ADR-0002](0002-privileged-host.md) §4.3 权限墙）；
   - 客户端只发强类型命令、收状态快照，**不链接任何硬件访问代码**。
3. **通信 = 带 ACL 的命名管道 + 强类型协议**：
   - 管道名固定、**不带版本**（`\\.\pipe\PerformixHost`；版本走首帧 `Hello` 协商，见
     [ADR-0002](0002-privileged-host.md) D7），**ACL 只允许交互用户与管理员**
     （`Interactive` / `Administrators`）；不开放 `Everyone`、不开放网络。
   - 消息用**固定枚举的命令 + 结构体参数**（`GetSnapshot` / `SetMode` / `SetFanDuty` /
     `ProbeExport` / `RestoreFans`…），带协议版本握手、请求 ID、超时、并发上限。
   - **服务端按允许列表校验**：命令参数不接受任意服务名、任意寄存器地址、任意文件路径、
     任意 shell 命令。服务页的「停止服务」这类命令的参数必须是**已知服务 ID**（来自数据文件），
     不是字符串直传（C9）。
   - 服务端用 `GetNamedPipeClientProcessId` + 进程校验确认调用者，拒绝非预期进程；
     并对单客户端做限流（防止本地 DoS）。
4. **单实例**：宿主单实例**主要由 SCM 保证**（ADR-0002 D8）；`Global\PerformixHost` 命名 mutex
   可保留作双保险（防手工起第二个进程）。客户端另有 UI 单实例 mutex（用 `Local\` 命名空间按会话隔离，
   沿用现有内部标识 `PreySenseApp_Unique_System_Mutex_999` /
   `PredexWinUI_Unique_Mutex_1234` 的兼容面，见 [../DECISIONS-FOR-USER.md](../DECISIONS-FOR-USER.md) B2）。
   宿主退出后客户端要能显示「宿主未运行」并提供「启动宿主」；不要静默失败。
5. **永远不做直接 EC 端口 I/O**（C8）：不装 WinRing0 / 不 `in/out` 端口 / 不映射物理内存写 EC。
   历史证据：旧版通过 LibreHardwareMonitor 装过 WinRing0 驱动，现在 `Overlay/WinRing0Cleanup.cs`
   专门清它。理由：**只有大家都走 WMI / ACPI 时，总线层才会替我们串行化**；
   直接端口 I/O 会绕过所有仲裁，是真正能写坏 EC 的那类操作。
6. **检测直接 EC I/O 的工具并把能力降为只读**：宿主启动与周期性探测时，检查常见直接访问工具
   （WinRing0 类驱动/服务、ThrottleStop、Intel XTU、厂商风扇工具等；名单放数据文件可更新）。
   命中时：
   - 相关写入能力降为 `ReadOnly` 或 `NoWrite`，UI 显示「检测到 X 正在直接访问硬件，已停止写入」；
   - **不自动杀别人的进程**（v1），只提示用户。
7. **信任边界**：宿主是唯一特权面，所以宿主代码的输入校验比 UI 重要十倍：
   协议解析、服务 ID 允许列表、参数范围（占空比 0-100、模式 ID 必须在数据文件里）都要在宿主侧再校验一遍。

### D4 宿主内单写者仲裁：`IHardwareArbiter`

**问题**：硬件写路径现在靠 `HardwareOperationGate`（一个 `SemaphoreSlim(1,1)`）串行化，
但它是**全局一把锁，不区分所有者**；而且它在 UI 进程里，管不到第二个进程。
宿主化之后，硬件写只发生在宿主内，**进程内仲裁就够用了**——因为外部进程要么走宿主协议（被串行化），
要么就是 D3 说的「直接 EC I/O」危险工具（检测后降级）。

**决策**：

1. 宿主内所有写操作必须先拿**写租约**（lease），读操作拿**读租约**（可共享；
   显示切换这类会换 adapter 的操作要独占）。
2. 所有者（`HardwareOwner`）：`Telemetry` / `FanCurve` / `ModeSwitch` / `Lighting` /
   `GpuTuning` / `ServicePage` / `Probe` / `HostMaintenance`。
3. **风扇与模式是写临界域**：同一时刻只允许一个所有者持有 `FanCurve` 或 `ModeSwitch` 的写租约。
4. **长期所有者可被抢占**：风扇曲线是长租约；模式切换进来时请求抢占 ——
   风扇曲线先暂停下发（保留状态与目标值），模式切换写完、回读确认后，风扇曲线恢复。
   抢占契约显式（`IPreemptibleWriteOwner`），不靠「反正锁一次就过去了」。
5. **租约必须有超时与心跳**：拿不到返回「忙」，阻塞排队上限可配；宿主崩溃租约自然消失。
6. **`HardwareOperationGate` 退役**：先让 arbiter 内部复用同一把信号量（避免双锁），
   调用点逐步迁移；迁移完成后删除 gate（步骤 3 的退出条件之一）。
7. **跨进程边界**：宿主替所有客户端串行化；但**OEM 栈仍在写 EC** 时我们无法阻止 ——
   那由「服务页检测 + 提示 + 关闭官方 UI/agent」与 WMI 侧的失败重试纪律处理。
   `AOD_WMI_HANDLER_MUTEX` 说明 Acer 的 WMI handler 是全局串行化的（见
   [../KEY-EVENT-SOURCES.md](../KEY-EVENT-SOURCES.md) §6 风险表），我们的写可能排在 OEM 后面超时 ——
   所以写入必须「回读确认 + 有限重试 + 失败可回滚」，不能盲重试。
8. **可观测性**：诊断快照要包含当前租约持有者、等待者、超时次数、最近审计记录。
   没有观测的锁最后都会变成死锁。

### D5 失效保护（必备，初版没有）

**决策**：宿主必须假设自己会崩、会被杀、会睡眠。下面四条是硬要求。

1. **风扇归还固件自动控制**：
   - 正常退出：宿主恢复固件 Auto（Acer 为 `SetGamingFanBehavior 0x410009`，实测见 FINDINGS §2.3）。
   - 睡眠 / 休眠 / 关机前：同样恢复 Auto，并记录「已归还」状态。
   - 崩溃 / 强杀：**无法保证**（进程已死）→ 缓解手段：
     a) 宿主写一个「正在自定义控制」标记文件（含时间戳与恢复命令），下次启动检测到标记就提示/自动归还；
     b) 文档与 UI 提供 `scripts\Restore-AutoFan.ps1` 一键恢复；
     c) **优先用固件曲线/EC 表格而不是持续下发占空比**，让固件始终保有兜底。
2. **软件循环曲线的温度硬上限兜底**：软件曲线模式下，CPU（或 GPU）温度 ≥ 95 °C 时
   强制停止自定义下发、恢复固件 Auto，并在审计日志里记一次 `thermal-fallback`。
   否则宿主一旦卡住/崩溃，风扇可能停在固定低转速，这是最危险的状态。
3. **每次写入记审计日志**（社区报 bug 全靠它）：
   - 字段：时间、来源客户端 + owner、操作、目标能力、**写入值**、**回读值**、回读强度、结果、
     安全等级、机型/BIOS/驱动版本。
   - 位置：`%ProgramData%\Performix\logs\audit-*.log`（或沿用 `%ProgramData%\PreySense\logs` 兼容面），
     轮转 + 大小上限；不记密钥/凭据；`更新与关于` 页提供「导出诊断」打包。
4. **睡眠唤醒 / AC 插拔后重新应用**：EC 会在这些事件后重置设置。
   - 宿主订阅电源事件（现有 `WmiController.PowerSource` / `TelemetryCoordinator` 已有电源感知），
     唤醒后重新探测能力并重新应用「用户当前模式 + 风扇策略」；
   - 重新应用失败要在 UI 顶部给 `InfoBar`，不要静默；
   - AC 插拔会改变可用模式（例如电池下狂暴/超频不可用）→ 用 D6 的「运行时可用性」表达。

### D6 能力三态（Declared → Probed → Verified）+ 运行时可用性

**决策**：能力不是 bool，是三态；只有走到 `Verified` 才允许自动写。

| 状态 | 含义 | 来源 | 允许写？ |
| --- | --- | --- | --- |
| `Declared` | 机型数据文件声明「这个机型应该有这个能力」 | `data/machines/*.yaml` | ❌ |
| `Probed` | 非破坏性读取成功（方法存在、签名匹配、读到值、服务在跑） | 运行时探测 | ❌（除非用户显式打开「实验性写入」） |
| `Verified` | **写入后回读通过**，或维护者在真机上确认（README 信任等级 = 维护者实机） | 探测 + 写入回读 / 人工验收 | ✅ |

- **默认 fail-closed**：数据没声明 = 没有这个能力（UI 隐藏）；声明了但没探测 = `Declared`
  （UI 显示但写入口禁用，原因「未探测」）；探测通过但没验证过写 = `Probed`
  （写入口禁用，原因「未在真机验证写入」）；只有 `Verified` 才能自动写。
- **用户显式「实验功能」开关**可以把 `Probed` 的能力提升为可写（一次会话有效、每项单独确认、
  强烈警告、失败自动降级），这是给愿意当小白鼠的用户留的路。
- **探测缓存**：缓存键 = `(机型 ID, BIOS 版本, 驱动版本, 宿主版本)`。BIOS 或驱动变了必须重探；
  缓存只缓存「探测结论」，不缓存传感器读数。
- **运行时可用性 + 原因**：能力除了状态，还要带 `Availability` 与 `Reason`：
  - `Available` / `Unavailable`（例：需接电源 / 需要重启才生效 / 被 X 占用 / 宿主未运行 /
    检测到直接 EC I/O 工具 / BIOS WPBT 改了状态）；
  - UI 规则（C6）：**能力没声明 → 隐藏；声明了但当前不可用 → 禁用 + 原因**，
    不允许「灰着不解释」，也不允许把不可用当不存在。
- **降级永不升级**：探测/写入失败（拒绝访问、返回错误、回读不符、检测到竞争工具）→ 本次会话
  该能力降为只读并记原因；要恢复只能重新探测。

> **修订（2026-11-13，[ADR-0003](0003-capability-availability.md)）**：**对外呈现与状态机以 ADR-0003 为准**；
> 本节的三态是**内部探测口径**（决定我们敢不敢写），九态是**产品口径**（决定用户看到什么、能不能点）。
> 三态照旧，但不再直接映射成 UI 状态：

| 三态（内部） | 对应九态（对外） | 说明 |
| --- | --- | --- |
| `Declared` | **未检测到 / 不支持**（的前提） | 只有数据文件声明，还不能断言能力存在；未探测前既不许显示成「可用」，也不许显示成「不支持」 |
| `Probed` | **只读**（读确定、但未写验证） | 读到值 ≠ 能写；写入口必须禁用并给原因 |
| `Verified` | **可用**（还要过运行时可用性） | 写 + 回读通过；「可用」还要求该控制依赖的所有输入信号都可信（ADR-0003 D3） |

- 本节原有的 `Availability` / `Reason`（需接电源 / 需重启 / 被占用 / 宿主未运行 / 竞争工具…）**保留**，
  作为九态 `reason` 的一部分；九态是呈现层，不是把运行时可用性删掉。
- 合成规则、优先级、证据门槛、文案与诊断页见 ADR-0003 §3；探测纪律见其 §4。

### D7 写入安全等级：按「能力 × 机型 × BIOS」声明，provider 只给上限

**决策**：

- 安全等级（`ReadOnly` / `WriteWithReadback` / `NoWrite`）与回读强度（`Verified` / `Echo` / `None`）
  按 **能力 × 机型 × BIOS** 三层解析：

  ```
  最终等级 = min( provider 声明的上限,  机型数据声明的等级,  BIOS 特定覆盖 )
  ```

  provider 只提供**上限**（例如 Acer provider 的「EC 灯效最多到 `WriteWithReadback`」），
  具体机型/BIOS 的等级来自数据文件；数据文件没写就取更保守的一层。
- `WriteWithReadback` 的补充要求同初版：必须有回读强度（`Echo` 只能用于可恢复的调参，
  关键操作如模式切换必须 `Verified`），必须有回滚手段。
- `NoWrite` 是默认值；`ReadOnly` 用于「明确不支持写」。
- 等级与强度进 `CapabilitySet`、`--probe` 输出与诊断页；每次写都按最终等级校验
  （数据文件写「允许」不能越过 provider 上限）。
- PH317-55 的示例定级（用于校准标准，不是通用规则；**BIOS 变化要重新确认**）：

| 能力 | 通道 | 等级 | 回读强度 |
| --- | --- | --- | --- |
| 性能模式 | 管道 cmd 32 + cmd 33 | `WriteWithReadback` | `Verified` |
| 风扇占空比 | WMI `SetGamingFanSpeed` | `WriteWithReadback` | `Echo`（RPM 交叉验证走管道 cmd 15 才算 `Verified`） |
| 风扇行为字 | WMI `SetGamingFanBehavior` | `WriteWithReadback` | `Echo` |
| 风扇曲线（8 点） | WMI `SetGamingFanTable` / 自定义模式 | `WriteWithReadback` | `Echo` |
| EC 键盘灯 / 灯条 | WMI EC 方法 + `GetGamingRgbKb` 回读 | `WriteWithReadback`（**写回真机未验收 → 实际 `Probed`**） | `Echo` |
| MUX | 无（`ActiveGpuMuxMode = -1`） | `ReadOnly` | — |
| 刷新率 | Windows 显示 API | `WriteWithReadback` | `Verified`（`EnumDisplaySettings` 读回） |
| CPU PL / 欠压 / PROCHOT | 需 MSR 驱动（no-driver fork） | `NoWrite` | — |
| GPU 时钟偏移 | NVAPI | `WriteWithReadback`（**真机写入未验收 → 实际 `Probed`**） | `Echo` |

### D8 正交维度：按**写入路径归谁**划分，不按「这东西叫 GPU」

**决策**：初版按「OEM 平台 / GPU 厂商」分还太粗。判定标准改成一句话：
**这个写入的实际路径归谁，就归哪个维度。** 名字里带 GPU 不代表归 NVAPI。

| 能力 | 写入路径 | 归属 | 现在在哪 | 目标 |
| --- | --- | --- | --- | --- |
| 性能模式写入 | OEM ACPI / 管道 / `ATKACPI` | OEM | `AcerPipeSensorSource` 等 | `Providers/<Family>` |
| 风扇占空比 / 曲线 / 行为字 | OEM WMI / EC | OEM | `WmiController.Fans`、`FanScheduler` | 写通道进 provider，曲线算法留 `Mode/` |
| 键盘 / 灯条 RGB | OEM EC / HID / 软件合成 | OEM | `Core/Rgb/*` | provider 声明类型，实现进 provider |
| **MUX / dGPU 断电 / TGP** | **OEM ACPI 写**（读状态可能走 GPU API） | **OEM** | `NvidiaDdsService` + WMI | `IMuxBackend`（OEM 写）+ `IGpuDdsReader`（只读） |
| **GPU 核心 / 显存偏移、GPU 传感器** | **NVAPI / ADL** | **GPU 厂商** | `Core/Gpu` | 不动，继续由 `IGpuControl` 负责 |
| 刷新率 | Windows 显示 API | OS | `DisplayManager` | 不动 |
| PL1/PL2 / 欠压 / PROCHOT | CPU 厂商 + OS（多数要 MSR） | CPU | `Mode/IntelRapl*` | 不动；no-driver 下 `NoWrite` |
| Windows 电源方案 | OS | OS | `WindowsPowerPolicy` | 不动；注意 D10 的 Generic 降级说明 |

**硬规则**：

- `Providers/<Family>/` 里禁止出现 GPU 厂商调用（`NvAPIWrapper`、`NvidiaGpuControl`、ADL 等），
  GPU 侧只通过 `IGpuControl` 抽象；反之 `Core/Gpu/` 里禁止出现 OEM 写通道。
- MUX 是**跨维度**能力：写走 OEM（`IMuxBackend`），读状态走 GPU（`IGpuDdsReader`），
  两者都要在能力快照里分别标可用性。
- CPU 厂商（Intel / AMD）是第三条正交轴：按 **CPU 供应商**分支，不按品牌分支
  （否则 ASUS + AMD 会掉进 Acer 的逻辑）。

### D9 机型隔离 + 「新机型是数据，不是代码」

**决策**：隔离靠目录 + 可见性 + 数据；新机型由社区提交**数据**，不必改代码。

1. **provider 按接口族分，不按机型分**：
   - `Providers/AcerGamingWmi/`（Acer `AcerGamingFunction` 家族）
   - `Providers/AsusAtk/`（ASUS `ATKACPI` 家族）
   - `Providers/Generic/`
   - 一个接口族覆盖该家族的全部机型；家族内部差异写数据。
2. **机型数据**：`data/machines/<brand>/<model>.yaml`（或 JSON，格式见 D9.3）。
   内容：识别串、模式列表（id + 语义 + 显示名 key）、风扇 id/单位/是否存在转速地板、
   RGB 类型、MUX 能力、刷新率策略、依赖服务清单、每项能力的等级/证据/信任等级。
   **社区只提交数据文件 + 证据**，不改代码。
3. **数据文件最小字段集**（v1，先小后大，见 D10）：
   `id` / `match`（SMBIOS 串、BIOS 区间）/ `family`（`acer-gaming-wmi` / `asus-atk` / `generic`）/
   `modes[]`（`id`、`semantic`、`i18nKey`）/ `fans`（`ids`、`unit`、`rpmReadback`）/ `rgb`（`kind`、`zones`）/
   `mux` / `services[]`（`name`、`safety`、`providesKey`、`impactKey`）/
   `capabilities{}`（`state`、`safety`、`readback`、`biosRange`）/ `evidence[]`（`kind`、`ref`）/ `trust`。
4. **README 品牌-机型表由数据文件自动生成**：`scripts\generate-machine-table.ps1`（待实现）
   读 `data/machines/**` 输出 README 的表格片段。**改表必须改数据**；手工改表视为错误。
   生成器未实现前，手工表必须与数据文件一致，并在 README 里注明「生成器待实现」。
5. **隔离硬规则**（保留初版的可执行部分）：
   - 目录 + 可见性：provider 具体类型 `internal`；跨家族互相引用编译期失败。
   - 品牌常量只准在自己的 `Providers/<Family>/`；搬家零改写（C1）+ golden byte 测试。
   - 测试注入：WMI / 管道 / HID / 注册表访问可注入（`MockHardwareService` 已证明可行）。
   - 命名：品牌前缀保留（`AcerPipeSensorSource`、`AsusAcpi`）；**不允许**把品牌专用实现
     起中性名字后到处复用。
   - 每个受支持机型必须有 `docs/FINDINGS-<MODEL>.md` + README 里对应的信任等级。
6. **信任等级**（README 与 App 都显示）：
   - `T2 维护者实机`（维护者自己跑完整验收）
   - `T1 N 台独立社区验证`（N ≥ 3 台不同机器、独立报告）
   - `T0 社区单台`（一份脱敏报告，未经独立复现）
   - `未验证`（只有上游/社区说法）
   **只有 T2 才能把「写」能力标 `Verified`**；T0/T1 最多 `Probed`，除非维护者复测。

### D10 过度设计警告（含 Generic 兜底的真实能力）

**决策**：接口先落、数据先小；不做的清单要写清楚。

1. 这一轮只做：接口 + `GenericProvider` + 把 Acer 包进 `Providers/AcerGamingWmi/` + 宿主骨架。
2. **`GenericProvider` 不许吹成「通用只读方案」**：
   - ACPI 热区（`MSAcpi_ThermalZoneTemperature`）在很多机型上是**假值**（PH317-55 的 TZ00
     恒 60.1 °C，见 FINDINGS §2.4）；
   - `powercfg` 在现代机型上常常只剩「平衡」，真正起作用的是 **overlay scheme**（电源模式覆盖），
     写它比切 scheme 更接近实际；
   - 所以 Generic 只能说：**GPU 传感器（NVML）+ Windows 电源模式（含 overlay）**，
     其它一律「未知」；不得宣传成「支持所有机型」。
3. **现在明确不做**：

| 不做 | 什么时候再做 | 为什么现在不做 |
| --- | --- | --- |
| 反射 / 插件式 provider 加载（DLL 热插拔） | 明确要第三方扩展时 | 反射难测、安全面大、没有需求 |
| 细粒度能力位扩展到几十个 bool | 出现第三个接口族时 | 现在能力轴够用，多出来的字段没人验证 |
| 跨进程写仲裁（驻留服务 / 驱动） | 永远不做（no-driver fork 红线） | 装服务/驱动等于变成另一个 OEM 栈 |
| 通用 EC 抽象层 | 不做 | 各家访问路径不同，伪统一诱导误写 |
| 多 provider 并存的自动选择策略 | 第二接口族落地后 | 现在只需要「精确 → 系列 → Generic」 |
| 机型数据 schema 全量设计 | 第二台真机验证后 | 一台机器的数据撑不起 schema，先小后大 |
| 语言包热加载 | 不做（v1 切换语言重建窗口/提示重启） | 见 SERVICE-PAGE 附录 B |

> **修订（2026-11-13，[ADR-0002](0002-privileged-host.md)）**：上表「跨进程写仲裁（驻留服务 / 驱动）」
> 一行说的是**不靠驻留服务去跟别家 OEM 栈做写仲裁、不装驱动**，这条不变；
> **我们自己的宿主服务化不在此列** —— 宿主是常驻 Windows 服务，按 ADR-0002 执行。
> 「不装驱动」仍然是红线（C2 / C8）。

4. **接口冻结策略**：v1 允许步骤 3 前调整；步骤 3 后只做「加字段 / 加可选成员」；
   第二个真机验证通过后做 v2 评审，删掉为单机型预留的字段（步骤 5）。
5. **反模式**：不要为了「看起来通用」把 Acer 常量泛化命名（`AcerFanBehaviorWords` →
   `FanBehaviorWords` 只是把事实藏起来）。

### D11 服务页 v1 范围：只做竞争检测与只读观察

**决策**：

- **v1 不做启停、不改启动类型**。只做：
  1. 检测本机 OEM 服务/进程（按数据文件 + 实际探测）；
  2. **竞争检测**：官方 UI/agent 是否在跑、是否有直接 EC I/O 工具在跑、是否有其它性能工具；
  3. 提示「它会和你抢什么」+ 建议用户自己关掉官方 UI（不自动杀）；
  4. 只读展示服务状态、「它提供什么 / 对我们的影响」。
- **为什么不能无脑停服务（必须写进文档的反面案例）**：Acer 的模式写入依赖 `PSSvc` 的
  cmd 32（`AcerPipeSensorSource`），**停掉 `PSSvc` 等于把我们自己的模式写入断掉**——
  实测：停了它切模式必失败、温度读数走 ACPI 回退（[SETTINGS-IA.md](../SETTINGS-IA.md) §6 第 1 条）。
  这就是「服务页 v1 只读」的直接理由，也是全功能版的允许列表必须先写清楚的前提。
- 全功能版（启停 + 启动类型）设计见 [SERVICE-PAGE.md 附录 A](../design/SERVICE-PAGE.md)，
  且必须满足：允许列表（只动整理过的 OEM 用户态服务，驱动级不碰）、默认只做运行时暂停
  （不改启动类型）、永久禁用要记录完整状态、处理 Windows Update / Store / BIOS WPBT 装回、
  卸载可恢复。

### D12 GPL 合法性与移植纪律

**法律结论**：本项目整仓 **GPL-3.0**，所以**可以**直接移植 seerge/g-helper（GPL-3.0）的 ASUS
ACPI / EC 代码，**不需要额外授权**。这与 MIT 时期不同：那时收录 GPL 派生代码会破坏整体许可，
现在整体已经是 GPL，方向一致。

**移植每个文件都必须做的五件事（缺一不可）**：

1. **文件头出处注释**，保留原样并补充信息：

   ```csharp
   // Ported from seerge/g-helper (GPL-3.0), https://github.com/seerge/g-helper
   // Upstream: app/AsusACPI.cs @ <commit/tag>（例如 v0.200）
   // Modifications by Performix: <改了什么；原样照搬就写 “none”>
   // SPDX-License-Identifier: GPL-3.0-or-later
   ```

   SPDX 跟着仓库 `LICENSE` 走：`LICENSE` 的项目声明是 **“version 3 … or (at your option) any
   later version”**，所以本项目文件用 `GPL-3.0-or-later`。移植文件**以上游文件头的声明为准**：
   上游写 `-only` 就 `-only`，写 `-or-later` 就 `-or-later`；上游没写清楚就保守按 `GPL-3.0-only`
   解释并在注释里注明「上游未明示 only/later」。不要为了统一而擅自改上游许可版本。
2. **在 `THIRD-PARTY-NOTICES.md` §2 登记**：现在只有 `GHelperFanCurve.cs` 一行；
   每移植一个文件，就加一行「本仓库文件 / 上游文件 / 改编说明」，并保留
   `LICENSES/GHelper-GPL-3.0.txt` 随仓库分发。**没有登记**的移植 = 未完成。
3. **新增文件也声明 GPL-3.0**：文件头 SPDX + 与项目内其它新文件一致（整仓 GPL 覆盖，但审计要求逐文件可见）。
4. **保留上游的来源链**：如果上游文件自己写着「来自 asus-linux / asus-wmi 的某个实现」，
   那段注释原样保留，不要只留一层。
5. **不搬非 GPL 内容**：GHelper 仓库里的图标、字体、商标、第三方素材不因为「项目 GPL」就打进来。
   ASUS ROG 字体在 [../LICENSING.md](../LICENSING.md) §3.3 已明确排除，继续排除；
   GPL-3.0 §7 不授予商标权。

**移植技术纪律**：

- 只移植**协议事实与算法**：命令号、字节、帧结构、曲线归一化、模式映射、EC 访问顺序。
- **不引入**上游结构：全局设置单例、WinForms UI、自动更新、AutoStart 逻辑。接口形状由本 ADR 决定。
- **锁定上游版本**：记录 commit/tag；升级时 diff + 更新 notices 行 + 跑 golden 测试。
- 移植顺序：先 `AsusAcpi`（设备 ioctl 封装）→ 再 EC / 模式 / 风扇 → 再 RGB / MUX；
  每层先 `NoWrite`，真机读通后逐项升到 `WriteWithReadback`。
- **不能用「参考算法重写」来逃避署名**：既不可靠也不诚实；GPL 已经允许搬，就按规矩搬。

**反向义务**：改成 GPL-3.0 后，本项目不能再以 MIT / 闭源方式发布；分发二进制必须提供源码
（或 GPL-3.0 §6 的书面 offer）。

---

## 5. 后果

### 5.1 正面

- **误写风险断崖式下降**：UI 不再持特权；宿主是唯一写者；能力默认 fail-closed；
  直接 EC I/O 的工具被检测后自动降级；软件曲线有温度硬上限兜底。
- **新机型只改数据**：社区提交 `data/machines/*.yaml` + 证据即可，不用发版、不用改代码。
- **服务页可落地且不危险**：v1 只读检测，先把「谁在抢硬件」讲清楚；全功能版有允许列表与完整恢复。
- **可测试**：能力三态、识别、仲裁、协议全部是纯逻辑 + 注入探针，可进现有 MSTest 基线
  （当前 584 通过 / 31 跳过）。
- **GPL 变成优势**：ASUS 支持可以直接移植 GHelper 的成熟实现，代价只是出处与许可流程。

### 5.2 负面 / 成本

- **宿主是多出来的一层**：多一个常驻进程、一套 IPC 协议、一套版本握手；安装/升级时多一次 UAC
  （运行时不再弹，见 ADR-0002）；UI 要处理「宿主未运行 / 宿主崩溃 / 版本不匹配」三种状态。
- **数据文件与生成器是新增维护面**：schema、生成脚本、数据校验测试都要写；
  数据错了比代码错了更难发现（所以要有测试 + 证据链 + 信任等级）。
- **Acer 代码要搬家**（大 diff），有搬错常量的风险 —— 缓解：只移动不改值 + golden byte 测试 +
  分两步（先适配器包住，再迁文件）。
- **架构测试一开始是红的**：`Performix.App` 现在就有厂商名，必须建基线并让它只缩不长。
- **provider 之间会出现相似代码**（风扇曲线调度、模式事务各写一份）——故意的：隔离优先于复用。
- **宿主崩溃仍无法保证归还风扇**：只能靠标记文件 + 恢复脚本 + 温度硬上限兜底；这是残余风险，必须写进文档。
- **文档债**：每个机型 `docs/FINDINGS-<MODEL>.md` + 数据文件 + README 表 + notices 逐文件登记。

### 5.3 对现有代码的影响（实现时要动的清单）

| 位置 | 影响 |
| --- | --- |
| 新项目 `src/Performix.Host/` | 特权宿主：管道服务端、协议、arbiter、看门狗、审计、provider 组装 |
| `src/Performix.App/app.manifest` | 去掉 `requireAdministrator`；宿主由 Windows 服务启动（[ADR-0002](0002-privileged-host.md)），界面只负责请求 `StartService` |
| `src/Performix.App`、`src/Performix.Overlay` | 改为宿主客户端；删除直连硬件的调用；厂商字面量进基线并逐步清零 |
| `Core/Hardware/Acer*`、`PredatorPipeService`、`AcerProfileApplyTransaction` 等 | 迁到 `Providers/AcerGamingWmi/`；常量与帧结构零改动 |
| `Core/Services/HardwareOperationGate.cs` | 被宿主内 `IHardwareArbiter` 取代（先包装、后删除） |
| `Core/Services/IHardwareService.cs` | 拆成「宿主实现」+「客户端契约」；UI 侧只留强类型命令/快照 |
| `Core/Rgb/AcerEcLightingCapabilities.cs` | 识别逻辑进 `Providers/AcerGamingWmi/MachineProfile`（type172 解析保留） |
| `Core/Gpu/*`、`Core/Display/*`、`Core/Mode/*` | 保持通用；`Mode/` 只加接口依赖，不搬品牌常量 |
| `Core/Services/AcerServiceDebloater.cs` | v1 只保留**只读检测**；启停代码移到全功能版（附录 A 的允许列表） |
| `src/Performix.Probe/Program.cs` | 增加宿主命令 `ProbeExport`（脱敏 JSON）+ 保留只读 `--gpuoc` 等 |
| `data/machines/**`、`scripts/generate-machine-table.ps1` | 新增：机型数据 + README 表生成器（待实现） |
| `docs/ARCHITECTURE.md` §6、README | 实现后同步（本 ADR 取代 §6 的结论） |
| `tests/Performix.Tests` | 新增：架构扫描、宿主协议、能力三态、仲裁、失效保护、数据 schema、白名单/允许列表 |

### 5.4 不做的后果（也是决策的一部分）

- 不做跨进程仲裁 → 直接 EC I/O 的工具我们管不到；靠检测 + 降级 + 提示兜底，**这是残余风险**。
- ~~不做常驻服务~~ → **已改判**：宿主是常驻 Windows 服务（[ADR-0002](0002-privileged-host.md)）；
  代价是睡眠唤醒 / 快速启动后必须重新初始化（ADR-0002 D2 / N7），不能假设状态还在。
- 不做热加载语言 → 切语言要重建窗口/重开宿主连接，但 v1 可接受。

---

## 6. 备选方案

| # | 方案 | 描述 | 为什么不选 |
| --- | --- | --- | --- |
| A | **维持现状：UI 提权直连硬件 + if 判断品牌** | 在 `Hardware/` 里加 `if (isAsus)` 分支 | 逻辑打架、UI 持特权、两个品牌写通道互抢——正是要解决的问题 |
| B | **每品牌独立 fork / 独立 exe** | 两套 UI、两套测试、两套发布 | 维护成本爆炸；Bug 修两次；许可与署名更难管 |
| C | **全插件化（反射加载 DLL + 机型 JSON 描述）** | provider 以 DLL 插拔 | 现在只有一台真机，schema 一定错；反射难测、安全面大；违反 C7 |
| D | **只做能力位，不做 provider/宿主** | `IHardwareService` 上加一堆 `SupportsX` 布尔 | 只解决 UI 显示，不解决所有权/常量归属/隔离/特权边界 |
| E | **抽象统一 EC 层** | 各家 EC 用一套读写抽象 | Acer 走 WMI 方法、ASUS 走 ATKACPI，没有共同模型；伪统一会诱导误写 |
| F | **直接 fork GHelper 做 ASUS 版** | 不移植，维护两个上游分支 | 与「一个轻量前端」定位冲突；不解决 Acer；GPL 义务不会减少 |
| G | **驻留服务 / 驱动做全局写仲裁** | 装服务或驱动，跨进程串行化硬件 | 违反 no-driver fork 红线；回到「装一堆后台服务」原点（注意：**我们自己的宿主服务化与之不同**，见 [ADR-0002](0002-privileged-host.md)） |
| H | **照抄 GHelper 的结构** | 把 ASUS 代码连同单例/设置/UI 一起搬 | 接口被上游绑架；Acer 与 ASUS 又要打架 |
| I | **进程内仲裁 + 危险操作「小心点」提示**（初版） | 只有一把锁，靠文档纪律防误写 | 挡不住第二个进程、挡不住直接 EC I/O 工具；已被外部评审否决 |
| J | **用 Windows 服务做宿主（而非按需提权进程）** | 常驻服务 + 客户端 | **已改判**：2026-11-13 外部评审后采纳（见 [ADR-0002](0002-privileged-host.md)）。原判断是「需要安装服务、卸载残留、与 no-driver fork 定位冲突」；评审后认为常驻是后台与失效保护的前提，安装/卸载成本可控，且**服务 ≠ 驱动**，不碰 no-driver 红线 |

---

## 7. 落地顺序

| 步骤 | 内容 | 退出条件（验收） |
| --- | --- | --- |
| **0. 宿主骨架 + 协议** | `Performix.Host`（先做控制台宿主，服务化见 [ADR-0002](0002-privileged-host.md)）：命名管道（ACL：交互用户 + 管理员）、协议版本握手、命令允许列表、单实例 mutex、审计日志骨架、`GetSnapshot` 只读命令；UI 改为客户端并显示「宿主未运行」 | 两个客户端能同时连；非交互用户连不上；未知命令被拒；宿主杀进程后客户端 3 s 内显示未运行；UI 以普通用户启动成功 |
| **1. 能力接口 + 三态** | `IMachineProfile` / `ICapabilitySet`（三态 + 可用性 + 原因）/ `IHardwareProvider`（接口族）/ registry（精确→系列→Generic）+ 能力快照 JSON + 缓存键 | 编译 0 错误；单测：匹配/歧义→Generic、三态默认 fail-closed、缓存键含 BIOS/驱动、`Probed` 不可写、`Verified` 才可写；**不改任何硬件行为** |
| **2. Generic provider** | 只读：GPU 传感器（NVML）+ Windows 电源模式（含 overlay scheme）；`PerformixProbe --probe` 输出**脱敏** JSON；在 PH317-55 上验证零写 | `--probe` JSON 通过 schema 校验且脱敏测试通过（无序列号/UUID/MAC/密钥/用户名路径/主机名）；日志证明零写 |
| **3. Acer provider 改造 + 宿主接管** | Acer 实现迁入 `Providers/AcerGamingWmi/`；`HardwareOperationGate` → 宿主 arbiter；失效保护四项上线；服务页 v1（只读检测）；App 品牌字面量基线开始缩 | golden byte 测试全过；风扇/模式/灯效行为与改造前一致；95 °C 硬上限与退出归还实测通过；睡眠唤醒后重新应用通过；`brand-baseline.txt` 变短；服务页能列出 `PSSvc` 且**没有**任何启停按钮 |
| **4. ASUS provider（移植 GHelper）** | 按 D12 移植 `AsusAcpi` → EC/模式/风扇 → RGB/MUX；能力先 `NoWrite`/`Probed`，真机读通后逐项 `Verified`；ASUS 数据文件与证据；至少一台 ROG/TUF 真机 | 真机识别成功；只读遥测正确；模式/风扇写入回读闭环；notices 逐文件登记；README 表由数据生成且信任等级正确（未验收不得写 T2） |
| **5. 第二台真机验证后收紧** | 接口 v2 评审；数据 schema 定版；删单机型预留字段；决定 D10 清单里哪些项要做；服务页是否升到全功能版 | 两个不同接口族真机同时通过；`IHardwareService`/UI 契约未破坏；ADR 追加 Amended；ARCHITECTURE §6 更新 |

**每一步都不做的事**（防止顺序被跳过）：

- 步骤 1-2 不许改 Acer 的协议常量与写路径（先建壳，不动事实）。
- 步骤 3 之前不许写 ASUS 的任何写入代码（读可以先生成探针）。
- 步骤 4 之前不许把 README 表里的 ASUS 行改成「已验证」。
- 步骤 5 之前不许做反射插件 / 全量数据 schema / 服务页全功能版。

---

## 附录 A：能力清单（三态 + 可用性 + 上限）

| 能力轴 | 取值 | 归属 | 默认三态 | 默认等级 |
| --- | --- | --- | --- | --- |
| 模式列表 | `ModeDescriptor[]`（id + 语义 + i18n key） | OEM | `Declared`（数据）+ `Probed`（探测） | `NoWrite` |
| 模式写入通道 | `None` / `OemPipe` / `OemWmi` / `AtkAcpi` / `EcDirect` | OEM | `Declared` | `NoWrite` |
| 风扇控制方式 | `None` / `DutyPercentEc` / `DutyPercentWmi` / `PwmRaw` / `RpmTarget` | OEM | `Declared` | `NoWrite` |
| 风扇回读 | `None` / `Echo` / `Verified`（RPM 独立来源） | OEM | `Declared` | — |
| RGB 类型 | `None` / `EcZoned` / `HidPerKey` / `OemUsb` / `SoftwareCompositor` | OEM / HID | `Declared` | `NoWrite` |
| MUX | `None` / `OemAcpi`（写）/ `GpuApiOnly`（读） | OEM 写 + GPU 读 | `Declared` | `NoWrite` |
| 刷新率控制 | `None` / `WindowsDisplayApi` | OS | `Declared` | `NoWrite` |
| PL1/PL2 驱动需求 | `NotSupported` / `RequiresMsrDriver` / `OemChannel` | CPU | `Declared` | `NoWrite` |
| 欠压驱动需求 | 同上 | CPU | `Declared` | `NoWrite` |
| GPU 调校 | 由 `IGpuControl` 查询（provider 通常 `None`） | GPU | — | `NoWrite` |
| OEM 服务清单 | `OemServiceDescriptor[]` | OEM | `Declared` | 只读 |
| 运行时可用性 | `Available` / `Unavailable` + `Reason`（需接电源 / 需重启 / 被占用 / 竞争工具 / BIOS 锁） | — | `Unavailable` | — |

**三态流转**：

```
Declared ──探测成功──► Probed ──写入+回读通过 / 维护者验收──► Verified
   │                     │                                      │
   └─探测失败/异常──────► Unavailable（原因）◄──写失败/回读不符──┘（降级，本次会话）
```

## 附录 B：`--probe` 能力矩阵 JSON（脱敏 + 信任等级）

> 状态：**未实现**。实现前 README 的手工采集流程等价可用。字段是给人看 + 贴 issue 的，不承诺稳定 schema。
> **必须脱敏**：排除 MSDM（OEM 产品密钥）、SLIC、SMBIOS Type 1/2/3 的序列号与 UUID、MAC、
> 用户名路径、主机名；只保留机型识别所需的厂商/产品串与能力证据。

```jsonc
{
  "schemaVersion": 1,
  "capturedAt": "2026-11-13T18:00:00+08:00",
  "probe": { "tool": "PerformixProbe", "version": "0.1.0-alpha.1", "hostVersion": "…", "elevated": true },
  "machine": {
    "manufacturer": "Acer",
    "product": "Predator PH317-55",
    "board": "RAV4_TLS",
    "biosVersion": "V1.14",
    "biosDate": "2021-07-14",
    "acerSmbiosTag": { "major": 2, "minor": 83, "keyboardZones": 4, "lightbars": false },
    "gpus": [{ "vendor": "NVIDIA", "pciId": "10DE:249D", "name": "RTX 3070 Laptop" }],
    "redactions": ["serial", "uuid", "mac", "msdm", "slic", "hostname", "userPath"]
  },
  "match": { "family": "acer-gaming-wmi", "model": "acer.ph317-55.2021", "quality": "Exact", "dataFile": "data/machines/acer/ph317-55.yaml" },
  "trust": "T2",                       // T0 社区单台 / T1 多台独立 / T2 维护者实机 / Unverified
  "capabilities": {
    "mode": { "state": "Verified", "channel": "OemPipe",
              "modes": [
                { "id": "0x06", "semantic": "Eco", "i18nKey": "mode.eco" },
                { "id": "0x00", "semantic": "Quiet", "i18nKey": "mode.quiet" },
                { "id": "0x01", "semantic": "Balanced", "i18nKey": "mode.balanced" },
                { "id": "0x04", "semantic": "Performance", "i18nKey": "mode.performance" },
                { "id": "0x05", "semantic": "Max", "i18nKey": "mode.beast" }
              ],
              "safety": "WriteWithReadback", "readback": "Verified" },
    "fans": { "state": "Verified", "kind": "DutyPercentWmi", "fanIds": { "cpu": 1, "gpu": 4 },
              "behaviorWords": ["0x410009", "0x820009", "0xC30009"],
              "safety": "WriteWithReadback", "readback": "Echo" },
    "rgb": { "state": "Probed", "kind": "EcZoned", "zones": 4, "safety": "WriteWithReadback", "readback": "Echo" },
    "mux": { "state": "Verified", "kind": "None", "activeMode": -1 },
    "refresh": { "state": "Verified", "kind": "WindowsDisplayApi", "safety": "WriteWithReadback", "rates": [60, 165] },
    "cpuPower": { "state": "Declared", "kind": "RequiresMsrDriver", "safety": "NoWrite" },
    "undervolt": { "state": "Declared", "kind": "RequiresMsrDriver", "safety": "NoWrite" }
  },
  "oemServices": [
    { "id": "pssvc", "name": "PSSvc", "displayName": "Predator Service", "state": "Running",
      "startMode": "Manual", "safety": "AllowlistNeverTouch", "requiredByUs": true,
      "provides": ["sensors", "modeWrite"], "impact": ["modeSwitch", "telemetry"] }
  ],
  "competingTools": { "detected": false, "items": [] },
  "evidence": [
    { "kind": "mof", "ref": "root\\wmi:AcerGamingFunction（方法签名）" },
    { "kind": "acpi", "ref": "DSDT/SSDT 表（脱敏后）" },
    { "kind": "behavior", "ref": "逐模式切换回读记录 + 测试曲线 60 s 转速/温度序列" }
  ],
  "warnings": ["rgb write not verified on hardware", "cpu power needs MSR driver — not supported in no-driver fork"]
}
```

**本仓当前的信任等级不是 T2**：唯一一台实测的 ASUS 机（TUF Gaming A16 **FA607PI**）是 **`T0`**
（单台、单次、**只读**探测；没有写过任何硬件，见 `data/machines/asus-tuf-a16-fa607pi.yaml` 的
`trust.level: T0` 与 [../design/ASUS-ATK-FAMILY.md](../design/ASUS-ATK-FAMILY.md)）。
上面样例里的 `"trust": "T2"` 是 Acer 维护者实机的形态，**不是本仓现在的最高等级**；
T0 数据最多把能力标到 `Probed`，不许标 `Verified`（D9.6 / 附录 A）。

**引导式行为验证**（数据可信度靠它，不靠截图）：

1. 逐模式切换 + 回读（记录切前/切后/回读值，失败也记录）；
2. 应用一条测试曲线，记录 N 秒（建议 60 s）转速 / 温度时间序列，然后**自动恢复**原状态；
3. 每条记录带时间戳与宿主版本；失败时附带错误码。

## 附录 C：接口签名草案

> namespace 仍沿用 `PreySense.*`（第二轮统一改名后再迁移，见 README「已知技术债」#1）。
> 这些是**形状**，不是最终实现；步骤 0-1 落地时允许调整。

**宿主协议（严格类型，无任意命令）**：

```csharp
namespace PreySense.Host.Protocol;

[Flags] public enum HostCommand : ushort
{
    GetSnapshot = 1 << 0,
    SetMode = 1 << 1,
    SetFanDuty = 1 << 2,
    SetFanCurve = 1 << 3,
    SetLighting = 1 << 4,
    SetGpuOffsets = 1 << 5,
    ProbeExport = 1 << 6,
    RestoreFansToFirmware = 1 << 7,
    GetServiceSnapshot = 1 << 8,   // v1：只读；不含启停
}

public sealed record HostRequest(
    ushort ProtocolVersion,
    uint RequestId,
    HostCommand Command,
    byte[] Payload);                // Payload 由各命令的强类型结构体序列化，服务端按命令校验字段

public interface IHostClient
{
    Task<Snapshot> GetSnapshotAsync(CancellationToken ct);
    Task<CommandResult> SetModeAsync(string modeId, CancellationToken ct);      // 服务端校验 id ∈ 数据文件
    Task<CommandResult> SetFanDutyAsync(byte fanId, byte dutyPercent, CancellationToken ct);
    Task<CommandResult> RestoreFansAsync(CancellationToken ct);
    Task<ProbeExport> ProbeExportAsync(CancellationToken ct);                    // 已脱敏
}
```

**能力三态 + 模式描述 + 仲裁**：

```csharp
namespace PreySense.Core.Hardware.Providers;

public enum CapabilityState { Declared, Probed, Verified, Unavailable }
public enum ModeSemantic { Eco, Quiet, Balanced, Performance, Max, Manual }
public enum WriteSafetyLevel { ReadOnly, WriteWithReadback, NoWrite }
public enum ReadbackStrength { None, Echo, Verified }

public sealed record ModeDescriptor(string Id, ModeSemantic Semantic, string DisplayNameKey, bool HotSwitch);
public sealed record Availability(bool Available, string? ReasonKey, string? Detail);

public sealed record Capability<T>(
    T Value, CapabilityState State, Availability Availability,
    WriteSafetyLevel Safety, ReadbackStrength Readback);

public interface ICapabilitySet
{
    IReadOnlyList<ModeDescriptor> Modes { get; }
    Capability<FanCapability> Fans { get; }
    Capability<ModeCapability> ModeWrite { get; }
    Capability<RgbCapability> Rgb { get; }
    Capability<MuxCapability> Mux { get; }
    Capability<RefreshCapability> Refresh { get; }
    Capability<DriverRequirement> CpuPowerLimits { get; }
    Capability<DriverRequirement> CpuUndervolt { get; }
    IReadOnlyList<OemServiceDescriptor> OemServices { get; }
    IReadOnlyList<string> RequiredOemServices { get; }
}
```

**识别 / provider / 仲裁**：

```csharp
public enum MachineMatch { NoMatch, FamilyMatch, ExactMatch }

public sealed record GpuIdentity(string Vendor, string PciId, string Name);

public sealed record MachineIdentity(
    string Manufacturer, string ProductName, string BoardName,
    string BiosVersion, DateOnly? BiosDate,
    AcerSmbiosTag? AcerTag,                 // Providers/AcerGamingWmi 解析；其它家族为 null
    IReadOnlyList<GpuIdentity> Gpus,
    IReadOnlyList<string> WmiInstances);

public enum FanControlKind { None, DutyPercentEc, DutyPercentWmi, PwmRaw, RpmTarget }
public enum ModeWriteChannel { None, OemPipe, OemWmi, AtkAcpi, EcDirect }
public enum RgbKind { None, EcZoned, HidPerKey, OemUsb, SoftwareCompositor }
public enum MuxKind { None, OemAcpi, GpuApiOnly }
public enum RefreshKind { None, WindowsDisplayApi }
public enum DriverNeed { NotSupported, RequiresMsrDriver, OemChannel }

public sealed record FanCapability(FanControlKind Kind, IReadOnlyDictionary<string, byte> FanIds);
public sealed record ModeCapability(ModeWriteChannel Channel);
public sealed record RgbCapability(RgbKind Kind, int Zones);
public sealed record MuxCapability(MuxKind Kind);
public sealed record RefreshCapability(RefreshKind Kind);
public sealed record DriverRequirement(DriverNeed Kind);

public interface IMachineProfile
{
    string ProfileId { get; }               // "acer.ph317-55.2021"
    string Family { get; }                  // "acer-gaming-wmi" / "asus-atk" / "generic"
    MachineMatch Matches(MachineIdentity identity);
}

public interface IHardwareProvider
{
    string Family { get; }
    ICapabilitySet Probe();                 // 运行时探测；失败项标 Unavailable + 原因，不抛
    IFanBackend? Fans { get; }              // 仅在能力状态允许时非 null
    IModeBackend? Modes { get; }
    IRgbBackend? Rgb { get; }
    IMuxBackend? Mux { get; }
    // 没有 GPU 调校后端 —— 见 D8，GPU 走 Core/Gpu 的 IGpuControl
}

public interface IHardwareProviderRegistry
{
    IMachineProfile? Match(MachineIdentity identity, out MachineMatch quality);
    IHardwareProvider Resolve(MachineIdentity identity);    // 无匹配 → GenericProvider
}

public enum HardwareOwner { Telemetry, FanCurve, ModeSwitch, Lighting, GpuTuning, ServicePage, Probe, HostMaintenance }

public interface IHardwareArbiter
{
    // 读租约可共享；写租约独占；拿不到返回 null（不抛、不无限等）
    ValueTask<IAsyncDisposable?> AcquireReadAsync(HardwareOwner owner, TimeSpan timeout, CancellationToken ct = default);
    ValueTask<IAsyncDisposable?> AcquireWriteAsync(HardwareOwner owner, TimeSpan timeout,
        bool preemptible = false, CancellationToken ct = default);
    HardwareOwner? CurrentWriteOwner { get; }
}

public interface IPreemptibleWriteOwner
{
    // 风扇曲线：被抢占时暂停下发、保留目标值；释放后恢复
    ValueTask PauseAsync(HardwareOwner requester, CancellationToken ct = default);
    ValueTask ResumeAsync(CancellationToken ct = default);
}
```

**宿主内失效保护与审计**：

```csharp
public interface IFailsafe
{
    ValueTask RestoreFansToFirmwareAsync(string reason, CancellationToken ct);   // 退出/睡眠/唤醒/温度兜底共用
    ValueTask ReapplyAfterPowerEventAsync(PowerEvent kind, CancellationToken ct);
    void WatchdogTick(TimeSpan now);                                            // 温度硬上限、心跳、租约超时
}

public sealed record AuditEntry(
    DateTimeOffset At, string Client, HardwareOwner Owner, string Operation,
    string Capability, string? Target, string? Value, string? Readback,
    ReadbackStrength ReadbackStrength, WriteSafetyLevel Safety, string Result);

public interface IAuditLog { void Write(AuditEntry entry); }
```

**服务页 v1（只读）**：

```csharp
public enum ServiceSafety { AllowlistNeverTouch, VerifiedSafe, Unverified, Unknown }

public sealed record OemServiceDescriptor(
    string Id, string ServiceName, string BrandKey, ServiceSafety Safety, bool RequiredByUs,
    string? ProcessName, IReadOnlyList<string> ProvidesKeys, IReadOnlyList<string> ImpactKeys,
    string? OurReplacementKey, string? Note);

public sealed record ServiceSnapshot(OemServiceDescriptor Descriptor, bool IsInstalled, bool IsRunning,
    string StartMode, bool HasDependents);

public sealed record CompetitionReport(
    bool OfficialUiRunning, bool DirectEcToolDetected, IReadOnlyList<string> OtherToolIds,
    string? AdviceKey);
```

## 附录 D：现有代码 → 目标归属

| 现在的位置 | 目标归属 | 说明 |
| --- | --- | --- |
| 新 `src/Performix.Host/` | 特权宿主 | 管道服务端、协议、arbiter、failsafe、审计、provider 组装 |
| `Core/Hardware/AcerWmi.cs` / `AcerPipeSensorSource.cs` / `AcerProfileApplyTransaction.cs` / `AcerOperatingModeTiming.cs` / `AcerErm4Table.cs` / `PredatorPipeService.cs` | `Providers/AcerGamingWmi/` | 协议事实；零改写搬家 |
| `Core/Hardware/WmiController*.cs` | Acer 签名进 `Providers/AcerGamingWmi/`；通用 WMI 调用留 `Hardware/` | 拆开 |
| `Core/Hardware/AcerEcLighting*.cs` / `AcerHidLighting*.cs` / `AcerLightingPipeClient.cs` / `AcerCoverLogoHidClient.cs` | `Providers/AcerGamingWmi/Rgb/` | 识别用的 type172 解析进 profile |
| `Core/Hardware/EnekRgbClient.cs` | 先留 `Providers/AcerGamingWmi/` | 若确认是通用 ENE 控制器再上移 |
| `Core/Hardware/AcpiThermalZoneSensor.cs` / `ThermalReadout.cs` / `DeviceHelper.cs` / `HidArrivalHub.cs` / `ThreadAffinityScope.cs` | `Hardware/`（共享） | 非品牌事实 |
| `Core/Hardware/CpuTelemetryReader.cs` / `Intel*.cs` / `CpuProchotController.cs` / `IPhysicalMemoryByteAccess.cs` / `ICpuDriverBackend.cs` / `PreySenseDriver*` | `Hardware/`（CPU 轴 / 驱动 stub） | 按 CPU 供应商分支 |
| `Core/Mode/*` | 保持 `Mode/` | 曲线插值、调度算法通用；品牌差异通过 provider 接口传入 |
| `Core/Services/*` | 保持 `Services/`；新增服务页只读层；`HardwareOperationGate` → 宿主 arbiter | 遥测协调留在客户端侧只读聚合也可以 |
| `Core/Services/AcerServiceDebloater.cs` | v1 只保留只读检测 + 数据文件驱动；启停代码进全功能版附录 | 见 D11 |
| `Core/Gpu/*` | 保持 `Gpu/` | GPU 维度 |
| `Core/Display/*` | 保持 `Display/` | OS 维度 |
| `Core/Diagnostics/AcerSensorProbe.cs` | `Providers/AcerGamingWmi/Diagnostics/` + 通用 `Probe/` | 探针分家族输出 + 脱敏 |
| `src/Performix.App/Services/AcerHotkeyWatcher.cs` | `Providers/AcerGamingWmi/Hotkeys/`（事件仍在客户端订阅，写操作走宿主） | 热键监听可留在非特权客户端，**写入必须过宿主** |
| `src/Performix.App/Services/TurboKeyToggle.cs` / `ShellServices.cs` 的模式字节 | 数据文件（modes）+ 宿主命令 | 语义标签在数据里 |
| `src/Performix.App/Pages/SettingsPage.xaml.cs` 的服务开关 | 服务页 v1（只读） | 品牌字面量清零 |
| `src/Performix.Overlay/WinRing0Cleanup.cs` | 保留（清理历史遗留），并作为 D3 竞争检测的参考实现 | 我们不装，但要清别人装的 |

## 附录 E：术语

| 术语 | 含义 |
| --- | --- |
| 宿主（Host） | 唯一特权进程，独占硬件访问；提供强类型管道协议 |
| provider | 某接口族的实现（Acer Gaming WMI / ASUS ATK / Generic），不按机型分 |
| 能力三态 | `Declared` / `Probed` / `Verified`，另有 `Unavailable`（带原因） |
| 写租约 | 一次写操作的所有权凭据；同一临界域同时只允许一个所有者（宿主内） |
| 失效保护 | 退出/崩溃/睡眠时归还风扇、温度硬上限、审计日志、唤醒重应用 |
| 直接 EC I/O | 绕过 WMI/ACPI 直接端口/物理内存访问 EC 的做法；**本项目永不做** |
| 允许列表 | 服务页可操作的服务白名单（数据文件驱动）；驱动级服务一律不在内 |
| 信任等级 | T2 维护者实机 / T1 多台独立 / T0 社区单台 / 未验证；**本仓当前这台 ASUS FA607PI 是 `T0`（单机只读探测，无写入证据）** |
| Generic | 兜底 provider：GPU 传感器（NVML）+ Windows 电源模式（含 overlay），不碰 OEM 写通道 |

## 附录 F：与其他文档的关系 / 待同步

| 文档 | 关系 |
| --- | --- |
| `README.md`「机型适配 / 适配一款新机型」 | 本 ADR 的用户可见版本（数据驱动表 + 脱敏 + 信任等级 + 新机型工作流） |
| `docs/design/SERVICE-PAGE.md` | D11 的落地设计：v1 只读检测 + 全功能版附录 A |
| `docs/ARCHITECTURE.md` §6 | 本 ADR 取代其结论；实现步骤 3-5 完成后要更新 |
| `THIRD-PARTY-NOTICES.md` §2 | D12 的登记处（每移植一个文件加一行） |
| `docs/LICENSING.md` | GPL-3.0 决定与边界；ASUS 字体等排除项继续有效 |
| `docs/FINDINGS-<MODEL>.md` | 每个机型的实测证据；README 表信任等级的支撑材料 |
| `data/machines/*.yaml` | 机型数据（新机型是数据不是代码）；README 表由它生成 |
