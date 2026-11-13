# 机型数据（`data/machines/`）

> 状态：**schema v1（草案）**，2026-11-13 随第一台真机数据（ASUS TUF Gaming A16 FA607PI）落地。
> 上位决策：[ADR-0001 机型适配层](../../docs/adr/0001-machine-provider.md) D9「**新机型是数据，不是代码**」。
> 接口族参考：[`docs/design/ASUS-ATK-FAMILY.md`](../../docs/design/ASUS-ATK-FAMILY.md)。
> 这个目录里**只放数据**：不放代码、不放二进制、不放截图、不放未脱敏的报告。

---

## 1. 这份数据是什么

- **一台（一组精确识别串相同的）机型的声明 + 证据。** 一个 YAML 文件对应一个 `id`。
- 它是三类读取方的**唯一数据源**：
  1. README 的「品牌-机型适配表」（由 `scripts\generate-machine-table.ps1` 生成，**生成器待实现**）；
  2. 宿主 / provider 的**能力初始声明**（`Declared` 层，ADR D6）；
  3. 设置页的**服务 / 竞争检测**清单（ADR D11 的服务页 v1）。
- **它不是实测日志。** 写清楚「探测到了什么、没探测到什么」比写「支持什么」重要；
  没探测到的能力必须留 `unknown`，不许为了表好看写成 `probed` / `verified`。

**三条硬约束（违反即数据缺陷）：**

1. 探测没给结论的，一律 `unknown` / `null` —— **不编造**字节、返回值、转速、模式 id。
2. 数据里每个 `verified` / `write_verified: true` 都必须有真机写入 + 回读证据；
   只有维护者实机（`T2`）才能这样写（ADR D9.6）。
3. 数据文件与 README 表必须一致；生成器实现前，PR 改表必须同时改数据（手工只改 README = 错误）。

---

## 2. 文件与命名

| 规则 | 说明 |
| --- | --- |
| 路径 | `data/machines/<id>.yaml` |
| 文件名 | 必须**等于** `id`（生成器与校验器按 `id` 找文件，不猜目录） |
| `id` 格式 | 小写字母 / 数字 / 连字符，`品牌-系列-型号`，例如 `asus-tuf-a16-fa607pi` |
| 唯一性 | `id` 全局唯一；同一底盘不同型号**各开一个文件**，不要在一个文件里堆多台 |
| 编码 | UTF-8；键名英文小写 + 下划线；值可以是中文（`note` / `known_gaps`） |
| `schema` | 必填，当前为 `1`；加**可选**字段不升版，改必填字段或语义才升版并写迁移说明 |

> 分层写法：ADR D9.3 写的是 `data/machines/<brand>/<model>.yaml`。当前 v1 用**扁平** `<id>.yaml`
> 落地（与分层等价，只是少一层目录）；任何工具都必须认 `id`，不要依赖目录层级。

---

## 3. 字段总览

必填 = 缺了整个文件无效；可选 = 没有就按 `unknown` / 空处理。

| 字段 | 必填 | 类型 | 含义 | 进 README 表哪列 |
| --- | --- | --- | --- | --- |
| `schema` | ✅ | int | 数据 schema 版本，当前 `1` | — |
| `id` | ✅ | string | 稳定机型 id，等于文件名 | 状态列附注（数据文件路径） |
| `brand` | ✅ | string | 品牌显示名（`ASUS` / `Acer` / …） | 品牌 |
| `series` | ✅ | string | 系列名（`TUF Gaming A16`） | 系列 / 机型 |
| `display_name` | ✅ | string | 机型全名（`TUF Gaming A16 FA607PI`） | 系列 / 机型 |
| `identity` | ✅ | map | 识别与展示用的不可变标识（见 §4.1） | 系列 / 机型、识别方式 |
| `interface_family` | ✅ | enum | 接口族（见 §6） | 识别方式（间接） |
| `host` | ✅ | map | 宿主驱动 / WMI 实例 / 不存在的类（见 §4.2） | 识别方式、依赖服务 |
| `identification` | ✅ | map | 识别信号与缺失项（见 §4.3） | 识别方式 |
| `capabilities` | ✅ | map | 每项能力的状态 / 通道 / 等级（见 §5） | 模式、风扇、键盘灯、GPU 超频、MUX、刷新率 |
| `modes` | 可选 | map | 模式档位列表（id + 语义）与状态（见 §4.4） | 模式 |
| `fans` | 可选 | map | 风扇 id / 单位 / 回读方式与状态（见 §4.5） | 风扇 |
| `services` | ✅ | list | OEM 服务 / 驱动清单（见 §4.6） | 依赖服务 |
| `competitors` | ✅ | list | 会抢硬件的进程 / 任务 / 服务 / 启动项（见 §4.7） | 依赖服务、状态 |
| `status` | ✅ | enum | 适配状态（见 §4.8） | 状态 |
| `status_note` | 可选 | string | 状态的一句话补充（中文） | 状态 |
| `trust` | ✅ | map | 信任等级与写入验证（见 §7） | 信任等级 |
| `known_gaps` | ✅ | list[string] | 已知缺口（探测拿不到 / 红线不做），必须写清楚**缺什么** | 状态（间接）、证据链 |
| `evidence` | ✅ | map | 证据引用与 DSDT 摘要（见 §4.9） | 状态（间接） |

---

## 4. 顶层字段详解

### 4.1 `identity`

| 键 | 类型 | 说明 |
| --- | --- | --- |
| `vendor` | string | SMBIOS 厂商串（`ASUSTeK` 这类 OEM 写法）；没采集就 `null` |
| `model` | string | 型号（产品串）；没采集就 `null` |
| `cpu` | string | CPU 型号（显示用） |
| `gpu` | string | 独显型号（显示用） |
| `bios` | string | BIOS 版本串 |
| `smbios_family` | string \| null | SMBIOS Family 串；没采集必须 `null`，**不许猜** |

### 4.2 `host`

| 键 | 类型 | 说明 |
| --- | --- | --- |
| `driver` | string | 宿主驱动名（`ASUS System Control Interface v3`） |
| `driver_version` | string | 驱动版本（参与能力缓存键，ADR D6） |
| `driver_state` | enum | `running` / `stopped` / `unknown` |
| `wmi_instances` | list[string] | 已枚举到并活跃的实例路径 |
| `absent_classes` | list[string] | **确认不存在**的类名（写下来，避免后来的人重复去找） |
| `driver_role` | string | 可选：驱动角色（如 `wmi-host-driver`），未知就不写 |

### 4.3 `identification`

| 键 | 类型 | 说明 |
| --- | --- | --- |
| `match_quality` | enum | `exact` / `family` / `unassessed`（对应 ADR D1 的 `ExactMatch` / `FamilyMatch` / 未判定） |
| `signals` | list[map] | 识别信号，每项 `kind` + `value`（+ `instance` / `version` 等） |
| `missing` | list[string] | 识别所需但**没采集**的字段（如 `smbios_manufacturer`） |

`signals[].kind` 允许值：`bios_string`、`wmi_class`、`host_driver`、`smbios_manufacturer`、
`smbios_product`、`smbios_board`、`pci_id`、`acpi_uid`。新增 kind 先改本节。

### 4.4 `modes`（可选）

| 键 | 类型 | 说明 |
| --- | --- | --- |
| `state` | enum | `declared` / `probed` / `verified` / `unknown` / `unsupported`（同 §5.2） |
| `values` | list[map] | 每项 `{ id, semantic, i18n_key? }`；ADR D9.3 要求 `id` + 语义 + 显示名 key |
| `note` | string | 说明证据与不确定项 |

`semantic` 取值按 ADR D1：`Eco` / `Quiet` / `Balanced` / `Performance` / `Max` / `Manual`。
**没有探测到档位字节就写 `values: []`，不许填占位 id。**

### 4.5 `fans`（可选）

| 键 | 类型 | 说明 |
| --- | --- | --- |
| `state` | enum | 同 §5.2 |
| `fan_ids` | map \| null | 例如 `{ cpu: 1, gpu: 4 }`；未知写 `null` |
| `unit` | string \| null | `duty_percent` / `pwm_raw` / `rpm_target`；未知 `null` |
| `rpm_readback` | string \| null | `none` / `echo` / `verified`；未知 `unknown` |
| `note` | string | 证据与缺口 |

### 4.6 `services`

每项：

| 键 | 必填 | 说明 |
| --- | --- | --- |
| `id` | ✅ | 稳定 id（服务名或短 id） |
| `kind` | ✅ | `service` / `driver` / `scheduled_task` |
| `state` | ✅ | `running` / `ready` / `disabled` / `stopped` / `empty` / `unknown` |
| `required_by_us` | ✅ | `true` / `false` / `unknown`；**没做过停用对照就写 `unknown`** |
| `name` / `version` / `role` | — | 可选补充（驱动角色、显示名、版本） |
| `note` | — | 可选；写清「停掉会不会影响我们」 |

### 4.7 `competitors`

每项：

| 键 | 必填 | 说明 |
| --- | --- | --- |
| `id` | ✅ | 任务名 / 服务名 / 启动项标识（或 `startup-surfaces` 这类聚合 id） |
| `kind` | ✅ | `scheduled_task` / `service` / `process` / `app` / `startup_entries` |
| `state` | ✅ | `running` / `ready` / `disabled` / `empty` / `unknown` |
| `trigger` / `principal` / `run_level` | — | 计划任务用：`logon` / `boot`；`Interactive` / `S4U`；`highest` / `limited` |
| `config` | — | （可选）竞争工具的配置快照：`path` / `keys` / `groups` / 其它。只作证据用 |
| `note` | — | 竞争风险说明；**「它随时可能回来抢通道」要写在这里** |

> 竞争检测的意义是 ADR D11 的服务页 v1：只检测 + 提示，不杀进程、不停服务。
> 写明确切的任务名 / 服务名，别写「奥创相关服务」这种模糊描述。

### 4.8 `status`

| 值 | 含义 | README 表「状态」列建议文案 |
| --- | --- | --- |
| `verified` | 真机闭环过（写 + 回读归档） | `✅ 已验证（本机）` |
| `read-only-probed` | 只读探测完成，**写入未验证** | `只读探测完成（远程真机）· 写入未验证` |
| `unverified` | 只有上游 / 社区说法，本项目没实测 | `未验证` |

状态是**人读的结论**；机器判定看 `capabilities[*].state` 与 `trust.write_verified`。

### 4.9 `evidence`

| 键 | 必填 | 说明 |
| --- | --- | --- |
| `probe` | ✅ | 仓库相对路径，指向接口族参考 / 探测记录（必须有对应文件） |
| `probe_date` | 可选 | `YYYY-MM-DD` |
| `dsdt` | 可选 | `{ size, oem_id, retrieval, dsts_at, devs_at, wmnb_at }`；`retrieval` 记录是否落盘 |
| `write` | ✅ | `none`（默认）或写入证据的路径 / 列表 |

> `evidence` 里的路径必须是**仓库内相对路径且文件存在**，CI 会校验；不要把 `%TEMP%`、`C:\Users\…`
> 这类本机路径写进来。

---

## 5. `capabilities`

### 5.1 结构

```yaml
capabilities:
  <能力键>:
    state:  declared | probed | verified | unknown | unsupported   # 必填
    path:   DEVS | DSTS | NVAPI | windows-display-api | ...        # 可选：读/写通道
    safety: ReadOnly | WriteWithReadback | NoWrite                 # 写能力必填；缺省按 NoWrite
    readback: None | Echo | Verified                               # 可选
    note:   一句话，必须写清「证据是什么、不是什么」
```

### 5.2 `state` 取值（ADR 三态 + 两个数据侧标记）

| 值 | 含义 | 允许写？ |
| --- | --- | --- |
| `declared` | 数据声明「这台机器应该有」，但本次没有本机探测证据 | ❌ |
| `probed` | 非破坏性探测到了对象 / 通道（类、方法、实例、当前值），**不代表写入验证过** | ❌ |
| `verified` | 写入后回读通过，或维护者实机验收（只有 `trust.level: T2` 能标） | ✅ |
| `unknown` | 探测没有结论：没采集 / 访问被拒 / 没做 | ❌ |
| `unsupported` | 明确不支持，或项目红线不做（如 MSR 写入） | ❌ |

- 前三个是 ADR D6 的**三态**；`unknown` / `unsupported` 是**数据文件侧标记**，
  运行时一律映射为 `Unavailable` + 原因（ADR D6 fail-closed），不参与写入判定。
- **降级永不升级**：失败只能往「更保守」的标记改，不能把 `declared` 写成 `probed` 充数。

### 5.3 能力键词表（固定）

| 键 | 对应 README 列 | 说明 |
| --- | --- | --- |
| `mode_set` | 模式 | 性能模式写入通道 |
| `fan_curve` | 风扇 | 风扇曲线 / 占空比写入 |
| `fan_rpm_read` | 风扇 | 转速读取来源 |
| `rgb` | 键盘灯 | 键盘灯 / 灯效 |
| `gpu_clock_oc` | GPU 超频 | NVAPI 时钟偏移（ADR D8：不归接口族） |
| `mux` | MUX | MUX 读写 |
| `refresh_rate` | 刷新率 | Windows 显示 API |
| `charge_limit` | 依赖服务（间接） | 充电阈值 |
| `pl_write` | — | CPU PL 写入（no-driver fork 下恒 `unsupported`） |

> 新增能力键 = 同时改 provider 的 `ICapabilitySet`（ADR 附录 C）与本节；
> 数据文件不许自造键名，否则生成器不认。ADR D10 明确不许把能力扩成几十个 bool。

### 5.4 写入等级规则（会进 CI）

- `safety` 取值只有 `ReadOnly` / `WriteWithReadback` / `NoWrite`；缺省 `NoWrite`。
- **`state != verified` 的能力，`safety` 不得是 `WriteWithReadback`**（fail-closed）。
- 写能力必须显式写 `safety`；只读能力可省略。
- `readback` 只在有写能力时有意义：`Echo` 只能用于可恢复调参，关键操作（模式切换）必须 `Verified`。

---

## 6. `interface_family`

| 值 | 覆盖 | 目标 provider 目录 | 现状 |
| --- | --- | --- | --- |
| `acer-gaming-wmi` | Acer `AcerGamingFunction` 家族 | `Providers/AcerGamingWmi/` | Acer 代码已在仓库（当前仍在 `Core/Hardware/Acer*`），迁入 provider 目录属 ADR 步骤 3 |
| `asus-atk` | ASUS ATK WMI（`WMNB` / `DSTS` / `DEVS`） | `Providers/AsusAtk/` | **数据先行**（ADR 步骤 4 未开始） |
| `generic` | 兜底：NVML + Windows 电源模式 | `Providers/Generic/` | ADR 步骤 2 计划中；**不进 README 表** |

- 一个字符串只对应一个 family，**不许出现同义变体**（`asus-atk-wmi`、`ASUS_ATK` 之类）。
- 新增 family 要同时改：ADR D9.1、本表、provider 目录；只在数据文件里写一个新字符串 = 错误。

---

## 7. `trust`

### 7.1 等级定义（来自 ADR D9.6，不另发明）

| 等级 | 含义 | 允许把写能力标 `verified`？ |
| --- | --- | --- |
| `T2` | 维护者实机（维护者自己跑完整验收） | ✅ 唯一允许的等级 |
| `T1` | N ≥ 3 台不同机器、独立社区报告验证 | ❌（最多 `probed`） |
| `T0` | 社区单台脱敏报告，未经独立复现 | ❌（最多 `probed`） |
| `未验证` | 只有上游 / 社区说法 | ❌ |

数据里 `trust.level` 只用 `T2` / `T1` / `T0` / `unverified`（`未验证` 的机器一般不该有数据文件）。

### 7.2 其余键

| 键 | 必填 | 值 |
| --- | --- | --- |
| `level` | ✅ | `T2` / `T1` / `T0` / `unverified` |
| `scope` | ✅ | `maintainer-hardware` / `multi-machine-community` / `single-machine-read-only` / `upstream-claim` |
| `verified_by` | ✅ | 证据来源（人 / `remote read-only probe` 这类描述） |
| `verified_at` | ✅ | `YYYY-MM-DD` |
| `write_verified` | ✅ | `true` 仅当真的写过并回读通过（此时 `level` 必须是 `T2`） |

### 7.3 等级口径（已澄清）

按 ADR D9.6：**`T2` = 维护者实机验证；`T1` = ≥3 台不同机器独立验证；`T0` = 单台社区机器/单次只读探测**。

第一份数据 `asus-tuf-a16-fa607pi.yaml` 是**一次远程只读探测**、没有写入验证，因此是：

```yaml
level: T0
scope: single-machine-read-only
write_verified: false
```

约束：

- 读取方**不要**把任何 `T0`/`T1` 当写入授权；写权限只由 `write_verified: true` + 能力级 `safety` 决定。
- 生成器/校验器**不要**用 `level` 反推「已经过 N 台验证」以外的语义。
- 新增数据时按其实际验证强度填 `T2`/`T1`/`T0`，不要为了好看抬高等级。


---

## 8. README 表 ↔ 字段映射（生成器约定）

| README 列 | 生成来源 |
| --- | --- |
| 品牌 | `brand` |
| 系列 / 机型 | `series` + `display_name` + `identity.bios` + `identity.cpu` + `identity.gpu` |
| 识别方式 | `identification.signals[]`（`kind` + `value` + `instance`）+ `host.driver` + `host.wmi_instances` + `identification.missing` |
| 模式 | `capabilities.mode_set`（`state` + `path` + `note`）+ `modes.values[]` |
| 风扇 | `capabilities.fan_curve` + `capabilities.fan_rpm_read` + `fans` |
| 键盘灯 | `capabilities.rgb` |
| GPU 超频 | `capabilities.gpu_clock_oc` |
| MUX | `capabilities.mux` |
| 刷新率 | `capabilities.refresh_rate` |
| 依赖服务 | `services[]` + `competitors[]` |
| 状态 | `status` + `status_note` |
| 信任等级 | `trust.level` + `trust.scope` |

**规则：**

- 表里的每一列都必须能从数据文件算出来；算不出来就先生 schema 字段，再生成 —— 不许在生成器里写死文案。
- `generic` family 不进表（ADR D9.4 / README 读表规则）。
- 生成器未实现前，手工表必须与数据文件逐项一致，PR 必须同时改两处。

---

## 9. 如何新增一台机型

1. **只读采集。** 在目标机上跑 `PerformixProbe` / 只读探测（见 README「适配一款新机型」）；
   **不写硬件、不装驱动、不改 ACL**。
2. **先看接口族。** 属于已有 family 就直接写数据；不属于就先开 ADR / 新增 family（§6）。
3. **复制模板**：拿本目录现有文件另存为 `data/machines/<id>.yaml`，逐字段填；
   不确定的写 `unknown` / `null`，并在 `known_gaps` 说明「没采集 / 被拒 / 没做」。
4. **填证据**：`evidence.probe` 指向文档；`evidence.write` 没写就写 `none`；
   DSDT 摘要（大小 / OEM ID / 命中偏移 / 是否落盘）能填就填。
5. **脱敏**：不得出现序列号、UUID、MAC、MSDM（OEM 密钥）、SLIC、用户名路径、主机名。
6. **定 `trust`**：`write_verified` 默认 `false`；不是维护者实机验收不许写 `true`。
7. **同步 README 表**（生成器实现前手工加行 + 一句说明）。
8. **不改代码。** 数据 PR 就是数据 PR。

提交前自检：`yaml` 能解析、`id` 与文件名一致、`state` / `safety` 都是词表内的值、
`known_gaps` 不为空、`evidence.probe` 指向的文件存在。

---

## 10. CI 会校验什么

### 10.1 现状（2026-11-13）

- `data/` 目录随第一份数据落地；`tests/` 里**还没有**数据 schema 测试；
- `scripts\generate-machine-table.ps1` **未实现**；
- 仓库里**没有**实际的 GitHub Actions workflow 文件（只有 `docs/ci-build.yml.example` 这份 CI 蓝图，
  它只跑 restore / build / test / WinUI 构建）。
- ⇒ **现在没有自动校验**，只能靠 Review 按本文逐项核对。

### 10.2 契约（生成器 / 校验器实现后，CI 至少拦这些）

1. `data/machines/*.yaml` 全部能被 YAML 解析，无重复 `id`；
2. `schema` 存在且等于当前版本；未知版本直接失败（不静默忽略）；
3. `id` 与文件名一致；`brand` / `series` / `display_name` / `interface_family` /
   `identity.bios` / `status` / `trust.level` 必填非空；
4. `interface_family` ∈ 本文 §6 词表；`status` ∈ §4.8；`trust.level` / `trust.scope` ∈ §7；
5. `capabilities` 的键 ∈ §5.3 词表；`state` ∈ §5.2；`safety` ∈ `ReadOnly|WriteWithReadback|NoWrite`；
6. **`state != verified` 的能力不得写 `safety: WriteWithReadback`**；
7. **`trust.write_verified: true` 仅当 `trust.level == T2`**；`verified` 能力同理；
8. `services[].kind/state`、`competitors[].kind/state` ∈ 词表；
   计划任务的 `run_level` ∈ `highest|limited`；
9. `known_gaps` 为数组且非空（没有缺口的机器通常意味着没探测透）；
10. `evidence.probe` 等仓库相对路径存在；禁止出现绝对路径 / `%TEMP%`；
11. 脱敏扫描：`data/` 下不得出现 `SerialNumber`、`UUID`、`MAC`、`MSDM`、`SLIC`、
    `C:\Users\`、主机名样式字符串；
12. **README 表与数据文件同步**：生成器输出与 README 当前表格片段比对，不一致失败。

---

## 11. 现有数据

| 文件 | 机型 | 接口族 | `status` | `trust.level` |
| --- | --- | --- | --- | --- |
| [`asus-tuf-a16-fa607pi.yaml`](asus-tuf-a16-fa607pi.yaml) | ASUS TUF Gaming A16 FA607PI（Ryzen 9 7940HX + RTX 4070 Laptop，BIOS `FA607PI.311`） | `asus-atk` | `read-only-probed` | `T0`（单机只读探测，写入未验证） |

---

## 12. 数据红线

- **不编造**：风扇转速、模式字节、写入返回码、传感器 id —— 没有就是 `unknown`。
- **不夸大**：`probed` 不是 `verified`；一次只读探测不构成「已验证」。
- **不移植结论**：别把 PH317-55（Acer）或上游 GHelper 的字节当成本机实测。
- **不写未脱敏内容**：序列号 / UUID / MAC / OEM 密钥 / 用户名路径 / 主机名。
- **不跳过证据**：每条结论都要能追到 `evidence` 或 `note`。
- **不把推测写成事实**：推测必须带「推测，未验证」字样，并且对应能力只能是 `declared` / `unknown`。
