# ASUS ATK WMI 接口族参考（`AsusAtkWmi_WMNB` / `DSTS` / `DEVS`）

> 状态：**真机只读探测记录**（2026-11-13）。机器：ASUS TUF Gaming A16 **FA607PI**
> （Ryzen 9 7940HX + RTX 4070 Laptop，BIOS `FA607PI.311`，内屏 2560×1600 165Hz）。
> 全程**只读**：没有执行任何 `WMNB` 方法、没有写硬件、没有改 ACL / 提权 / 走 IOCTL / 装驱动；
> DSDT 只在内存里做检索，**没有落盘**。
> 接口族名：`asus-atk`（[ADR-0001](../adr/0001-machine-provider.md) D9.1 的 `Providers/AsusAtk/`）。
> 对应机型数据：[`data/machines/asus-tuf-a16-fa607pi.yaml`](../../data/machines/asus-tuf-a16-fa607pi.yaml)；
> 字段定义见 [`data/machines/README.md`](../../data/machines/README.md)。
> 读者：要写 ASUS provider 的人。§1–§8 只写**这台机器上探测到的事实**；§9 是外部评审给的
> 取证方法学（来源与本机核实状态见该节开头）。推断一律标注「推断」，不许当成实测。

---

## 0. 一句话结论

**功能不在方法名里。** `AsusAtkWmi_WMNB` 的 18 个方法名里没有一个含
Fan / Thermal / Mode / GPU / Camera / Charge 关键字，全部靠 **Device_ID**（`WMNB` 的第一个参数）分派；
`DSTS` = 读状态、`DEVS` = 写状态，两者都在 DSDT 里被命中；
**最大的数据缺口是 `AsusHWMonitorWMI` 访问被拒**（多路传感器数值拿不到，本次没有做任何绕过）。

**本文分两部分，别混读**：§1–§8 是**本机只读探测的事实记录**（有证据、有偏移、有读数）；
§9 是**外部评审给出的 Device_ID 取证方法学**（2026-11-13，外部资料，未在本机核实）。
写 provider 前两块都要看：前者告诉你「现在知道什么」，后者告诉你「怎么把不知道的安全地变成知道」。

---

## 1. 探测对象与边界

| 项 | 值 |
| --- | --- |
| 机型 | ASUS TUF Gaming A16 **FA607PI** |
| CPU / GPU | AMD Ryzen 9 7940HX / NVIDIA RTX 4070 Laptop |
| BIOS | `FA607PI.311` |
| 内屏 | 2560×1600 165Hz（34×22 cm） |
| 宿主驱动 | `ASUS System Control Interface v3` **3.1.72.0**（运行中） |
| 现存 WMI 实例 | `ACPI\PNP0C14\ATK_0`、`ACPI\PNP0C14\AHWM_0`（均 Active） |
| 已确认不存在的类 | `ASUSManagement`、`AsusOptimization`、`ASUSWMI`、`ASUSSystemControl` |
| 已停用服务 | `ASUSOptimization`、`AsusCertService`（**Disabled**） |
| 本次做过 | 枚举 `root\wmi` 类 / 方法 / 签名 / 实例；读标准 WMI 传感器、显示、电池快照；内存检索 DSDT；读计划任务、服务、配置 |
| 本次**没做** | 任何 `WMNB` / `DSTS` / `DEVS` 调用；任何写入；ACL 修改或提权；IOCTL / 驱动；事件订阅；服务启停 |

> 边界说明：这份文档的价值是「哪些通道存在、名字和 id 是什么、哪里被挡住了」，
> **不包含任何写入行为结论**。写入一律按 ADR D6 的 fail-closed 处理。

---

## 2. `root\wmi` 下的三个类

### 2.1 `AsusAtkWmi_WMNB`：18 个方法，全部 `Implemented=True`

`WmiMethodId` 就是方法名的小端 ASCII 四字符（例：`INIT` → 内存字节 `49 4E 49 54` → `0x54494E49`）。
18 个方法**一一对应**，没有任何一个方法名带功能语义：

| 方法名 | `WmiMethodId` | 用途推断（除注明外均为名字推断，未验证） | 本次证据 |
| --- | --- | --- | --- |
| `INIT` | `0x54494E49` | 初始化 / 握手 | 仅命名 |
| `BSTS` | `0x53545342` | 电池状态（Battery STatuS） | 仅命名 |
| `SFUN` | `0x4E554653` | 未定位（名字无明确语义） | 仅命名 |
| `WDOG` | `0x474F4457` | 看门狗 | 仅命名 |
| `KBNI` | `0x494E424B` | 键盘相关（KB） | 仅命名 |
| `SCDG` | `0x47444353` | 未定位 | 仅命名 |
| `SPEC` | `0x43455053` | 未定位 | 仅命名 |
| `OSVR` | `0x5256534F` | OS 版本 | 仅命名 |
| `VERS` | `0x53524556` | 接口 / 固件版本 | 仅命名 |
| `SPBL` | `0x4C425053` | 未定位 | 仅命名 |
| `SDSP` | `0x50534453` | 显示相关 Set（与 `GDSP` 成对） | 仅命名 |
| `GDSP` | `0x50534447` | 显示相关 Get（与 `SDSP` 成对） | 仅命名 |
| `GLCD` | `0x44434C47` | LCD 相关 | 仅命名 |
| `ANVI` | `0x49564E41` | 未定位 | 仅命名 |
| `CBIF` | `0x46494243` | 未定位 | 仅命名 |
| `MWGF` | `0x4647574D` | 未定位 | 仅命名 |
| `DSTS` | `0x53545344`（原记录）<br>`0x53544344`（第二候选，见 §9.3） | **设备状态读**（Device STatuS） | DSDT `0x092C2` 命中，只验证到 `0x53545344` 这一条 |
| `DEVS` | `0x53564544` | **设备状态写**（Device SET） | DSDT `0x0A1EC` 命中 |

**关键结论（写给实现者）：**

1. **关键词计数 = 0。** 18 个方法名中 Fan / Thermal / Mode / GPU / Camera / Charge 一个都没有。
   想靠「找 `SetFanSpeed` 这样的方法」来支持 ASUS 是走不通的 —— 找不到不是机型问题，是这套接口的设计。
2. **功能靠 Device_ID 分派。** 用户态看到的是 `WmiMethodId`（四字符），ACPI 侧看到的是同一个四字符
   被当作 `WMNB` 的 id 参数，真正区分「风扇 / 模式 / 灯效 / 充电」的是**后面那个 id**（见 §3）。
3. 本次只检索了 `DSTS`（`0x53545344`） / `DEVS` 两个常量在 DSDT 里的命中；
   **第二个 `DSTS` 候选 ID `0x53544344` 没有检索**（外部资料说 Linux 驱动里两个都存在，见 §9.3），
   也没有检索其余 16 个方法 id，不知道它们在 ACPI 里是否出现、对应哪个 Device_ID 分支。
4. 方法名与四字符 id 的对应关系是**真的**（18 个全对得上），不是巧合。
   但名字与功能**没有**对应关系（见第 1 条），这两件事要分开记。

### 2.2 `AsusHWMonitorWMI`：3 个方法 + 访问被拒（最大数据缺口）

| 方法 | `WmiMethodId` | 参数类型 | 用途推断（未验证） |
| --- | --- | --- | --- |
| `getTotalSensorValues` | `0x0B` | `SInt64` | 传感器数值总数 / 批量取值 |
| `getTotalSensorID` | `0x0C` | `SInt64` | 按索引取传感器 ID |
| `getSensorLimitByID` | `0x0D` | `SInt64` | 按传感器 ID 取上下限 |

**探测到的事实（同一台机器、同一会话、同一权限）：**

| 访问路径 | 结果 |
| --- | --- |
| 类路径（`root\wmi:AsusHWMonitorWMI`） | 「无法从客户端中访问 CIM 资源」 |
| 实例路径（`ACPI\PNP0C14\AHWM_0`） | 「拒绝访问」 |

- 类本身能枚举到（3 个方法、签名可读），但**调不动实例**；当前权限下拿不到任何传感器数值。
- 这是本机型**最大的数据缺口**：多路温度 / 风扇转速的候选来源之一直接不可用，
  标准 WMI 又只剩 1 个 ACPI 热区（见 §6），所以风扇转速目前**没有已验证的来源**。

**红线（不要做）：**

1. **不改 ACL**（`root\wmi` 命名空间或 `AHWM_0` 实例的安全描述符）。
2. **不提权**到 SYSTEM / TrustedInstaller 去「借」权限；不注册伪造的 WMI 提供程序。
3. **不发 IOCTL、不装驱动、不映射物理内存**：ADR C8 / D3 的硬红线，直接 EC 端口 I/O 永不做。
4. **不把「访问被拒」解释成「不支持」**：类和方法都在，缺的是权限/调用条件，结论只能是
   `unknown` / `Unavailable` + 原因（ADR D6 fail-closed）。
5. 可以接受的下一步只有：**用户显式授权下，用管理员会话重跑一次只读查询**。
   这属于「补探测」，不是「绕过」；本次没做，所以数据里是 `unknown`。

### 2.3 `AsusAtkWmiEvent`：极简事件类

| 属性 | 类型 | 说明 |
| --- | --- | --- |
| `EventID` | `UInt32` | 唯一的事件标识（**没有**类型名、没有明细字段） |
| `TIME_CREATED` | WMI 标准 | 事件创建时间 |
| `SECURITY_DESCRIPTOR` | WMI 标准 | 事件安全描述符 |
| `Active` | WMI 标准 | 实例是否活跃 |
| `InstanceName` | WMI 标准 | 实例名 |

- **没有** `WmiEventType`、**没有** `EventDetail`（跟某些品牌族 / 上游文档里的形态不一样，别照抄）。
- WQL 查询形如：

  ```sql
  SELECT * FROM AsusAtkWmiEvent
  ```

- 本次**没有注册订阅**（`WQL` 只写了形态，没有 `ManagementEventWatcher` / `Register-CimIndicationEvent`）。
- 注意事项：
  1. 注册订阅会留下 WMI 消费者 / 订阅状态，进程崩溃时容易残留，**要么显式清理、要么不注册**；
  2. 只有 `EventID`，没有类型和明细 → 「这个 id 对应哪个四字符 Device_ID / 哪个硬件事件」
     必须自己建映射，本次没有建；
  3. 订阅本身不写硬件，但它**改变系统状态**，属于「会留残留」的操作，本次刻意不做。

---

## 3. ACPI 证据：`DSTS`（读）/ `DEVS`（写）怎么被确认的

### 3.1 检索结果

| 项 | 值 |
| --- | --- |
| DSDT 大小 | 66728 字节 |
| OEM ID | `_ASUS_` |
| 检索方式 | **只在内存里做字符串检索，未落盘** |
| `DSTS` 常量 `0x53545344` | 命中 1 处 @ `0x092C2` |
| `DEVS` 常量 `0x53564544` | 命中 1 处 @ `0x0A1EC` |
| `Method(WMNB, ...)` 起始 | `0x091DE`，`argcount = 3`、`serialized = 1` |
| `ATK` 关键字 | 112 处 |
| `PNP0C14` 设备 | 2 个，`_UID` 分别是 `ATK` 与 `ITKS` |
| `_DSM` | 9 个 |

### 3.2 关键结构

两处常量都在同一个 `Method(WMNB, 3, Serialized)` 里的 If 链上：

```text
Method(WMNB, 3, Serialized)          // 起始 0x091DE
  ...
  If (LEqual(Local0, 0x53545344))    // DSTS @0x092C2
      ...
  If (LEqual(Local0, 0x53564544))    // DEVS @0x0A1EC
      ...
```

### 3.3 结论

- **ACPI 侧是三参数：`WMNB(id, arg1, arg2)`。**
- 用户态 / MOF 看到的方法**少了一个参数**：那个「少掉的参数」就是方法 ID（`WmiMethodId` 那个四字符），
  **由驱动按 `WmiMethodId` 补进去**。所以不要把「MOF 里参数个数」当成 ACPI 调用签名。
- `DSTS` = 读状态、`DEVS` = 写状态的判定，依据就是两个常量分别命中在这个
  `Method(WMNB)` 的两条 `If(LEqual(Local0, ...))` 分支上。

### 3.4 `DEVS` 分支里的一处细节（推测，未验证）

`DEVS` 分支内出现了这样一段：

```text
If (LEqual(IIA0, 0x00050011))
    If (Arg0 == 2)
        Store (One / Zero, BLCT)
```

- `BLCT` 疑似**充电阈值类的二值开关**（写 1/0 的那种），但**这是推测**：
  `IIA0` 与 `BLCT` 的完整语义、`Arg0 == 2` 的条件含义、以及 `arg1 / arg2` 分别传什么，都没定位。
- 因此机型数据里 `charge_limit` 只能是 `declared`，`write_verified: false`；
  电池快照（100% / `Charging=False` / `ChargeRate=0`）**不能**用来反证阈值是否生效。

### 3.5 偏移的可用性

`0x092C2` / `0x0A1EC` / `0x091DE` 只对**本次内存镜像**有效：

- DSDT 编译方式、BIOS 版本变了，偏移就会变；**不是 ABI，不要写进代码当常量**；
- 换 BIOS / 换驱动版本后必须重探（ADR D6 的缓存键包含 BIOS 与驱动版本）；
- 下次要做这件事，正确做法是重新取 DSDT（仍按只读）再检索常量，而不是复用偏移。

---

## 4. G-Helper 互斥：这台机器已经被它替代奥创

### 4.1 现状

| 项 | 状态 |
| --- | --- |
| `GHelper` 进程 | 当前**未运行** |
| 计划任务 `GHelper` | `LogonTrigger` · `Interactive` · **Highest** · 状态 **Running** |
| 计划任务 `GHelperCharge` | `BootTrigger` · `S4U` · Limited · 状态 **Ready** |
| 服务 `ArmourySocketServer` | **Disabled** |
| `HKCU\Run` / `HKLM\Run` / 启动文件夹 | **均为空** |

**互斥检测结论（本机）：** 三个启动面都空 + 奥创服务已停 → 检测面只需要盯
**上面两个计划任务**（`Get-ScheduledTask`）+ 进程名。
这是**本机事实**，不要推广成通用结论：别的机器可能还有 Run 键、启动文件夹、奥创残留任务。

**为什么必须盯它：** `GHelper` 是 `LogonTrigger`，用户登录就会被拉起来；
它替代奥创后用的是 ASUS 自家的 ACPI / WMI 通道（README 记录上游 GHelper 走
`\\.\ATKACPI` + ASUS ACPI-WMI；本次没有抓它的调用），**随时可能回来跟我们抢 `DSTS` / `DEVS`**。
按 ADR D11，v1 只**检测 + 提示**，不杀进程、不停服务、不改启动类型。

### 4.2 `%APPDATA%\GHelper\config.json`：84 键

| 分组 | 键数 |
| --- | --- |
| `performance` | 3 |
| `fan` | 10 |
| `limit` | 12 |
| `gpu` | 19 |
| `undervolt` | 9 |
| `power` | 6 |
| `autoapply` | 6 |
| `aura` | 3 |
| `display` | 8 |
| `keyboard` | 3 |
| `misc` | 2 |
| 未归组（`cpu_temp_0` / `cpu_temp_1` / `cpu_temp_2`） | 3 |
| **合计** | **84** |

### 4.3 一个直接踩到的坑：`powermode` 映射落空

- G-Helper 配置里的 `powermode` 指向 **「高性能」GUID `8c5e7fda…`**；
- 但本机 `powercfg /L` **只剩「平衡」** → 这个映射在本机直接落空。
- 所以：**不要**把 Windows 电源方案 GUID 当成 ASUS 档位字节来用；
  也不要把「G-Helper 配了某个方案」当成「本机支持某个档位」的证据。

---

## 5. 三个陷阱（按踩到的概率排序）

### 5.1 EDID 只报 60Hz（大坑）

- 机器当前是 **2560×1600 @ 165Hz**；
- 但 `WmiMonitorListedSupportedSourceModes` **只报 `2560×1600@60` 一条**；
- 原因：165Hz 走 **DisplayID**，不在 EDID 的基础（established / standard / detailed）列表里；
- 后果：任何基于 EDID 的刷新率档位枚举都会**读错**——以为「只有 60Hz」，进而误判
  「不支持高刷」。这不是显示器问题，是**枚举来源选错了**。
- 正确来源：Windows 显示 API（`DisplayConfigGetDeviceInfo` / `EnumDisplaySettings` 一族），
  并且**不能用 EDID 列表当可用率上限**。
- 与本仓库现有实现的冲突：`DisplayManager` 走的是 Windows 显示 API（`EnumDisplaySettings`），
  不读 EDID，所以在 PH317-55 与本机型上都不会踩这个坑；但任何**新写的**
  EDID / `WmiMonitorListedSupportedSourceModes` 枚举路径都会读错 —— 机型数据里已把这条禁掉
  （`refresh_rate.note`）。别为了「统一枚举来源」把它换回 EDID。

### 5.2 `powercfg` 只剩「平衡」

- 本机 `powercfg /L` 只有均衡一项（没有高性能 / 节能）；
- 上游工具配的「高性能 GUID」因此指向一个不存在的方案 → 映射落空（§4.3）；
- 推论：**不要用 power scheme / overlay 作为这台机器的模式映射来源**。
  真正决定性能档位的很可能是 `DEVS` 那套 Device_ID 写入（本机型尚未验证）。
- 这也验证了 ADR D10 对 Generic provider 的警告：`powercfg` 在现代机型上常常只剩「平衡」，
  它**不等于**「机器没有性能模式」。

### 5.3 服务 Disabled ≠ 类不存在

- `ASUSOptimization`、`AsusCertService` 都是 **Disabled**；
- 但 `AsusAtkWmi_WMNB` 的 18 个方法**全程 `Implemented=True`**，类与实例都在；
- 所以：**能力探测不能以「OEM 服务在跑」为前提**；服务状态只用于服务页展示与竞争提示。
- 反过来也不成立：服务 Disabled **不**代表通道没被占用（G-Helper 的计划任务照样可能拉起）。

---

## 6. 传感器与显示现状（只读快照，仅证明通道）

| 来源 | 本次读数 | 说明 |
| --- | --- | --- |
| `MSAcpi_ThermalZoneTemperature` | 仅 1 个热区 `TZ01` = **3552**（= 355.2 K ≈ 82.1 °C） | 温度有值；只是快照，不是基准 |
| `Win32_Fan` / `CIM_Fan` | **空** | 标准 WMI 拿不到风扇转速 |
| 风扇转速 | **无已验证来源** | 候选：`DSTS`（未调用）、`AsusHWMonitorWMI`（拒绝访问） |
| `WmiMonitorListedSupportedSourceModes` | 只报 `2560×1600@60` | 实际 165Hz 走 DisplayID，见 §5.1 |
| 电池 | `PowerOnline=True`、`Charging=False`、100%、16715 mV、Remaining 79824 mWh、`ChargeRate=0` | 满电时 `ChargeRate=0` 属正常，**不能**当充电阈值证据 |

> 这些数值是探测那一刻的读数，**只用来证明通道存在**，不是本机型的基准值，
> 更不要写进测试断言。

---

## 7. 未验证项清单（写 provider 前先看这里）

| 未验证项 | 现状 | 要验证需要什么 |
| --- | --- | --- |
| `BLCT` 语义 | 疑似充电阈值二值开关（§3.4，推测） | DSDT 完整反编译 + 真机写入 + 回读 |
| `DEVS` 的 Device_ID 取值与载荷 | 只知道 ACPI 方法有三参数，不知道各 Device_ID 的 arg1/arg2 格式 | 真机逐 id 调用（先只读/无害 id）+ 回读 |
| `DSTS` 的 Device_ID 与返回格式 | 未调用，转速/温度/单位全未知；`DSTS` 还有**两个 method ID**（§9.3），哪个可用没探测 | 只读调用 + 与已知传感器交叉验证；两个 ID 都要试 |
| 模式档位字节与语义 | 完全没有 | 逐档写 + 回读 + 可回滚（ADR D5） |
| GPU Mode / MUX 映射 | 有没有 MUX、dGPU 断电怎么写都没数据 | 厂商工具行为对照 + OEM 写通道验证 |
| `AsusHWMonitorWMI` 传感器 id 表 | 类能枚举、实例拒绝访问 | 管理员只读会话重试（需授权），仍拿不到就保持 `unknown` |
| DisplayID 高刷枚举 | 知道 EDID 只报 60Hz，没验证 DisplayID 路径 | Windows 显示 API 实测 |
| `AsusAtkWmiEvent` 的 `EventID` 语义 | 只确认结构，没有订阅、没有映射 | 显式订阅 + 记录残留清理方案 |
| 风扇 fanId / 单位 / 转速地板 | 全部未知 | 写后回读 + 时间序列 |
| 写入回读强度与回滚 | 没有任何写入证据 | 维护者实机验收（只有 T2 才能升 `verified`） |
| SMBIOS 识别串 | 没采集（`smbios_family: null`） | 只读采集 SMBIOS Type 1/2/3（脱敏后入数据文件） |

---

## 8. 给写 ASUS provider 的人的规则清单

1. **先只读、先数据。** 按 ADR D9 步骤 4：ASUS 写入代码落地前，只做识别、探测、
   数据文件；数据文件先行合法（本文件就是那一步的产物）。
2. **默认 `NoWrite`，fail-closed。** 任何没做过「写 + 回读 + 回滚」的能力，一律
   `NoWrite`，UI 显示但禁用 + 原因（ADR D6）。
3. **不许绕过访问控制。** 不改 ACL、不提权到 SYSTEM、不走 IOCTL / 驱动 / 物理内存。
   被拒就是被拒，记 `Unavailable` + 原因（ADR C8 / D3）。
4. **不许按方法名猜功能。** 本族 18 个方法名没有一个带功能语义（§2.1）；
   功能在 Device_ID 上，id 未知就写 `unknown`。
5. **不许拿别家的常量试。** 别把 Acer 的 `0x410009` / PH317 的字节套到 ASUS 上；
   也别把 G-Helper 的配置值当成本机的实测字节。
6. **写入必须闭环。** 回读强度、有限重试、失败可回滚、退出/崩溃归还（ADR D4 / D5）；
   没有回读手段的写入一律 `NoWrite`。
7. **冷启动先查互斥。** 检查 `GHelper` / `GHelperCharge` 两个计划任务与进程；
   检测到就降级 + 提示，不杀进程（ADR D11）。
8. **BIOS / 驱动版本变了就重探。** 偏移、id 表、方法集合都可能变（ADR D6 缓存键）。
9. **证据进数据文件。** 每项结论写 `state` + `note` + `evidence`；
   推测必须带「推测，未验证」，不许伪装成实测。
10. **读之前先查 AML 副作用**（§9.4）：只读阶段**只调 `DSTS`**，不调 `DEVS`（写）与 `INIT`（通知 BIOS
   「OS 已接管」，会改热键行为）；没有反编译证据的 ID 一律不调。读取串行化、频率 ≥500ms 一次。
11. **未验证的 `(主板 Product, BIOS SMBIOSBIOSVersion)` 组合一律降级为「只显示」**（§9.2）：
   既不写、也不当作可用能力；ID 集合与语义随 BIOS 变，别把上一次的偏移/常量带过来。

---

## 9. Device_ID 取证与只读验证方法论（外部评审结论）

> **本节不是本机探测结果。** 这里是 2026-11-13 外部工程评审给出的方法学，回答「一个 Device_ID
> 到底可不可信、只读阶段还能安全地做什么、拿不到时该怎么办」。
> 标「外部资料」的（Linux 驱动 / G-Helper / 评审结论）**均未在本机核实**；本机实测只有 §2 / §3 / §6。
> 本节的方法是**给写 provider 的人的执行纪律**，不是可选建议。

### 9.1 可信来源优先级：先看 AML，再对驱动，最后才看社区工具

| 排序 | 来源 | 为什么权威 | 能给出什么 | 不能给出什么 |
| --- | --- | --- | --- | --- |
| ① | **本机 DSDT / SSDT**（`iasl -d` 反编译后看 `WMNB` 里 `If (IIA0 == 0x0012xxxx)` 分派） | 本机型、本 BIOS 的**真值**；同一个 ID 在不同 BIOS 版本上语义可能变 | 分支是否存在、参数个数与顺序、读到哪、写什么、读路径有没有副作用 | 别人的机型怎么编码；偏移不是 ABI（§3.5） |
| ② | **Linux `asus-wmi.h` / `.c`**（独立实现，可交叉验证；较新内核的 `asus-armoury` 带**按机型的功率表**） | 另一套独立实现，能验证「这个 ID 在别的机器 / 别的 WMI 版本上被怎么读」；armoury 的功率表是机型级数据，可当**假设来源** | 方法 ID 集合（含 §9.3 的 `DSTS` 双 ID）、常见 Device_ID 的读法、按机型的表 | 本机 BIOS 的分支、本机的语义 |
| ③ | **G-Helper**（社区实测） | 价值在**机型特判**而不只是常量表：哪些机型要特殊处理、什么时候回退到 `\\.\ATKACPI`、BIOS 版本分支 | 机型级的「实测行为」与代码分支 | 本机 BIOS 的结论；跨机型一致性（§9.2） |

**用法**：① 定「本机有没有」；② 给「这个 ID 可能是什么」的假设；③ 给「哪个机型踩过什么坑」。
**没有 ① 的证据，最多只能写 `declared` / `unknown`；②③ 只能产生假设，必须走 §9.5 验证。**

### 9.2 直接沿用 G-Helper 常量表的四个风险

| # | 风险 | 具体表现 | 我们的处理 |
| --- | --- | --- | --- |
| 1 | **同一 ID 在不同机型编码不同** | 性能模式 ROG / TUF 用 `0x00120075`，Vivobook 用 `0x00110019`；档位编号顺序也不同 | 常量必须按 `(机型, BIOS)` 落数据文件，**不进代码**；代码里只许出现「从数据文件读到的 id」 |
| 2 | **带 presence 位只说明 BIOS 认识这个 ID** | 读到 `0x00010000` 是 presence 位已实现（低 16 位是值、开关看 bit0），不代表**功能生效** | presence 位只算「有这个 ID」，不算「功能可用」；能力状态最多 `Probed`，写入口禁用（ADR D6） |
| 3 | **TUF 可能根本没有 MUX / Overdrive** | 同品牌不同系列硬件不同；GHelper 表里有不代表本机有 | 机型数据里 `mux` 保持 `unknown`（§7），**不按系列推断** |
| 4 | **BIOS 更新会增删 ID** | ID 集合、语义、偏移都随 BIOS 变 | **验证主键 = `(Win32_BaseBoard.Product, Win32_BIOS.SMBIOSBIOSVersion)`**；未在这个组合上验证过的 ID，一律**降级为「只显示」**（不写、不当作可用能力） |

> 所以优先级表里 GHelper 排最后：不是它错，而是它给的是「机型级结论」，
> 我们缺的恰恰是「本机型本 BIOS 的结论」。

### 9.3 新事实：`DSTS` 有两个 method ID（外部资料，未在本机核实）

- Linux 内核 `asus-wmi` 驱动里 **`DSTS` 有两个 method ID：`0x53544344` 和 `0x53545344`**；
  驱动在**运行时探测哪个可用**。**不要写死。**
- 我们此前只记录了 `0x53545344`（§2.1 表格、§3.1 的 DSDT 命中），另一个完全没提过。
- 按 §2.1 的字节序反推：`0x53545344` = `DSTS`、`0x53544344` = `DCTS`；
  两者都是「读设备状态」的候选，哪个在本机哪个 WMI 版本上有效**只能运行时探测**（推断，未验证）。
- 影响三件事：
  1. 探测代码里的 method ID 必须来自「探测结果 + 数据文件」，**不能是源码里的常量**；
  2. 本机 DSDT 检索只覆盖 `0x53545344`，**`0x53544344` 没有检索过**（§2.1 第 3 条）→
     本机有没有这条分支是未知，不是不存在；
  3. 数据文件已把这条缺口写进 `known_gaps`（`data/machines/asus-tuf-a16-fa607pi.yaml`）。

### 9.4 只读阶段的边界：只调 `DSTS`，不碰 `DEVS` / `INIT`

| 项 | 结论 | 为什么 |
| --- | --- | --- |
| `DSTS`（两个候选 ID 都算） | ✅ 允许（前提见下） | 读状态；是唯一可能拿到温度 / 风扇 RPM 的只读通道 |
| 标准 WMI / NVML / 性能计数器（§9.7 的 ②③④） | ✅ 允许 | 与 ASUS 通道无关，不碰硬件写路径 |
| `DEVS` | ❌ 禁止 | 写状态（模式、风扇、充电阈值都走它）；只读阶段没有任何写入理由 |
| `INIT` | ❌ 禁止 | 它是通知 BIOS「OS 侧已接管」，**会改变热键通知等行为** —— 不是纯读；调完再想「干净地只读」已经晚了 |
| 其余 16 个方法（`BSTS` / `SFUN` / `WDOG` / `KBNI`…） | ❌ 禁止 | 没有副作用证据，默认按「可能有副作用」处理；不调 |

**读之前必须先做的三件事**：

1. 在反编译 AML 里看该 ID 的**读路径有没有副作用**：写 EC 索引寄存器（`0x62` / `0x66` 一类）、
   `Notify`、进 SMI（`Store` 到 `0xB2` 之类）；
2. **有副作用 → 不作为只读候选**；
3. **没有反编译证据 → 不调**（fail-closed）。

**读取纪律**：

- **串行化**：同一时刻只有一个读；不与 `DEVS` 交错（G-Helper / 奥创可能同时在写，§4）；
- **频率 ≥500ms 一次**：不允许一次性刷几百个 ID（既可能触发 EC 竞争，也会把日志刷爆）；
- 每次读记：ID、输入参数、返回值、时间戳、BIOS / 驱动版本（ADR D6 缓存键）。

### 9.5 判「有效值 vs 垃圾值」

**确定性判据（先看这个，命中就不必再猜）**：

| 返回值 | 判定 | 说明 |
| --- | --- | --- |
| `0xFFFFFFFE` | **不支持** | 明确的「没有这个功能」 |
| `0x00010000` | **presence 位已实现** | 低 16 位是值，开关看 bit0；只说明 BIOS 认识这个 ID |
| `0` / `0xFFFFFFFF` / 高位乱置 | **可疑** | 不当作有效值：`0` 可能是「未初始化 / 默认」，全 F 常见于错误 / 未实现，高位乱置说明打包宽度不对 |
| 位宽 / 符号与 MOF 声明不符 | **可疑** | 先怀疑「读到了共享缓冲残留」（见下表 ⑤），不要先怀疑传感器坏了 |

**启发式判据（按要求从强到弱排序）**：

| 顺序 | 判据 | 怎么做 | 为什么可信 |
| --- | --- | --- | --- |
| ① | **外部激励只读观测**（最强） | 用户用**官方 Fn+F5 / MyASUS** 改设置（切模式、改电池上限），我们**只读**看对应 ID 是否同步变化 | 只读前提下能拿到「输入 → 输出」的因果，是唯一不靠猜的办法 |
| ② | **负载相关性** | 压测时 CPU 温度升、风扇 RPM **滞后**升（方向 + 滞后都要对上） | 物理量之间有因果；单一时刻的数值不算证据 |
| ③ | **值域合理性** | 模式 `{0,1,2}`、电池上限 20–100、风扇约 0–7000、温度 20–105 | 便宜地筛掉明显不对的；**超出值域不等于无效**（可能有单位 / 偏移），要再查 |
| ④ | **稳定性** | 配置类连读一致；传感器类平滑（不跳变） | 真实传感器有惯性；配置值不会随机变 |
| ⑤ | **残留缓冲检测** | A → 未知 X → B → X 交错读；若 X 总等于上一次结果、或等于 **ID 本身** → 读到共享缓冲残留（同一个 `WMNB` 复用了返回缓冲） | 这是最容易把垃圾当值的一类错，能直接排除一批「看起来很像」的假数据 |
| ⑥ | **跨 ID 一致性** | 多个未知 ID 返回值**完全相同** | 说明读到的是默认值 / 未实现占位，不是各自的值 |

> 判据 ①② 需要用户配合（用官方工具操作），是**交互式验证**：不能自动化，也不能在无人值守探测里
> 假装做过。没有做就写 `unknown`（ADR D6 fail-closed）。

### 9.6 `AsusHWMonitorWMI` 拒绝访问的排查顺序

按顺序查，**每一步都只读**：

| 顺序 | 怀疑点 | 现象 / 错误码 | 怎么只读确认 |
| --- | --- | --- | --- |
| ① | **进程未提权**：在管理员组里，但不是「以管理员运行」→ 令牌里管理员组是 deny-only | `0x80041003` / `0x80070005` | `whoami /groups` 看「仅用于拒绝」；或在代码里看 `WindowsIdentity` 的组列表。**用户显式授权**后用管理员会话重跑只读查询 |
| ② | **类 / 命名空间 DACL 被厂商收紧** | 同类错误码，但管理员也拒 | 只读读 `root\wmi` 与实例的安全描述符；`wmimgmt.msc` 只读查看。**不改 ACL** |
| ③ | **ACPI-WMI 数据块 GUID 的安全描述符** | 拒绝访问 | 只读读 `HKLM\SYSTEM\CurrentControlSet\Control\WMI\Security\{GUID}` 的值与 ACL，看是否只授 SYSTEM |
| ④ | **provider 自校验调用方签名 / 路径** | 错误码不标准、或 provider 自己返回拒绝 | 从 provider 限定符找 `__Win32Provider` → `CLSID` → 对应 DLL → 签名者；事件查看器 `Microsoft-Windows-WMI-Activity/Operational` 看 provider 报错 |

**先分清 provider 没加载**：`0x80041013`（provider 加载失败）/ `0x80041010`（类无效）——
这两个**不是权限问题**，先查 provider 注册，别按上面的顺序查 DACL。

**只读排查手段（全部只读）**：事件查看器 `Microsoft-Windows-WMI-Activity/Operational`、
`wmimgmt.msc`（只读看）、`Get-CimInstance -Namespace root\wmi -ClassName __Win32Provider` 找
`__Win32Provider` → `CLSID` → DLL 签名者。

**红线不变**（§2.2）：不改 ACL、不提权到 SYSTEM、不注册伪造 provider。排查结论只能写
「原因 + `Unavailable`」，不许把「被拒」写成「不支持」。

### 9.7 正当替代数据源（按可靠性 / 侵入性排序）

| 排序 | 来源 | 能给什么 | 限制 | 侵入性 |
| --- | --- | --- | --- | --- |
| ① | **ATK `DSTS` 读温度 / 风扇**（GHelper 表里 `Temp_CPU = 0x00120094`、`Temp_GPU = 0x00120097`） | 温度，可能还有风扇 RPM | 必须按 §9.4 / §9.5 验证；GHelper 表是**外部资料，未在本机核实** | 只读，无提权 |
| ② | **NVML**（dGPU 温度 / 功耗 / 频率 / 占用） | dGPU 实时数据 | 无需管理员；但**笔记本风扇不走 NVML**（风扇是 EC 管的） | 用户态库，低 |
| ③ | **Windows 性能计数器**（`Processor Information`、`Thermal Zone Information`）+ `Win32_Battery` / `BatteryStatus` | CPU 相关与电池状态；与厂商通道无关 | 热区在部分机器是固定值 / 非 CPU；计数器名随语言版本变 | 只读，低 |
| ④ | **`MSAcpi_ThermalZoneTemperature`**（0.1 K） | 便宜的温度来源 | 很多机器是固定值 / 不是 CPU 温度（本机只有 1 个热区，§6） | 只读，低 |
| ⑤ | 厂商 SDK / 合作授权 | 最权威，也可写 | 需要授权与 NDA；覆盖范围视厂商而定 | 中 |
| ⑥ | **用户自愿的签名内核驱动方案**（LibreHardwareMonitor + **PawnIO**；**不要用 WinRing0**） | 能读 EC / 风扇等一切 | 需要用户明确同意 + 签名驱动；与 HVCI / 反作弊可能冲突 | 高 |
| ⑦ | 复用官方组件授权（ATKACPI 管道 / 官方 WMI 的私有契约） | 借用官方行为 | **非公开契约，随版本失效**；**若对方校验调用方签名，任何绕过都不正当** | 高（法律 / 稳定性） |

> **不要用 WinRing0**：它是已知漏洞驱动，已进微软「易受攻击驱动阻止列表」，且与 HVCI / 反作弊冲突。
> 这条与 ADR-0001 C8 / D3 的红线一致（旧版装过它，`Overlay/WinRing0Cleanup.cs` 就是清理代码）。

### 9.8 本节红线

1. **不做 EC 端口 I/O**：需要内核驱动，且会与 BIOS / EC 固件竞争，导致 **EC 状态错乱**。
2. **只读阶段只调 `DSTS`**：不调 `DEVS` / `INIT`（§9.4），不为了「多拿一个值」冒险。
3. **不改 ACL、不装驱动、不提权、不发 IOCTL**：被拒就记 `Unavailable` + 原因（§2.2 / ADR C8）。
4. **启发式结论不能写成 `verified`**：①–⑥ 只是判据，不是写入证据；`verified` 只认「写 + 回读 + 回滚」。

---

## 10. 相关文档

| 文档 | 关系 |
| --- | --- |
| [ADR-0001](../adr/0001-machine-provider.md) | D6 三态、D7 安全等级、D8 归属、D9 接口族与数据、D11 竞争检测 |
| [ADR-0002](../adr/0002-privileged-host.md) | 宿主形态（Windows 服务 + 管道）；本文 §9 的只读探测将来跑在宿主里，边界不变 |
| [`data/machines/asus-tuf-a16-fa607pi.yaml`](../../data/machines/asus-tuf-a16-fa607pi.yaml) | 本机型的机器可读数据（本文的证据来源之一） |
| [`data/machines/README.md`](../../data/machines/README.md) | 数据 schema：字段、`state` 取值、`trust` 定义、CI 校验 |
| [SERVICE-PAGE.md](SERVICE-PAGE.md) | 服务页 v1：本文件的 §4 就是它的数据来源形态 |
| [FINDINGS-PH317-55.md](../FINDINGS-PH317-55.md) | 另一接口族（Acer）的实测记录：刷新率走 Windows 显示 API；本族则是 EDID 不可信、必须以显示 API 为准 |
| [README「机型适配」](../../README.md) | 用户可见的适配表与信任等级 |
