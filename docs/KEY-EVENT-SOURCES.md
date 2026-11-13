# KEY-EVENT-SOURCES.md — 狂暴模式键 / Predator 键 的事件通路

> 只读逆向调研，2026-11-13。**未改任何代码、未构建、未启动程序、未写注册表。**
> 结论基于：本机实测（用户已录的钩子日志 + 本次只读 WMI 类枚举 / PnP 枚举 / 导入表扫描 / 静态反汇编）
> 对象：`C:\Program Files\Acer\PredatorSense Service\` 官方栈 3.0.3170.0（PSSvc / PSAgent / PSAdminAgent），
> 以及 `decompiled\PredatorSense\`（GUI 反编译，本目录 122 个 .cs，注意：**GUI 里没有按键逻辑**）。

---

## 0. 一句话结论

| 键 | 通路 | 证据等级 |
|---|---|---|
| **Predator 键**（`E0 75`） | **标准键盘扫描码通路**。官方软件不用钩子，而是用 **Raw Input**（`RegisterRawInputDevices`，键盘 usage 1/6，`RIDEV_INPUTSINK`）接收 | **实测**（5 次按键 5 条记录）+ 静态（导入表/反汇编） |
| **狂暴模式键** | **ACPI-WMI extrinsic 事件**：`root\wmi:APGeEvent` → 属性 `EventDetail`(uint8[8]) → `PSAdminAgent.exe` 的 `treadstone::TsWmiEventSink::Indicate` → `HotKeyAction::Press_Turbo_key*` | 静态**强**证据；本机**字节值待实测**（未能确认到具体字节值，见 §7） |

```
狂暴键:  EC/BIOS ──ACPI Notify(WMID)──> WMI provider ──APGeEvent──> PSAdminAgent(TsWmiEventSink)
                                                                      └─ HotKeyAction::Press_Turbo_key[_Operating_Mode]
                                                                           └─ Turbo_mode_switch[_Operating_Mode] -> WMI 写模式/风扇 + OSD

Predator 键: 键盘控制器 ── E0 75 (scan 0x75, ext) ──┬─> WH_KEYBOARD_LL（我们已能收到 ✅）
                                                    └─> Raw Input（官方 PSAdminAgent 用它）
```

---

## 1. 三条候选通路，逐条判定

### 1.1 (a) 键盘钩子 / `RegisterHotKey` —— **排除**（官方不用）

| 检查 | 结果 | 位置 |
|---|---|---|
| 反编译 GUI 里 `SetWindowsHookEx` / `WH_KEYBOARD_LL` / `RegisterHotKey` / `GetMessage` 循环 | **0 命中** | `decompiled/PredatorSense/*.cs`（122 个文件全量 grep） |
| 官方 3 个进程 + 全部 DLL 的**导入表**里 `SetWindowsHookEx*` / `RegisterHotKey` | **0 命中** | 本次 pefile 扫描 `PredatorSense Service` + `Quick Access Service` 全部 exe/dll |
| 官方进程导入的相关 API | `RegisterRawInputDevices` / `GetRawInputData`（PSAdminAgent、QAAdminAgent、QALockHandler）、`keybd_event` / `SendInput`（只用于合成） | 同上 |

结论：官方**没有**用钩子抢键。因此 Predator 键的 `E0 75` 能被我们的 `WH_KEYBOARD_LL` 收到，是"两套消费者并行"的正常结果，不是运气。

### 1.2 (b) ACPI-WMI 事件 —— **确认**（狂暴键走这条）

本机 `root\WMI` 实测存在的 Acer 事件类（`Get-CimClass`，只读）：

| 类名 | 基类 | Qualifier（实测） | 属性 |
|---|---|---|---|
| `APGeEvent` | `WMIEvent` | `dynamic=True`, `provider=WmiProv`, `guid={676AA15E-6A47-4D9F-A2CC-1E6D18D14026}`, `Description=APGe WMI Event, Version 2.81` | `EventDetail` **UInt8Array, MAX=8, WmiDataId=1**；`SECURITY_DESCRIPTOR`、`TIME_CREATED`、`Active`、`InstanceName` |
| `AcerGenericEvent` | `WMIEvent` | `dynamic=True`, `provider=WmiProv`, `guid={2E6B3CE2-6A58-4AF2-8767-4640CA8A38E3}`, `Description=Acer Generic WMI Event, Version 2.81` | 同结构：`EventDetail` UInt8Array MAX=8 |

**谁在订阅**（关键证据：只有一方有订阅串）：

| 二进制 | `SELECT * FROM APGeEvent` | `EventDetail` | 说明 |
|---|---|---|---|
| **PSAdminAgent.exe** | ✅ | ✅ | **就是它**：`treadstone::TsWmiEventSink::Initialize` / `::Indicate` |
| QAAdminAgent.exe / QALockHandler.exe / QAWiFiPowerSwitch.exe | ✅ | ✅ | Quick Access 栈（Fn 键/锁屏），不是 Predator 栈 |
| **PSSvc.exe** | ❌ | ❌ | 只有**方法类** WQL：`SELECT * FROM AcerGamingFunction` / `AcerBiosConfigurationTool` / `APGeAction` |
| PSAgent.exe | ❌ | ❌ | 二进制扫描，无事件订阅 |
| PredatorSense.exe（GUI 3.0.3196.0，Store 版） | ❌ | ❌ | 二进制扫描 `APGeEvent`/`EventDetail`/`AcerGenericEvent` 全无；只有 `predatorsense_service_namedpipe`（走管道） |

`PSAdminAgent.exe` 里的完整证据链（x64，镜像基址 `0x140000000`）：

| 串 / 符号 | 字符串 VA | 被引用处 |
|---|---|---|
| `SELECT * FROM APGeEvent` | `0x140096C78` | `0x14003230C`（在函数 `0x140032000` 内，该函数调用 `TsWmiEventSink::Initialize`） |
| `treadstone::TsWmiEventSink::Initialize` | `0x140096280` | 函数 `0x14002DB70` |
| `treadstone::TsWmiEventSink::Indicate` | `0x140096350` | 函数 **`0x14002DDD0`**（事件分发主体） |
| `EventDetail` | `0x1400962F8` | `0x14002DEA4`（`call [rax+0x20]` = `IWbemClassObject::Get`，随后 `0x14002DED4` 拷成字节向量） |
| `HotKeyAction::Press_Turbo_key` | `0x140098960` | 函数 `0x140040060` |
| `HotKeyAction::Press_Turbo_key_Operating_Mode` | `0x140098C60` | 函数 `0x140041120` |
| `HotKeyAction::Turbo_mode_switch` | `0x140098AE0` | 函数 `0x140040380` |
| `HotKeyAction::Turbo_mode_switch_Operating_Mode` | `0x140098B20` | 函数 `0x140040BF0` |
| `HotKeyAction::key_action` | `0x140097FD8` | 函数 `0x14003B870` |

> 命名解释：`WMIFunction.cs`（GUI 反编译）**名字叫 WMI，其实是命名管道客户端**（`reverse/pipe/PAYLOAD-NOTES.md` 已证）。
> 真正的 WMI 事件消费在 **PSAdminAgent**（native C++，`treadstone::` 是 Acer 内部代号，PSSvc 里也有 `treadstone::TsClientCommandProcessor`）。

### 1.3 (c) 命名管道 —— **排除**（没有按键事件 cmd）

| 检查 | 结果 |
|---|---|
| `TsDotNetLib.decompiled.cs:218-273` 的 `ServiceCommand` 枚举（0-based = 管道 cmd 号） | 全部是 `WMIGet/Set*`、`Reg*`、`CreateProcess*`、`CPU*/GPU*`、`BIOS*`、`Get/SetOperationMode` —— **没有"按键事件/通知/推送"条目** |
| 协议方向 | 一问一答，**客户端发一个命令收一个回包**（`PAYLOAD-NOTES.md` §1，`WMIFunction.cs:15-25`）；服务端不主动 push |
| PSSvc 里与键相关的串 | `Enable/Disable Turbo Key WMI SetGamingLED/MiscSettingFunction` 之类**只在 PSAdminAgent** 里；PSSvc 只有 `kSvcCmdSetOperationMode`（被写方） |

结论：管道是**执行层**（谁去写模式），不是**事件层**（谁先知道你按了键）。

### 1.4 附加发现：(d) Raw Input —— Predator 键在官方侧的真正落点

`PSAdminAgent.exe` 函数 `0x140032000` 里连续注册了 3 个 `RAWINPUTDEVICE`（`cbSize=0x10`，`uiNumDevices=1`，`dwFlags=0x100` = `RIDEV_INPUTSINK`）：

| 注册点 | `usUsagePage` | `usUsage` | 含义 |
|---|---|---|---|
| `0x1400321C0` | `0x0001` | `0x0006` | 标准键盘（**`E0 75` 从这里进**） |
| `0x1400321F3` | `0x0088` | `0x0001` | 非标准页（疑似 Acer EC 自定义 collection，**待实测**） |
| `0x14003221C` | `0x0001` | `0x0002` | 鼠标 |

`WM_INPUT` 处理函数 `0x1400324D0`，用 `GetRawInputData(… 0x10000003 = RID_INPUT …)` 取 `RAWINPUT` 后按 `dwType`（`[r12]`）分支：

| 分支 | 处理 | 地址 |
|---|---|---|
| `dwType == 1`（`RIM_TYPEKEYBOARD`） | 键盘路径（`E0 75` 走这里） | `0x1400334B0` |
| `dwType == 2`（`RIM_TYPEHID`） | 取 `RAWHID.bRawData`（`0x18+8 = [r12+0x20]`），判 `bRawData[1]==0x81 && bRawData[2]==0xFF`（`and eax,0xFFFF00; cmp eax,0xFF8100`，忽略 report id 即 `bRawData[0]`） | `0x1400325A6`-`0x1400325B7` |

→ **这解释了为什么我们的 LL 钩子和官方能同时看到 Predator 键**：Raw Input 与 LL 钩子不是互斥关系。
→ 厂商 HID 报告 `?? 81 FF` 被官方当成按键处理，但**哪个物理键产生 `81 FF` 本次未能确认**（我们的 `HidModeKeyWatcher` 里等的是 `04 85 FF`，字节不同，见 §7 待实测 #4）。

---

## 2. `APGeEvent.EventDetail` 字节格式（**从 `TsWmiEventSink::Indicate` 反汇编得出**）

函数 `0x14002DDD0`：`Get("EventDetail")` → 取 `VARIANT` 的字节数组 → 拷到栈上 `[rsp+0xa0..]` → `movzx ecx, bl` 取 **[0]**，再 `sub/cmp` 链分发。

```asm
14002DEE1  movzx ecx, bl                  ; ecx = detail[0]
14002DEE4  sub   ecx, 1
14002DEE7  je    0x140031E65             ; detail[0] == 1
14002DEED  sub   ecx, 6
14002DEF0  je    0x14002FAE3             ; detail[0] == 7   ← 游戏键族
14002DEF6  sub   ecx, 1
14002DEF9  je    0x14002F9A5             ; detail[0] == 8   ← 电源
14002DEFF  sub   ecx, 1
14002DF02  je    0x14002F97E             ; detail[0] == 9
14002DF08  cmp   ecx, 3                  ; detail[0] == 12  ← 高级设置键
14002DF0B  jne   0x140031F2B             ; 其余一律忽略
```

| 字节 | 含义 | 依据 |
|---|---|---|
| **[0]** | 事件类别 | 上面的分发链（反汇编，确定性高） |
| **[1]** | 子事件 id | 各分支 `movzx eax, byte ptr [rsp+0xa1]`，如 `0x14002FAE3`、`0x14002F992`、`0x14002F9B9` |
| **[2]** | 附加参数（自定义键号 / 背光状态） | `movzx ebx, byte ptr [rsp+0xa2]`（`0x14002FB07`、`0x1400300E3`）；日志 `Return detail[2]: 0x%X`（`0x140031EA3`）、`Custom key number: 0x%X`（`0x1400300F8`） |
| [3..7] | 未使用（`MAX=8`，本固件分支只读到 [2]） | 反汇编 |

> ⚠️ 旧笔记里 `[6, x]` 的猜测**不适用本机**：dispatch 表里没有 6，只有 **1 / 7 / 8 / 9 / 12**。

### 2.1 类别 / 子类 → 官方处理（`switch` 到哪个方法）

| detail[0] | 子条件 | 官方处理 | 依据 |
|---|---|---|---|
| 1 | — | 键盘背光状态变化：日志 `Keyboard Backlight state change` + `Return detail[2]: 0x%X`，写 `BK_Hotkey_Number` / `KBBackLight_State`，刷新 OSD 名 | `0x140031E65`、`0x140031E83`、`0x140031EA3` |
| **7** | **[1]=1** | 热键 Profile 通知：读 `SOFTWARE\OEM\PredatorSense\KeyAssignment`，`FindWindow("PSAGENT","PSAgent")`，`PostMessage(0x8007, wParam=detail[2])` | `0x14002FAE3`→`0x14002FB42`；串 `PSAgent`/`PSAGENT` @`0x14008F468`/`0x14008F478` |
| **7** | **[1]=2** | **自定义键**：日志 `Custom key number: 0x%X`（= detail[2]），调 `HotKeyAction::key_action(group,key)` @`0x14003B870`；它读 `C:\ProgramData\OEM\PredatorSense\ProfilePool\HotkeyProfilePool\…\Main.xml` 的 `type`/`option`/`userDefineName`，再分到 `do_fan_switch` / `do_cpu_oc_switch` / `do_gpu_oc_switch` / `do_advance_setting` / `do_sound_mode_switch`，并弹 OSD `Physicalbutton%d` | `0x140030019`→`0x1400300F0`；`0x140043320` |
| **7** | **[1]=4** | **狂暴 / Turbo / 模式键** → 见 §2.2 | `0x140030FFD`（`cmp al,4`）→ `0x140031005…` |
| 8 | — | 电源状态变化（AC/电池/PD）：`kAdmAgtPowerStatusChange`，`notify 0x8004`，`detail[1]` 位标志：`bit0`=AC，`bit2..3`=PD | `0x14002F9A5`、`0x14002F9DA`、`0x14002FA56`(`and ebx,4`) |
| 9 | — | `notify 0x8005`，带 `detail[1]`（用途未定） | `0x14002F97E` |
| 12 | `[1]==1` 且 全局"OperationMode 支持" ∈ {4,5} | 高级设置热键（Sticky Key / Win+Menu 键 / LCD Overdrive），读 `SOFTWARE\OEM\PredatorSense\AdvanceSettings` | `0x14002EA28`…`0x14002EA7B`；`HotKeyAction::do_advance_setting` |

### 2.2 `detail[0]=7 && detail[1]=4` 的完整分发（狂暴键）

```asm
14002FAE3  movzx eax, byte ptr [rsp+0xa1]    ; detail[1]
14002FAEB  cmp   al, 1
14002FAED  jne   0x140030019                 ; -> [1]=2 自定义键分支
14002FFFF  ...                              ; [1]=1 分支
140030019  cmp   al, 2
14003001B  jne   0x140030FFD                 ; -> [1]=4 分支
140030FFD  cmp   al, 4
140030FFF  jne   0x140031F2B                 ; 否则忽略
140031005  …                                 ; === Turbo / Mode 键处理 ===
```

处理体关键步骤（`0x140031005` 起）：

| 步骤 | 内容 | 地址 |
|---|---|---|
| 1 | 读全局"Operating Mode 支持"（`HKLM\SOFTWARE\OEM\PredatorSense` `Operation_Mode_Support`，见 `Handover.md` §5） | `0x140031013` (`mov ecx,[rip+0x79C6F]`) |
| 2 | 读当前 **Turbo 键 LED 状态**：`GetGamingLEDBehavior` 返回值 `>>8 & 3`（bits 8..9）存 `[r13+0x24]` | `0x1400310F8`-`0x14003110F` |
| 3 | 状态 == 1 → 打 `Disable Turbo Key…` 日志 + 写 `SetGamingLEDBehavior`（Disable 侧 `0x400001`，与 `FanControlPage.cs:438` 同值）→ 调 **`Press_Turbo_key(false)`** | `0x140031141`、`0x140031219` |
| 4 | 状态 != 1 → `Enable Turbo Key…` + 写 `SetGamingLEDBehavior`（`0x10001`）→ 调 **`Press_Turbo_key(true)`** | `0x14003125B`、`0x140031335` |
| 5 | 若支持 Operating Mode → 改走 **`Press_Turbo_key_Operating_Mode`**（`0x140041120`），由它调 `Turbo_mode_switch_Operating_Mode`（`0x140040BF0`） | `0x140031624`、`0x1400317F7`、`0x140041398` |

`Press_Turbo_key`(`0x140040060`) 再调 `Turbo_mode_switch`(`0x140040380`)：读 `SOFTWARE\OEM\PredatorSense\Overclock\*`、查 CPU/GPU OC 支持、`g_cpu_lock`/`g_gpu_lock` 闸门，然后写风扇/OC 状态。
`Press_Turbo_key_Operating_Mode` 的路径还会重写 `FanControl` 相关值并走 `WMI SetGamingFanGroupBehavior`。

> **模式循环顺序：未能确认。** 静态码里 `Turbo_mode_switch*` 读的是当前 OC/风扇/模式状态再决定，没看到"0→1→2→3→4→5"的硬编码环。
> 唯一可靠的顺序证据在 GUI：`decompiled/PredatorSense/gameSyncSettingListItem_OperationMode.cs:166-198` =
> `0=Quiet(默认?) 1=Default 2=Render 3=Edit 4=Extreme 5=Turbo`，`MainWindow_*.cs:98` 数组
> `ModeQuiet / ModeNormal / ModeRender / ModeEdit / ModeExtreme / ModeTurbo`。
> 狂暴键到底是「切换 Turbo ↔ 上一模式」还是「按 0..5 轮转」，**必须实测**（见 §7）。

---

## 3. 可执行订阅骨架（C#）

**标注**：`【实测】` = 本机 WMI 类枚举/已测事实；`【反编译推断】` = 从 PSAdminAgent 反汇编得出，尚无本机字节实证。

```csharp
// 【实测】类名与命名空间：Get-CimClass root/WMI APGeEvent
using System.Management;

const string Scope       = @"root\wmi";         // 【实测】
const string EventClass  = "APGeEvent";         // 【实测】（回退 AcerGenericEvent，同样【实测】存在）
// 注意：extrinsic（dynamic）事件不需要 WITHIN，写了反而变成轮询。
var scope   = new ManagementScope(Scope);
var query   = new WqlEventQuery($"SELECT * FROM {EventClass}");   // 【实测】PSAdminAgent 用的就是这句
var watcher = new ManagementEventWatcher(scope, query);

watcher.EventArrived += (_, e) =>
{
    // 【实测】属性名 EventDetail，类型 UInt8Array（MAX=8）
    if (e.NewEvent?["EventDetail"] is not byte[] d || d.Length < 2)
        return;

    byte cat = d[0];
    byte sub = d[1];
    byte arg = d.Length > 2 ? d[2] : (byte)0;

    switch (cat)
    {
        case 0x01: /* 【反编译推断】键盘背光状态变化 */               break;
        case 0x07: // 【反编译推断】游戏键族
            switch (sub)
            {
                case 0x01: /* hotkey profile 变更通知 */              break;
                case 0x02: /* 自定义键 arg = 键号 */                  break;
                case 0x04: HandleTurboKey();                          break; // ← 狂暴/模式键【反编译推断】
            }
            break;
        case 0x08: /* 电源：bit0 = AC，bit2..3 = PD */                break; // 【反编译推断+已有实测代码】
        case 0x09: /* notify 0x8005 */                               break;
        case 0x0C: /* 高级设置热键 */                                 break;
    }
};

watcher.Start();   // 必须在 MTA 线程上；事件回调也在 MTA
// Dispose: watcher.EventArrived -= …; watcher.Stop(); watcher.Dispose();
```

要点：
1. **MTA 线程**：`ManagementEventWatcher` 的事件回调跑在 MTA；`PreySense.Core/Hardware/TurboKeyWatcher.cs` 已经这么做了（`SetApartmentState(ApartmentState.MTA)`）。
2. **UI 线程**：回调里只投递，不做重活（`MainViewModel.OnTurboKeyPressed → _dispatcher.InvokeAsync` 已符合）。
3. **不要加 `WITHIN`**；`WITHIN` 只对 intrinsic/`__Instance*` 事件有意义。
4. `APGeEvent` 打不开时再退 `AcerGenericEvent`（两者结构相同，见 §1.2 表）。

---

## 4. 我们代码里的现状

| 文件 | 现状 | 判定 |
|---|---|---|
| `app\Fork\PreySense.Core\Hardware\TurboKeyWatcher.cs` | 已实现 WMI 订阅：`APGeEvent`，失败退 `AcerGenericEvent`（`:73-77`）；`TryParseEventDetail` 解析 `byte[]`/数字（`:117-170`）；`OnEventArrived` 读 `EventDetail`（`:204`）；`ModeKeyCategory = 0x07`、`ModeKeyId = 0x05`（`:34-35`）；`IsPhysicalModeKey`（`:179-194`）是一堆启发式；`category == 0x08` → `TryParseAcerPowerEvent`（`:218-225`） | **通路对了，id 错了**：本机应是 `[0]=7 && [1]=4`，代码里 `0x05` 与多种兜底会把 `[1]=2` 的自定义键、`[1]=1` 的 profile 通知也当模式键 |
| `app\Fork\PreySense.Core\Hardware\HidModeKeyWatcher.cs` | 按硬件 id `1025174B` 找 HID collection（`:19`），按报告 `04 85 FF` 判键（`:101`）；`IsWindowsExclusiveHidPath` 跳过 `\kbd`/`\mou`（`:78-84`） | **本机是死路**：`Get-PnpDevice` 实测**没有** `1025174B`；本机键盘是 `VID_3151&PID_4015`（vendor collection 在 `MI_01&COL05` / `MI_02`）。这条是 PHN16 的代码，PH317-55 不适用 |
| `app\Fork\PreySense.Core\Hardware\WmiController.PowerSource.cs` | `StartPowerSourceWatch()` 自己又建了一个 `APGeEvent` watcher（`:12`），只处理 `detail[0]==8`（`:26-42`） | **重复订阅**：和 `TurboKeyWatcher` 订阅同一个类（两份消费者）。应合并 |
| `app\Fork\PreySense.Core\Hardware\WmiController.cs` | `:20` `_powerSourceWatcher` 字段、`:852` 退订 | 同上，只是字段 |
| `app\Fork\PreySense.Frontend\Services\KeyboardHook.cs` | `SC_PREDATOR = 0x75`（`:15`）——**与实测一致**；`SC_MODE_KEY = 0x85`（`:16`）——来自 PHN16，本机收不到；`IsChassisModeKey`（`:103-116`）还兜底 `vk==0xFF/0x00 && scan!=0` 与 `0x6A/0x6B/0x7E`；亮度键映射（`:118-137`） | Predator 键**已经能认**；`SC_MODE_KEY=0x85` 在本机是**永不命中**的死分支；`0xFF+任意scan` 的兜底有误吞风险 |
| `app\experiments\Predex.WinUI\Services\KeyboardHookService.cs` | `VK_PREDATOR = 0xCC`（`:18`）、`VK_MODE_KEY = 0xC3`（`:19`） | **与实测不符**（实测 `vk=0xFF, scan=0x75`）。这是 WinUI 试验分支，产品主线是 `app\Fork`（WPF） |
| `app\Fork\PreySense.Frontend\ViewModels\MainViewModel.cs` | `:138-146` 建 `TurboKeyWatcher` + `HidModeKeyWatcher`；`:328` `TurboKeyPressed → HandleModeKeyPressed()`；`:330-343` `CyclePerformanceMode(current)` 切模式；`:340` 打 `mode 0x.. (was 0x..)` | 接线已通；循环顺序需按实测校准 |

---

## 5. 建议改动（**只写方案，未动代码**）

| # | 文件 | 改法 | 风险 |
|---|---|---|---|
| 1 | `TurboKeyWatcher.cs` | 把 `ModeKeyId = 0x05` 改成 `0x04`；`IsPhysicalModeKey` 收紧为 `category==0x07 && subEventId==0x04`（其余启发式删掉或降级为 debug 日志）；`[1]=2`（自定义键）单独抛 `CustomKeyPressed`，`[1]=1` 只记日志 | 低：纯判定逻辑，不写硬件 |
| 2 | `TurboKeyWatcher.cs` | 每次事件把 `EventDetail` 原样打日志（`BitConverter.ToString(d)`）——**这是补齐"实测字节"的开关**，跑一次按几次键就够 | 低 |
| 3 | `WmiController.PowerSource.cs` | 删掉独立 watcher，`PowerSourceChanged` 改由 `TurboKeyWatcher` 的 `PowerSourceReceived` 转接（代码里已有这个事件） | 低；注意两处都在用就别双订阅 |
| 4 | `HidModeKeyWatcher.cs` | PH317-55 上直接不启动（按 `1025174B` 存在性判定），或加 `enabled:false` 开关；别投入精力 | 低（现在它本来也什么都做不了，只是白开线程） |
| 5 | `PreySense.Frontend\Services\KeyboardHook.cs` | `SC_MODE_KEY` 保留但不再作为模式键来源（本机无此扫描码）；把 `IsChassisModeKey` 的 `(vk==0xFF\|\|0x00)&&scan!=0` 兜底去掉或白名单化，避免吞掉其它 OEM 键 | 中：误吞会导致其它键失灵，改完要逐键回归 |
| 6 | `PreySense.Frontend\ViewModels\MainViewModel.cs` | `CyclePerformanceMode` 的环按实测顺序校准（见 §7） | 中：会真写模式 |
| 7 | 新增（可选） | `TurboKeyWatcher` 增加 `TurboKeyPressed` 去抖（现在 80 ms，事件重复到达时够用，但要防 OSD 抖动） | 低 |

**不建议**：给 `TurboKeyWatcher` 加 `WITHIN` 轮询；给 PSSvc 加事件订阅（它本来就没有，改了会破坏 `Handover.md` §5 的协议认知）。

---

## 6. 风险（对照 `Agent.md` 红线）

| 风险 | 判断 | 说明 |
|---|---|---|
| 订阅 `APGeEvent` 会不会和 PSSvc 抢？ | **不会抢** | WMI extrinsic 事件是**消费者模型**：每个 `ManagementEventWatcher` 是独立消费者，所有订阅者都收到同一事件。而且 **PSSvc 根本不订阅**（§1.2 表），订阅方是 PSAdminAgent。 |
| 会不会影响风扇/模式写入？ | **间接会，但不是订阅的锅** | 真正的互斥在 WMI **方法调用**：`Device.dll` 里有串 `AOD_WMI_HANDLER_MUTEX`（命名互斥量），说明 Acer WMI handler 是全局串行化的。我们的 `SetGamingMiscSetting`/风扇写入会与 PSSvc / PSAdminAgent 争这把锁 → 可能超时/返回错误。按现有纪律处理：**写后 cmd 33 读回校验 + 失败重试**，不要盲重试风暴。 |
| 会不会和官方栈"双击"？ | **会** | 若用户同时开官方 PredatorSense（PSAdminAgent 在跑），同一次狂暴键两边各处理一次 → 模式切两次。现状 `src\Run-GUI.ps1` 已先关官方进程，**必须保持**（`Handover.md` §9 第 7 条）。 |
| 违反红线 1（内核驱动/安全启动） | **不涉及** | 全程用户态 WMI 订阅。 |
| 违反红线 2（改协议常量/帧结构/cmd 号） | **不涉及** | 订阅事件不是管道协议；模式写入仍走现有 cmd 32/33，不动常量。 |
| 违反红线 3（停用/重配 PSSvc） | **不涉及** | 只读订阅；可选 `Start-Service` 自愈逻辑不变。 |
| 违反红线 4（畸形帧搞崩 PSSvc） | **不涉及** | 订阅不发帧。 |
| 违反红线 8（每秒重写风扇） | **注意** | 事件驱动本身不会高频；但"每次按键就重放风扇"要沿用现有"切换后重放 + 5 s 看门狗"，别在回调里循环写。 |
| 权限 | **需管理员** | `root\wmi` 事件订阅在非提权下可能 `Access Denied`；Predex 已有 `requireAdministrator`。 |
| 双 watcher 开销 | **中低** | 现在 `WmiController.PowerSource` 与 `TurboKeyWatcher` 各订阅一次 → 合并成一份（§5 #3）。 |

---

## 7. 待实测（**本次未执行**，都在红线内、只读）

| # | 验证 | 做法（只读，不改代码不写注册表） | 期望 |
|---|---|---|---|
| 1 | **狂暴键的 EventDetail 字节** | 提权 PowerShell 订阅 `SELECT * FROM APGeEvent`，打印 `EventDetail` hex；按 3 次狂暴键 | 出现 `07 04 …`（若出现 `07 02` 说明是自定义键通道；若**什么都没有** → 说明本机走 §1.4 的 `0x0088/0x0001` Raw Input 或固件直处理，需改抓 Raw Input） |
| 2 | **Predator 键是否也进 WMI** | 同上，按 3 次 Predator 键 | 期望**没有** APGeEvent（它走键盘通路） |
| 3 | **模式循环顺序** | 记录"当前模式(cmd 33 / `CurrentOperationMode`) → 按一次键 → 再读"连按 7 次 | 得到真实环（是否 `5 ↔ 上一模式`，或 `0..5` 轮转） |
| 4 | **厂商 HID 报告 `?? 81 FF` 是哪个键** | 用 Raw Input（`RIDEV_INPUTSINK`，usage page 1/usage 6 + `0x0088/0x0001`）或 HID 读取 `HID\VID_3151&PID_4015&MI_01&COL05` / `MI_02`，按狂暴键/Predator 键各 3 次 | 判断狂暴键是不是走 HID 而不是 WMI（§1.4） |

最小只读捕获骨架（**待执行**）：

```powershell
# 只读：订阅事件并打印 hex，不写任何硬件
$q = New-Object System.Management.WqlEventQuery "SELECT * FROM APGeEvent"
$w = New-Object System.Management.ManagementEventWatcher (New-Object System.Management.ManagementScope "root\wmi"), $q
Register-ObjectEvent $w EventArrived -Action {
  $d = $Event.SourceEventArgs.NewEvent["EventDetail"]
  "{0}  {1}" -f (Get-Date -f HH:mm:ss.fff), (($d | ForEach-Object { $_.ToString('X2') }) -join ' ')
}
$w.Start()
# …按几次键…  Ctrl+C 后： Unregister-Event *; $w.Stop(); $w.Dispose()
```

---

## 8. 证据索引（可复核）

| 材料 | 路径 / 命令 |
|---|---|
| 官方按键实测日志（Predator 键 5 次 `vk=0xFF scan=0x75`） | `docs\predator-key-capture.txt:22-26` |
| 官方栈版本 | `C:\Program Files\Acer\PredatorSense Service\{PSSvc,PSAgent,PSAdminAgent}.exe` = **3.0.3170.0**（x64，2021-07-14） |
| WMI 事件类实测 | `Get-CimClass -Namespace root/WMI -ClassName APGeEvent`（含 `EventDetail` MAX=8）、`AcerGenericEvent` |
| 事件订阅方确认 | `PSAdminAgent.exe` 含 `SELECT * FROM APGeEvent` / `EventDetail` / `treadstone::TsWmiEventSink::*`；PSSvc/PSAgent 不含 |
| 无钩子确认 | 全 Acer exe/dll 导入表扫描：无 `SetWindowsHookEx*` / `RegisterHotKey` |
| GUI 无按键逻辑 | `decompiled\PredatorSense\` 122 个 .cs 全量 grep：`SetWindowsHookEx`/`WH_KEYBOARD_LL`/`RegisterHotKey` = 0 命中 |
| 管道无按键 cmd | `decompiled\TsDotNetLib\TsDotNetLib.decompiled.cs:218-273`（`ServiceCommand` 枚举） |
| 热键 `type` 表 | `decompiled\PredatorSense\CommonFunction.cs:959-...`（`Get_type_and_option_name`） |
| hotkey `type`→UEIP 映射 | `decompiled\PredatorSense\HotkeyProfileXML.cs:185-211` |
| 反汇编产物（本次） | `.tmp\key-research\{sink_disasm.txt, refs.json, PSAdminAgent.u.txt, PSAdminAgent.a.txt, *.py}` |

### 8.1 热键 `type` 编号表（`CommonFunction.Get_type_and_option_name`，`CommonFunction.cs:959` 起）

`type`（XML `Main.xml` / `HKLM\SOFTWARE\OEM\PredatorSense\KeyAssignment\GroupN:typeM` 用这套 0-based 编号）：

| type | 含义（MUI key） | option 含义 |
|---|---|---|
| 0 | CPU 超频切换（`MUI_CPU_OC_Switch`） | 0..4 不同档位轮转/互切 |
| 1 | GPU 超频切换（`MUI_GPU_OC_Switch`） | 同上 |
| 2 | 风扇切换（`MUI_Fan_Switch`） | 0=Auto>Max>Custom，1=Auto<>Max，2=Max<>Custom，3=Auto<>Custom |
| 3 | 启动应用（`MUI_Launch_App`） | — |
| 4 | 高级设置（`MUI_ED_Advance_Setting`） | 0=粘滞键，1=Win+菜单键，2=LCD Overdrive |
| 5 | 宏（`MUI_Macro`） | — |
| 6 | 无（`MUI_None`） | — |
| **7** | 启动应用（`MUI_Launch_App`，与 3 同类） | — |
| 8 | 游戏直播（`MUI_Gameplay_Broadcasting` / Mixer） | 固定 |
| 9 | 系统性能切换（`MUI_Sys_Performance_Switch`，Normal<>Extreme） | 固定 |
| 10 | Acer TrueHarmony 音效（`MUI_Acer_TrueHarmony`） | DTS 音效模式 |

`type` → `HKLM\SOFTWARE\OEM\PredatorSense\UEIP\Hotkey\hotkeytypeM`（`"<n>,<flag>"`）的换算（`HotkeyProfileXML.cs:185-211`）：
`type<=3 → n=type`；`type∈{5,6} → n=type-1`；`type=4 → option 0/1/2 → n=6/7/8`；`type=7 → n=9`。

> **"狂暴键是哪个 type"**：它**不是**可分配的 `type`。狂暴/模式键是固定的硬件键（`HotKeyAction::Press_Turbo_key[_Operating_Mode]`），不经过 `HotkeyProfilePool` 的 type 表；
> `type=7`（UEIP 9）是"启动应用"，官方 PredatorSense V30 的 `[SpecialHotkey] SpecialType` 走的是 `PredatorSense.ini`（`MacroSettingPage.cs:211`，`1`=默认宏 Mixer/Start recording，`2`=MagForce 键），与本机狂暴键无关。
