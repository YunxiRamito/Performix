# Predator PH317-55 — 逆向结论与可行性（实测）

> 目标机：Acer Predator PH317-55（Helios 300，TGL / RAV4_TLS，BIOS V1.14，i9-11900H + RTX 3070 Laptop）
> OEM 应用：`AcerIncorporated.PredatorSenseV30_3.0.3196.0_x64` → `Win32\PredatorSense.exe`（x64 **托管 .NET / WPF**，95 MB）
> 结论一句话：**风扇曲线可以完全复刻，且不需要任何内核驱动。**

---

## 1. OEM 应用是什么

| 项 | 结果 | 证据 |
|---|---|---|
| PE 架构 | x64, PE32+ | `mach=0x8664 magic=0x020B` |
| 宿主 | **.NET 托管**（CLR DataDir[14] ≠ 0） | PE 头解析 |
| UI | WPF，XAML 资源内嵌 | `/PredatorSense;component/fancontrolpage.xaml` 等 |
| 类型数 | 627（反编译出 149 个 .cs） | `ilspycmd -l c` |
| 硬件通道 | **不直接调 WMI**，走命名管道 `predatorsense_service_namedpipe`，由服务进程代为下发。cmd 9/10 = 通用 WMI Get/SetFunction；**传感器读数是 cmd 15**（`kSvcCmdWMIGetGamingSystemInformation`） | `WMIFunction.cs:11-55`, `WMIFunction.cs:172-190` |
| 风扇页 | Auto / Max / Custom / **Silent** 四种模式 + CoolBoost + 每风扇滑杆 | `FanControlPage.cs` |

反编译产物：`PredatorSense/decompiled/`（ilspycmd 9.1.0，跑在本机已装的 .NET 8 运行时上）

---

## 2. 关键：OEM 自家协议编码（已从代码还原）

### 2.1 风扇行为（模式）
`FanControlPage.Set_WMI_FanMode()` 组包，形如 `fanMask | (mode2bit << 16)`：

| 模式 | 2-bit 值 | 实测载荷 |
|---|---|---|
| Auto | 1 | `0x00410009` |
| Max | 2 | `0x00820009` |
| Custom | 3 | `0x00C30009` |

单风扇（CPU 1 个、GPU 1 个）时 fanMask = bit0(CPU) + bit3(GPU) = `0x9`，高位 mode 位 = CPU bit0、GPU bit6 → `0x41/0x82/0xC3`，与 PreySense 文档完全一致。

### 2.2 风扇转速（占空比）
```text
gmInput = (dutyPercent << 8) | fanId      fanId: 1 = CPU, 4 = GPU
```
> PreySense 的 `((pct*25600/100)&0xFF00)+id` 与上式等价（`pct*25600/100 == pct<<8`）。

### 2.3 本机 WMI 方法实测（root\WMI → `AcerGamingFunction`，实例 `ACPI\PNP0C14\APGe_0`）

| 方法 | 状态 | 说明 |
|---|---|---|
| `GetGamingMiscSetting` | ✅ | `0x0B` → 返回 `0x100` ⇒ 当前 Balanced |
| `GetGamingSysInfo` | ⚠️ 半残 | 只有 id `0x00` / `0x02` 有数据，`0x03..0x40` 全返回 `2`（= 本底盘不支持） |
| `GetGamingFanSpeed` | ⚠️ | 返回**上次下发的占空比**（÷256），不是 RPM 反馈 |
| `SetGamingFanBehavior` | ✅ | `0x410009` / `0x820009` / `0xC30009` 均 `ReturnValue=True` |
| `SetGamingFanSpeed` | ✅ | **写入生效**：下发 35% 后回读 35（基线 72/65） |
| `SetGamingFanTable` | ✅ 存在 | OEM 的 **Silent** 模式用它（载荷 1..4，按 CPU/GPU 超频档位映射） |
| `SetGamingProfile` / `GetGamingProfile` | ✅ 存在 | 配置类 API |
| `*FanGroupBehavior` / `*FanGroupSpeed` / `GetGamingFanTable` | ❌ | 本机型 provider 未导出（OEM 通过服务层适配） |

> 权限：WMI 方法在低完整性（sandbox）下返回「拒绝访问」，**普通用户 + 管理员令牌即可**，不需要升到内核。

### 2.4 温度 / RPM 来源（无驱动方案，已实测）

**WMI 的 `GetGamingSysInfo` 在本机是残废的**（id 1→0，id 2→`0x10000000000`，id 6/10→`2`），
`MSAcpi_ThermalZoneTemperature` 的 TZ00 恒为 60.1 °C（加满负载都不动，是死的）。
真正能用的数据源是 **OEM 服务管道**，协议已完整破解：

| 来源 | 结果 | 方式 |
|---|---|---|
| CPU 温度 | ✅ 74–89 °C（满载会上升，真实 die 传感器） | 管道 cmd 15, id=1 |
| CPU 风扇 RPM | ✅ 3529–5000 | 管道 cmd 15, id=2 |
| GPU 温度 | ✅ 55–59 °C（与 `nvidia-smi` 交叉验证一致） | 管道 cmd 15, id=10 |
| GPU 风扇 RPM | ✅ 3529–4109 | 管道 cmd 15, id=6 |
| 系统温度 | ✅ | 管道 cmd 15, id=3 |

管道协议（详见 [reverse/pipe/PAYLOAD-NOTES.md](../reverse/pipe/PAYLOAD-NOTES.md)）：

```text
管道    \\.\pipe\predatorsense_service_namedpipe   (服务 PSSvc, StartMode=Manual)
请求帧  i16 cmd | u8 numArg | { i32 len | payload }        cmd 15, arg = (1 | (id<<8)) as u32
响应帧  u8 numArg | i32 len | payload                       Get 返回 8 字节 u64
解码    status = raw & 0xFF (0=OK)   value = (raw>>8) & 0xFFFF   （无缩放）
```

> ⚠️ **发格式不对的帧会让 PSSvc 进程直接退出**，组包必须严格。

### 2.5 端到端验证（已实测，2026-11-12）

```text
auto baseline   cpu=69C gpu=57C  rpm=4109/4109
custom 100%     cpu=70C gpu=55C  rpm=5000/3947     ← 转速真的上去了
custom 30%      cpu=70C gpu=55C  rpm=3409/3529     ← 真的降下来了
back to auto    cpu=65C gpu=56C  rpm=3529/3529     ← Auto 归还成功
```

---

## 3. 和两个参考仓库的关系

| 仓库 | 定位 | 对本机的价值 |
|---|---|---|
| `cleyton1986/predator-sense` | Linux/Rust 重写（`protocol/src/lib.rs` 142 KB、`kernel/facer.c`、`hidraw`）| 协议字典 / EC 层参考；Windows 无用 |
| `hammadzaigham/PreySense` | **C#/.NET 10 + WPF 完整替代品**，含 `WmiController`、`FanCurveFollower`、`FanStage`、RGB、overlay | **最佳移植基座**；但它的 CPU PL1/PL2 调优依赖自带内核驱动 `PreySenseDriver.sys` → 正是被火绒拦的那个 |

### PreySense 曲线逻辑（可直接抄）
`FanCurveFollower.Evaluate(cpuTemp, gpuTemp, stages, delay)`：
- 每档 `{CpuRiseC, GpuRiseC, CpuDutyPercent, GpuDutyPercent}`，最多 12 档
- 任一路到达该档 rise 温度 → 升档；两路都低于上一档 rise → 降档
- `127` = 禁用该路 trip（哨兵值）
- 同档内两风扇取**较高**占空比
- 共享 0–5 s 迟滞
- 只在目标变化时下发，避免 EC 反复重启 PWM 产生啸叫

---

## 4. 本机实现形式（结论）

```
1 s 轮询 ──┬─ TZ00 (CPU °C)  ─┐
           └─ NVML  (GPU °C)  ─┴→ FanCurveFollower → duty → AcerGamingFunction.SetGamingFanSpeed
                                                  (首次先 SetGamingFanBehavior 0xC30009)
退出/崩溃 → SetGamingFanBehavior 0x410009 归还固件 Auto
```

- **零内核驱动**：火绒那条告警可以完全绕开（不装那个 .sys 就行）
- 需要：管理员令牌（WMI 方法权限）、.NET 10 SDK（若要编译 WPF GUI）
- 不能做的：CPU PL1/PL2 / PROCHOT / 电压 —— 那些必然要 MSR 驱动，会被杀软拦，且是真实高危面
- 可做：风扇曲线、模式切换（Quiet/Balanced/Performance/Turbo/Eco）、温度监控、Silent/Max、亮度、电池 80% 限制（`BatteryControl` 存在）

---

## 5. 风险与回滚

- 欠转：曲线下限设 ≥ 30%，或在 CPU > 85 °C 时强制 100%
- 失控：脚本退出/异常必须恢复 `0x410009`
- 与官方 Predator Sense **抢控制权**：跑自制程序前必须关掉官方 App 和 `Acer*Service`
- 已实测回写不影响：`0x410009` 下 EC 自行接管（回读值会停留在最后一次下发值，属正常）
