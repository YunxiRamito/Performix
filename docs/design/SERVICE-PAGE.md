# 设置 · 「服务」页设计（OEM 服务检测 / 竞争提示 / 替代管理）

> 状态：设计稿（2026-11-13，外部评审后的 v2）。实现前先读 [ADR-0001 机型适配层](../adr/0001-machine-provider.md)
> 的 D3（特权宿主）与 D11（服务页 v1 范围）。
> 约束：320 DIP 定宽、二级页 520 DIP 封顶、页内滚动；见 [UI-DESIGN-RULES.md](../UI-DESIGN-RULES.md)。

**这一版把范围砍了：**

| 版本 | 范围 |
| --- | --- |
| **v1（本文件 §1-§10）** | **只做检测 + 提示 + 只读观察**：列出本机 OEM 服务状态、检测谁在和我们抢硬件、告诉用户后果，建议用户自己关官方 UI。**不启停服务、不改启动类型。** |
| 全功能版（附录 A） | 启停 + 启动类型切换 + 一键恢复。条件与风险见附录 A，v1 之后单独评审。 |

---

## 1. 为什么 v1 只做只读

三个理由，按重要性排：

1. **停服务的收益远小于代价。** Acer 的模式写入走 `PSSvc` 的管道 cmd 32（`AcerPipeSensorSource`）。
   `PSSvc` 不只属于官方 UI，**它就是我们自己的依赖**：停掉它 → 切模式必失败、温度读数退化到
   ACPI 回退（实测，见 [SETTINGS-IA.md](../SETTINGS-IA.md) §6 第 1 条）。现有
   `AcerServiceDebloater` 的白名单里**没有 `PSSvc`**，而它的 DisplayName 是 `Predator Service`，
   会被「名字含 Predator」的枚举规则收进来 —— 也就是说，现在的「停用 Acer 服务」开关
   本来就能把我们自己的命脉停掉。这就是「为什么不能无脑停服务」的反面案例。
2. **大部分「服务在抢硬件」的问题，关官方 UI 进程就够了**，不需要动服务、不需要管理员写权限、
   不需要处理 Windows Update / Store 自动更新 / BIOS WPBT 把服务装回来的问题。
3. **写服务的代价**是本地提权面：一个能改任意服务启动类型的特权接口，就是本地提权/DoS 跳板。
   v1 干脆不提供这个能力（宿主协议里也没有这些命令）。

> v1 的价值不是「帮你优化」，是**把竞争关系讲清楚**：谁在跑、它提供什么、停掉/关掉会失去什么、
> 我们有没有替代。用户自己决定要不要动，我们不替他动。

---

## 2. 页面位置与信息架构

```
设置（齿轮）
├─ 电池与供电
├─ 外观
├─ 语言（行内下拉）
├─ 开机启动（行内开关）
├─ 快捷栏
├─ 服务            ← 本页（只读）。副标题：`Acer · 1 个官方进程在跑`
├─ 高级            ← 原「停用 Acer 服务」开关删除，功能不再存在
└─ 更新与关于
```

- 二级页高 520 DIP：页头 40 + 竞争检测区 + 服务列表（页内滚动）。
- 页面可见时每 10 s 刷新一次（只读 SCM / 进程查询）；离开页面停止刷新；页头给手动刷新按钮。
- **宿主未运行时**：页面显示「宿主未运行，无法读取硬件状态」+「启动」按钮，不显示旧快照冒充实时。

---

## 3. v1 数据模型（全部只读）

### 3.1 服务描述（来自机型数据文件，不由 UI 写死）

```csharp
// 宿主协议 / Providers 共享（草案，namespace 仍沿用 PreySense.*）
public enum ServiceSafety
{
    AllowlistNeverTouch,   // 我们的依赖 / 驱动级 / 系统关键：只展示，永不操作
    VerifiedSafe,          // 已核实：停掉只影响官方 UI / 功能已由我们替代（全功能版才可操作）
    Unverified,            // 名字/社区信息暗示可停，但本项目未核实
    Unknown                // 没信息：只展示
}

public sealed record OemServiceDescriptor(
    string Id,                        // 稳定 id（"pssvc"），不是服务名字符串，避免协议注入
    string ServiceName,               // 真实服务名（"PSSvc"）
    string BrandKey,                  // i18n key 片段（"acer.predator-sense"）
    ServiceSafety Safety,
    bool RequiredByUs,
    string? ProcessName,              // 官方 UI 进程（只用于提示，不杀）
    IReadOnlyList<string> ProvidesKeys,      // 它提供什么（i18n key）
    IReadOnlyList<string> ImpactKeys,        // 停掉后我们的哪些功能失效（i18n key）
    string? OurReplacementKey,               // 我们的替代实现（i18n key），null = 无替代
    string? NoteKey);                        // 「未核实」等说明
```

### 3.2 运行期快照（v1 只读）

| 字段 | 来源 | 说明 |
| --- | --- | --- |
| `IsInstalled` | 注册表/SCM 读取 | 不存在 → 放「未安装」折叠区 |
| `IsRunning` | SCM 状态 | 运行中 / 已停止 |
| `StartMode` | SCM 配置读取 | `自动 / 手动 / 禁用 / 未知`（只读展示） |
| `HasDependents` | SCM 依赖查询 | 仅用于提示「停它会影响别的服务」，v1 不改任何东西 |
| `Competition` | 进程 + 服务 + 驱动扫描（只读） | 见 §4 |

### 3.3 展示规则

| 情况 | 展示 |
| --- | --- |
| 数据文件声明 + 本机存在 | 正常行（状态、提供什么、影响、替代状态） |
| 数据文件声明 + 本机不存在 | 「未安装」折叠区（默认收起，显示数量） |
| 本机存在但数据文件没声明 | **不显示**（宁可漏，不可猜）；写进诊断日志 |
| `Unknown` / 未核实 | 行内标黄「未核实」，影响一行写「未核实」，不编内容 |
| 我们的依赖（`RequiredByUs` / `AllowlistNeverTouch`） | 置顶「必需」分组，锁图标，**没有操作按钮** |

---

## 4. 竞争检测（v1 的核心功能）

### 4.1 检测什么

| 类别 | 检测对象（示例） | 检测方式（只读） | 命中后的说法 |
| --- | --- | --- | --- |
| 官方 UI / 代理进程 | `PredatorSense.exe`、`PSAgent.exe`、`PSAdminAgent.exe`（Acer）；Armoury Crate / ASUS 相关系列（ASUS，待核实） | 进程名/路径匹配（数据文件驱动） | 「官方控制中心在运行，可能抢模式写入 / 风扇控制」 |
| **直接 EC I/O 工具** | WinRing0 类驱动（`WinRing0*`、`R0*` 服务/文件）、ThrottleStop、Intel XTU、其它厂商风扇工具 | 服务/驱动存在性 + 进程名 | 「检测到直接访问硬件的工具。我们已把相关写入降为只读，避免互相踩 EC」 |
| 其它性能工具 | 数据文件维护的名单（可更新） | 进程名 | 「可能同时写性能模式」 |
| 我们的宿主 / 客户端 | 宿主在跑、UI 在跑 | 单实例 mutex / 管道 | 不进「竞争」区（这是我们自己） |

> 检测名单放**数据文件**（不硬编码在 UI）；数据文件可随版本更新。**只提示，不自动杀进程**
> （v1 明确不做杀进程，避免误杀和权限滥用）。

### 4.2 与能力层的联动（重要）

- 检测到**直接 EC I/O 工具**时：宿主把「风扇/模式/灯效」的写入能力降为 `ReadOnly` 或 `NoWrite`，
  UI 在相关功能页显示禁用 + 原因（「检测到 WinRing0 类工具，已暂停写入」）。
- 检测到官方 UI 在跑：**不降级**，只提示（它走的是和我们相同的 WMI/管道，总线层会串行化；
  真正的风险是超时，不是写坏 —— 超时由写入的重试纪律兜底）。
- 解除竞争后（工具退出）重新探测能力，恢复可用；**不自动写**，等用户操作。

---

## 5. 线框图（320 DIP，v1）

```text
┌──────────────────────────── 320 DIP ─────────────────────────────┐
│ ‹  服务                                    Acer  ·  只读观察    ⟳ │ 40  页头（返回 / 标题 / 品牌 / 刷新）
├──────────────────────────────────────────────────────────────────┤
│ ⚠ 官方控制中心正在运行                                            │ 56  InfoBar（竞争提示）
│   PredatorSense.exe 可能和我们抢模式写入。调模式前建议先退出它。   │
│   本页不会结束进程，也不改服务。                [ 怎么办？ ]       │
├──────────────────────────────────────────────────────────────────┤
│ 竞争检测                                                          │ 24  分组标题
│ ● 官方 UI / 代理进程        运行中 1 个     PredatorSense.exe      │ 20
│ ○ 直接硬件访问工具          未检测到                              │ 20
│ ○ 其它性能工具              未检测到                              │ 20
├──────────────────────────────────────────────────────────────────┤
│ 本机 OEM 服务（只读）                                             │ 24
│ ┌──────────────────────────────────────────────────────────────┐ │
│ │ 🔒 Acer Predator Sense Service             ● 运行中 · 手动   │ │ 20
│ │    PSSvc · 必需（我们的通道）                                 │ │ 16
│ │    提供：温度 / 转速、性能模式写入（管道 cmd 15/32/33）       │ │ 16
│ │    影响：停掉它，模式写入与遥测都会断（实测）                 │ │ 16
│ └──────────────────────────────────────────────────────────────┘ │
│ ┌──────────────────────────────────────────────────────────────┐ │
│ │ ○ Acer Lighting Service                    ○ 已停止 · 手动   │ │ 20
│ │    AcerLightingService                                        │ │ 16
│ │    提供：键盘 / 灯条灯效（未核实）                            │ │ 16
│ │    影响：未核实（我们走 EC 直驱，是否互斥未验证）             │ │ 16
│ └──────────────────────────────────────────────────────────────┘ │
│ ▸ 其它已知但本机未安装的服务（5）                                 │ 24  折叠
├──────────────────────────────────────────────────────────────────┤
│ 本页只观察，不启停服务、不改启动类型。                            │ 32  固定页脚说明
└──────────────────────────────────────────────────────────────────┘
```

空态（`Generic` / 未适配机型 / 宿主未运行）：

```text
┌──────────────────────────── 320 DIP ─────────────────────────────┐
│ ‹  服务                                                         │
├──────────────────────────────────────────────────────────────────┤
│   没有识别到这台机器的 OEM 服务（当前按通用机型运行）。           │
│   通用机型不做写入，也就不需要和 OEM 服务竞争。                   │
│                                                                  │
│   [ 复制诊断信息 ]                                               │
├──────────────────────────────────────────────────────────────────┤
│ 宿主未运行：无法读取服务状态。              [ 启动 Performix ]   │
└──────────────────────────────────────────────────────────────────┘
```

---

## 6. 行内容：它提供什么 / 对我们的影响

每一行**必须**有这两句话，不知道就写「未核实」，不允许空、不允许编。

### 6.1 Acer（本机只为 PH317-55 核实过 `PSSvc`）

| 服务 | 它提供什么 | 停用影响（对我们） | 我们的替代 | 建议等级 |
| --- | --- | --- | --- | --- |
| `PSSvc`（`Predator Service`） | 传感器读数（温度/转速）+ 性能模式写入与回读（管道 cmd 15/32/33） | **必需**：切模式失败、温度读数退化到 ACPI 回退（实测） | 无法替代（它是我们要用的通道本身） | `AllowlistNeverTouch` |
| `PredatorSense`（进程，不是服务） | 官方控制中心 UI + 风扇曲线 UI | 可能抢模式写入；关闭它无损失（我们自己就是替代前端） | 我们（全量替代） | 提示关闭，本页不展示操作 |
| `AcerLightingService` | 键盘 / 灯条灯效（推断，未核实） | 未核实：至少官方灯效控制失效；与我们 EC 直驱是否互斥未验证 | 部分（EC 直驱 / HID） | `Unverified` |
| `AcerQAAgentSvis` | Fn 热键 / 快捷功能（推断，未核实） | 未核实：可能影响 Fn 键原生行为 | 部分（热键监听） | `Unverified` |
| 其它 Acer 服务（`AcerDIAgentSvis` / `AcerCCAgentSvis` / `AcerDeviceEnablingServiceV2` / `AcerServiceSvc` / `ASMSvc` / `AASSvc` / `AICO_svc` / `AICO2_svc` / `AcerSysHardwareService` / `AcerHardwareService` / `AcerCentralService`） | 名字暗示 Care Center / 设备使能 / 语音 / 中央服务，**均未核实** | 未核实 | 我们不替代更新/商店/语音类功能 | `Unknown` |

> 服务名清单来自现有 `AcerServiceDebloater.ManagedServices`（代码），**不代表我们知道它们干什么**；
> 进入数据文件时必须逐项标状态与证据。

### 6.2 ASUS

- ASUS 的服务/进程清单在移植 GHelper 时按上游代码逐个核实后写进 ASUS 数据文件。
- 核实之前 ASUS 的服务清单为空，页面显示「本机未核实到可管理的 ASUS 服务」。
- 不要把「Armoury Crate 相关的服务」整包当同类；逐个写它提供什么、我们替代什么。

---

## 7. 宿主与权限

- 服务状态读取走**宿主协议**（`GetServiceSnapshot` 只读命令），客户端（UI）不提权。
- 宿主侧对服务读取同样按**允许列表**：只接受数据文件里声明的服务 `Id`，
  不接受任意服务名 → 不存在「给个名字就查」的注入面（ADR D3 / C9）。
- 宿主未运行 / 管道断开：页面显示空态 + 「启动宿主」，不缓存旧数据当实时。
- 本页**没有任何需要管理员权限的操作**（v1 全部只读）。这也是它能安全上线的关键。

---

## 8. 文案与语言（JSON + ICU，全应用通用）

语言包改为**外壳 JSON + 消息 ICU MessageFormat**（理由与完整规范见附录 B）。

本页用到的 key：

| 前缀 | 用途 | 示例 |
| --- | --- | --- |
| `service.page.*` | 页面标题 / 页脚说明 / 空态 | `service.page.footer` = `This page only observes. It does not start, stop or reconfigure services.` |
| `service.group.*` | 分组标题 | `service.group.required` = `Required (cannot be stopped)` |
| `service.state.*` | 状态词 | `service.state.running` = `Running` |
| `service.startType.*` | 启动类型 | `service.startType.manual` = `Manual` |
| `service.competition.*` | 竞争检测 | `service.competition.officialUi` = `Official control centre is running` |
| `service.competition.directEc` | 直接 EC I/O 工具 | `service.competition.directEc` = `A tool with direct hardware access was detected. Writes are paused.` |
| `service.impact.*` | 影响文案 | `service.impact.unverified` = `Not verified — the effect on this machine is unknown.` |
| `service.brand.<brandKey>.*` | 品牌服务文案 | `service.brand.acer.pssvc.provides` |
| `service.error.*` | 错误 | `service.error.hostNotRunning` |

规则：

- **服务名 / DisplayName / 状态值是数据，不是 key**；「提供什么 / 影响什么 / 建议」才是 key。
- 占位符用 **`{name}`**，禁止 `{0}`；复数用 ICU（`{count, plural, one {...} other {...}}`）。
- key 由 **source generator 从 `en-US` 生成强类型常量**，拼错编译失败。
- 不许在 XAML / 代码里写中文字面量；连「未核实」也要走 key。
- 新增服务 = 新增数据 + key；en-US 缺 key 时测试失败，其它语言允许缺并回落 en-US。

---

## 9. 验收清单（v1）

| # | 检查项 | 判定 |
| --- | --- | --- |
| 1 | 页面/宿主代码**没有任何** SCM 写调用（启停、改启动类型、删服务） | 源码扫描 + 代理审计 |
| 2 | `PSSvc` 等依赖项显示为「必需」且没有任何操作入口 | 截图 + 协议测试（未知命令被拒） |
| 3 | 服务状态与 `sc query` 一致；不存在的进「未安装」折叠区 | 手测对照 |
| 4 | 数据文件没声明的服务**不显示**（但写诊断日志） | 注入测试 |
| 5 | 竞争检测：官方 UI 命中 → InfoBar；WinRing0 类工具命中 → 能力降级 + 原因 | 桩测试 + 手测 |
| 6 | 检测到直接 EC I/O 工具后，风扇/模式写入确实被宿主拒绝 | 协议测试 |
| 7 | 宿主未运行：空态 + 启动按钮，不显示旧数据 | 手测 |
| 8 | 页面可见才刷新（10 s），离开即停；无 WMI/SCM 常驻轮询 | 日志计数 |
| 9 | 文案全走 key；en-US 完整、ICU 占位符各语言一致 | 本地化测试 |
| 10 | 320 DIP / 200% 缩放 / 长语言（hi、fr）不裁切、可滚动 | 截图 |
| 11 | 键盘可达、讲述人可读（状态 + 影响 + 原因） | 手测 |
| 12 | 诊断导出已脱敏（无序列号 / UUID / MAC / 密钥 / 用户名路径 / 主机名） | 单测 |
| 13 | 本页不出现品牌名硬编码；品牌 chip 来自数据 | 架构扫描测试 |

---

## 10. v1 明确不做

- 不启停服务、不改启动类型、不删服务（全功能版才做，见附录 A）。
- 不杀进程（官方 UI 关闭由用户自己决定）。
- 不做「一键优化」「节省 n MB」「释放内存」这类不可验证的说法。
- 不因为名字像 OEM 就信它可停（现有 debloater 的名字匹配就是教训）。
- 不把服务页放进快捷栏 / 首页；它是排障页，不是日常功能。

---

## 附录 A：全功能版（启停 + 启动类型）——v1 之后单独评审

> **前提**：只有 v1 上线、竞争检测与能力降级跑稳之后才考虑。实现时必须满足下面全部条件。

### A.1 允许列表（不是白名单）

| 规则 | 说明 |
| --- | --- |
| 只动「整理过的 OEM 用户态服务」 | 每条要有厂商 + 版本 + 证据 + 信任等级；**驱动级服务一律不碰**（`AcerApplicationBaseDriver_Device` 这类连展示都不给操作） |
| 允许列表在 Core / 数据文件 | 只增不减；provider 只能加自己的项；UI 只能看到已列出的服务 |
| 协议参数是稳定 `Id` | 不接受任意服务名/注册表路径（ADR D3 / C9） |
| `PSSvc` 永久拒绝 | `RequiredByUs` 的服务没有操作入口；宿主侧二次校验 |
| 默认动作 = **运行时暂停（stop）**，不改启动类型 | 重启后自然恢复，异常退出也不会留下永久状态 |
| 永久禁用是「高级」动作 | 必须记录 `StartType` / `DelayedAutoStart` / 失败恢复动作 / 触发器，恢复时逐项写回 |

### A.2 操作与二次确认

| 动作 | 确认框 | 样式 |
| --- | --- | --- |
| 运行时暂停服务 | 需要：服务名 + 启动类型 + 它提供什么 + 停用影响 + 「重启后自动恢复」 | 红色文字按钮 |
| 恢复运行 | 不需要 | 普通按钮 |
| 永久禁用（高级） | 需要 + 红色 + 明确写「重启后仍然禁用」+ 列出恢复方法 | 红色 |
| 恢复所有服务 | 需要 + 列出即将恢复的项 + 逐项回读结果 | 红色文字按钮 |

### A.3 永久禁用必须记录的完整状态

```jsonc
{
  "service": "AcerQAAgentSvis",
  "startTypeBefore": "Auto",           // Boot/System/Auto/Manual/Disabled
  "delayedAutoStartBefore": false,
  "failureActionsBefore": "restart:60000/restart:60000//",  // SCM 失败恢复动作
  "triggersBefore": ["..."],            // 服务触发器
  "wasRunningBefore": true,
  "changedAt": "2026-11-13T18:00:00+08:00",
  "restoredAt": null
}
```

- 状态文件：`%LOCALAPPDATA%\Predex\oem-services.json`（沿用现有内部标识兼容面）。
- 恢复时**写回原值**，不是「一律 auto」（现有 `EnableAndStartAll()` 的 bug 就是一律 auto）。
- 只走 SCM（`ChangeServiceConfig`），**不直接写注册表 `Start` DWORD**
  （现有 `SetServiceStartModeRegistry` 的 bug：手动(3) 表达不出来、绕过 SCM、不通知）。

### A.4 会「装回来」的东西（必须处理）

| 场景 | 行为 | 处理 |
| --- | --- | --- |
| Windows Update 装回 OEM 组件 | 服务/进程重新出现 | 页面提示「官方组件已恢复」，不自动再停 |
| Microsoft Store 自动更新官方 App | 同上 | 同上 |
| BIOS WPBT 在开机时注入 | 服务重新出现/启用 | 同上；这类情况**不承诺能永久禁用** |
| 用户手动重装官方控制中心 | 同上 | 同上 |
| 卸载 Performix | **必须恢复** | 安装器 / `scripts\Restore-OemServices.ps1`；便携版在「更新与关于」页给入口 |

### A.5 失败与回滚

| 场景 | 处理 |
| --- | --- |
| 有依赖者（`HasDependents`） | 拒绝停止，说明依赖它的服务 |
| 停止超时（5 s） | 回读；仍运行 → 回滚启动类型并提示 |
| 写入启动类型失败 / 权限不足 | 回滚到原值，提示权限问题 |
| 回读不一致 | 回滚（启动类型 + 尝试启动），提示「操作未生效，已恢复原状」 |
| 服务不存在（改名/升级） | 移进「未安装」，不报错 |
| 非管理员 | 全部禁用 + 提权入口（全功能版才需要管理员） |
| 连续失败 2 次 | 停止自动重试，提示看审计日志 |

### A.6 线框图（全功能版，供参考）

```text
┌──────────────────────────── 320 DIP ─────────────────────────────┐
│ ‹  服务                                    Acer  ·  2 项运行中   │
├──────────────────────────────────────────────────────────────────┤
│ 必需（不可停用）                                                  │
│ 🔒 Acer Predator Sense Service       ● 运行中 · 自动 · 无操作入口 │
│    提供：传感器 + 模式写入；影响：必需（实测）                    │
├──────────────────────────────────────────────────────────────────┤
│ 可暂停（重启后自动恢复）                                          │
│ ○ Acer Lighting Service              ○ 已停止 · 手动              │
│    提供：灯效（未核实）；影响：未核实                             │
│    [ 启动 ]                                                       │
│ ● Acer Quick Access Agent            ● 运行中 · 自动              │
│    提供：Fn 热键（未核实）；影响：可能影响 Fn 原生行为            │
│    [ 暂停（二次确认）]   [ 高级：永久禁用… ]                      │
├──────────────────────────────────────────────────────────────────┤
│ 危险操作                                                          │
│ [ 恢复所有服务为原启动类型 ]                                      │
└──────────────────────────────────────────────────────────────────┘
```

### A.7 测试（全功能版）

- 允许列表：未列出的服务无法通过协议操作；`PSSvc` 永远拒绝。
- 三态启动类型往返（Auto/Manual/Disabled）+ `DelayedAutoStart` + 失败恢复动作 + 触发器原值恢复。
- 回读不一致 → 自动回滚。
- 有依赖者拒绝停止。
- 卸载/恢复脚本后与初始状态逐项一致。
- Windows Update / Store / WPBT 模拟「装回来」→ 页面提示而不自动再停。

---

## 附录 B：语言包格式（全应用通用）

**结论**：外壳用 **JSON**，消息内容用 **ICU MessageFormat**；不再用「XML + 键值对」的外壳。

| 项 | 决定 | 理由 |
| --- | --- | --- |
| 外壳格式 | **JSON**（`strings/<lang>.json`） | Weblate / Crowdin 原生支持；AI 与译者都熟；工具链成熟 |
| 消息格式 | **ICU MessageFormat** | 复数/选择/性别/嵌套都能表达；不需要自己发明 |
| 占位符 | **`{name}`**，禁止 `{0}` | 位置参数一改顺序就错位；具名参数可读、可校验 |
| 强类型 key | source generator 从 `en-US` 生成 `LocKeys.*` 常量 | 拼错编译失败；这是 CI 最常漏的一类错误 |
| 安全 | **禁用 DTD**（防 XXE / billion laughs）；限制文件大小；只接受 UTF-8 | 第三方语言包是不可信输入 |
| 第三方语言包 | 限制大小、标注「第三方」、单条失败回退 `en-US` | 一条坏翻译不能拖垮整个 UI |
| 伪本地化 | 加一个 `qps-ploc`（伪本地化）语言做截断/布局检查 | 长文本问题必须自动化发现，不能靠肉眼 |
| RTL | 预留（布局支持 RTL），**v1 不承诺**完整 RTL | 没有 RTL 语言验收，不吹 |
| 热加载 | **v1 不做**：切语言重建窗口/客户端，必要时提示重启 | 避免资源字典与宿主快照的竞态 |

示例（`strings/en-US.json`）：

```jsonc
{
  "$schema": "./strings.schema.json",
  "service.page.title": "Services",
  "service.page.footer": "This page only observes. It does not start, stop or reconfigure services.",
  "service.state.running": "Running",
  "service.competition.roles": "{officialUi}, {directEcTool} and {otherTool}",
  "service.competition.officialUi": "Official control centre: {count, plural, one {# process running} other {# processes running}}",
  "service.impact.unverified": "Not verified — the effect on this machine is unknown.",
  "service.brand.acer.pssvc.provides": "Sensors and performance-mode switching (pipe cmd 15/32/33)",
  "service.brand.acer.pssvc.impact": "Required: stopping it breaks mode switching and telemetry."
}
```

规则：

- `en-US` 是**唯一基准**，缺 key 的构建/测试失败；其它语言允许缺，运行时回落 `en-US`。
- 服务名 / DisplayName / 状态值是数据；「提供什么 / 影响什么 / 建议」是 key。
- 品牌相关 key 放 `service.brand.<brandKey>.*`，由数据文件的 `BrandKey` 组装。
- 测试：key 完整性、ICU 解析、占位符集合一致、伪本地化不裁切、第三方包单条坏数据回落。
