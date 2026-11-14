# Performix 交接文档

> 交接时间：2026-11-14
> 当前版本：`0.1.0-alpha.2`（prerelease）
> 仓库：https://github.com/YunxiRamito/Performix
> main HEAD：`8e1a7eb`（本地与远端一致，工作区干净）

---

## 一、当前状态一句话

首个可对外分发的测试版已发布，安装器、应用内更新、设置区重构、新图标、开机自启全部落地并实机验证。**只有 PH317-55 一台机器验证过**，其余机型靠社区反馈铺开。

---

## 二、已发布的东西

### Release

`v0.1.0-alpha.2`，prerelease，三个资产：

| 资产 | 大小 | 说明 |
|---|---|---|
| `Performix-win-x64.zip` | 33,489,786 | 应用本体，根目录直接可见 `Performix.exe` |
| `PerformixSetup.exe` | 11,941,554 | 单文件安装器，自带运行时补装 |
| `update.json` | 3,000 | 更新清单，同时提交到仓库根供加速源读取 |

线上回读校验过：zip 的 sha256 `3d504807e6e7a8f7…` 与仓库根清单、Release 清单三处一致。

### 更新源（六个，按官方组 → 加速组降级）

`github-releases` / `npm-official` / `gh-proxy` / `ghproxy.net` / `jsdelivr-gh` / `npmmirror`

---

## 三、这轮做了什么

### 1. 应用内更新

- 更新页五状态：未检查 / 有新版 / 下载中 / 更新失败 / 需手动安装
- 六个源自动降级，下载中断点续传，下载中可换源
- 包大小与 SHA-256 双硬门禁，不符直接拒收并删包重下
- 失败态给出原因和下一步动作，完整哈希写日志，页面只显示前 8 位
- 替换前备份旧 exe，失败保留 `backup\`
- `minVersion` 真正生效：低于最低版本拒绝就地升级，转手动安装

**默认自动更新**（用户后来改的需求，覆盖了原来「只检查不安装」的立场）：

- 启动延迟 45 秒检查，之后 6 小时间隔
- 检查 → 下载 → 校验 → 等空闲时安装
- 用户正在调曲线、切模式、或有对话框开着时会等待，不打断
- 右下角信息窗显示状态，**明确 NOTOPMOST**，不遮托盘图标
- 可在更新页关闭，关闭后完全不联网
- 后台失败静默，只记日志

### 2. 安装器（`PerformixSetup.exe`）

- 单文件、裁剪、11.4 MB，不依赖 .NET 运行时即可启动
- 检测 .NET 10 桌面运行时，缺了用 8 线程分段下载补装（断点续传 + SHA-512 校验）
- 装到 `%LOCALAPPDATA%\Performix\app\<version>\`，保留最近两个版本
- 建开始菜单快捷方式，`--uninstall` 卸载（保留用户配置并提示位置）
- 支持 `--offline <zip>` / `--version <tag>` / `--dir` / `--no-launch` / `--desktop-shortcut` / `--silent`

### 3. 设置区重构（六板块）

常规 / 个性化 / 调校 / 服务 / 更新 / 关于

- 入口是图标 + 名称，无多余描述文字（写进 `docs/UI-DESIGN-RULES.md` §7「设置页文案纪律」）
- **常规**：语言（简中/繁中/EN/日本語）、开机启动、总在最前、特殊键唤醒、电源配置（电池/电源两级各五档）、USB 关机通电、电池充电限制
- **个性化**：主题（外观模式/窗口风格/材质/强调色）+ 界面开关
  - 状态条 / GPU 栏 / 刷新率栏 / 小组件栏四个面板开关
  - 状态条内容可勾选：硬件温度 / 硬件占用 / 硬件频率 / 风扇转速，全部真实数据源，按需采样
- **服务**：按机器品牌显示（ASUS / Acer / Lenovo / MSI / HP / Dell / Alienware / Razer / GIGABYTE），品牌识别不出用中性文案；批量停止有二次确认；`PSSvc` 在保护名单里永不禁用
- **更新 / 关于**：从原 MiniMetaPage 拆成两个独立页

### 4. GPU 模式改为三档

混合模式 / 核显模式 / 独显直连

- 能力判定抽成纯函数 `GpuModes.Evaluate`，六个探针布尔
- 不支持的档位置灰并写明原因，不做假开关
- 当前模式识别：DDS 有证据用 DDS，否则面板由 iGPU 驱动判为混合，真读不到才 N/A

### 5. 图标与外观

- 新图标：G3 连续曲率超椭圆 + 白色码表，全尺寸 ico（16~256）
- 标题栏标记跟随强调色实时染色（Composition 遮罩刷），颜色过浅自动调深
- `assets/brand/build-icons.py` 一条命令从 4K 母版重建全部产物

### 6. 窗口行为

- **总在最前**：presenter + `SetWindowPos` 双写；置顶时不再失焦自动隐藏
- **特殊键唤醒**：Predator 键走低级键盘钩子（`E0 75`），开关接在 `ToggleRequested` 上
- **开机自启**：首次运行默认开启，只驻留托盘不弹窗口；用户关掉一次后不再自动开启
- 修复：隐藏动画未完成会取消后续显示、连续导航导致窗口尺寸卡中间值、插拔 USB RGB 设备灯效列表不刷新、风扇页返回后转速不更新

### 7. 界面细节修复

- 展开箭头方向：收起朝上、展开朝下
- 首启箭头绕 (0,0) 旋转导致错位，改为等测量完成再定 `CenterPoint`
- 去掉鼠标悬停冒出的 "Esc" 标签（`KeyboardAcceleratorPlacementMode.Hidden`）
- OSD 切分辨率后停在旧坐标（实测跑到屏幕外 990 px），改为每次显示前重取 workArea + DPI，挂 `WM_DISPLAYCHANGE` / `WM_DPICHANGED`

### 8. 发布流水线

- `scripts/build-app-zip.ps1` / `build-installer.ps1` / `publish-release.ps1` / `get-release-notes.ps1`
- `.github/workflows/release.yml`：tag 触发，构建 + 测试 + 发布三资产
- CI 发布后把自身生成的 `update.json` 回写 main，避免加速源指向本地构建的包
- Release 说明从 `CHANGELOG.md` 对应版本一节取，不再堆 git log
- jsDelivr 的 `@main` 缓存必须 purge，脚本会自动调 API 并轮询校验

### 9. 文档

- `README.md` 重写：置顶早期测试提示，安装改为「把这段提示词粘给你的 Agent」，适配提示词 + 适配列表
- `docs/INSTALL-WITH-AGENT.md`：给 Agent 的完整安装指令
- `docs/RELEASE.md`：发布流程（含 jsDelivr 缓存、可复现性边界、清单对账）
- `docs/SETTINGS-UX-REVIEW.md`：15 条 UX 审查结论
- `.github/ISSUE_TEMPLATE/bug_report.yml` + `PULL_REQUEST_TEMPLATE.md`
- `CHANGELOG.md`

---

## 四、没完成的

### 1. 「核显模式」没有实现路径

**现状**：置灰，提示「本机的 NVIDIA 与固件接口都没有提供『仅核显』模式，此版本不支持」。

**为什么**：`NvidiaDdsService` 只能表达 Optimus / Automatic / dGPU 三种，没有「关掉 dGPU 只留 iGPU」的接口。Acer WMI 也没有。

**要做什么**：要么找 NVIDIA 的 `NvCplApi` 里有没有禁用 dGPU 的 setting，要么走设备管理器禁用/启用，要么承认这一档不做并从 UI 移除。

### 2. 「独显直连」在本机不可用（硬件限制）

PH317-55 没有 MUX：Acer WMI `GetGamingMiscSetting(0x02)` 返回 `0xFF00`（高字节 0xFF = 不支持），`nvidia-smi` 显示 dGPU 没接显示器。

**这不是缺陷**，是硬件没有。但在有 MUX 的机器上（例如 ASUS ROG 部分型号）要验证 `SetGpuMuxMode` 真的能切。

### 3. OEM 服务管理只对 Acer 开放

`OemBrandProbe.CanManageServices => Brand == OemBrand.Acer`——**只有识别成 Acer 的机器**，开关才可点。ASUS / Lenovo / MSI 等品牌的机器上，服务页整个是只读的，显示「本版本尚未实现 {0} 服务的停用：下面的列表只读」。

而且即使是 Acer，也只有**整组开关**，不能逐项停用。

**要做什么**：
- 逐项开关：给 `OemBrandCatalog` 加单项控制，并补 PSSvc 之外的保护名单
- 开放其它品牌：得先为每个品牌确认服务清单和依赖关系，现在只有 Acer 那份是实测过的
- 服务列表现在只有名字 + 用途，没有「停掉会怎样」的风险分级

### 4. 「使用电源时 → 恢复原有配置」

设计图上这一档没做。`Session.AcPowerMode` 只接受五个模式字节，没有能存「恢复原配置」的字段，加字段要动 Core schema。

**要做什么**：`PreySenseConfig.Session` 加一个 `AcPowerModeRestoreOriginal` 或让 `AcPowerMode` 接受 -1 表示"不干预"。

### 5. 电源配置的默认值已核对

`PreySenseConfig.Session`：`BatteryPowerMode = 6`（Eco，即设计图要求的「节能默认」✓），`AcPowerMode = 1`（Balanced）。

AC 那档设计图要的是「恢复原有配置（默认）」，现在是 Balanced——这就是第 4 条那个未做项。

### 6. 只有一台机器验证过

所有硬件路径（风扇曲线、EC 读写、灯效、热键、电源模式）都只在 PH317-55 上跑过。README 的适配列表也只有一行。

---

## 五、还需改善的

### 1. 验收脚本依赖「屏幕亮着」

`ia-verify.ps1` / `update-verify.ps1` 用 `PrintWindow` 抓图，**显示器熄灭时 DWM 停止合成，抓到全白**。这个坑已经骗过一次（一度被误判为 WinUI 合成故障）。

**已做**：脚本改为先写临时目录、断言全过才落 `docs/`，失败时 `docs/` 一字不动。

**还能改**：脚本启动前主动检测屏幕状态（`GetLastInputInfo` / 显示器电源），熄灭就明确报错而不是跑一遍再失败。

### 2. 多 agent 并发时的端口与窗口争用

这轮多个 agent 同时改同一个 checkout，出现过：抢 8099 端口、`Stop-Process -Name Performix` 互杀窗口、`KeyboardAccelerator` 与 `UpdateInfoWindow` 半成品互相编译失败。

**建议**：并行改同一仓库时，验收脚本要加文件锁或端口探测，或者干脆串行化验收环节。

### 3. 打包不可 bit-for-bit 复现

`build-app-zip.ps1` 做到了**容器层**确定（条目排序、时间戳固定），但内容层不可复现：SDK 补丁版本、NuGet 还原图、pdb 里烧进去的绝对路径、R2R/PGO 都会改字节。

**现状**：本地构建 33,490,261 字节 / `a8d5e3…`，CI 构建 33,489,786 字节 / `3d5048…`。同一份源码两个哈希。

**规则**：以 CI 构建的资产为准，本地构建只用于验证。`docs/RELEASE.md` §2.1 写清楚了。

### 4. `--check-update` 的同步 `Exit()` 未验证

`App.xaml.cs` 里 `--check-update` 在 `OnLaunched` 里同步调 `Exit()`，注释警告会 0xC0000374 崩溃。实测跑通了没崩，但没做压力验证。

### 5. 页面订阅统一

`FansPage` 那个真 bug 已修（`Vm ??=` 跳过 setter 导致重新进入后不再订阅，转速冻结），抽了 `PropertyChangedSubscription` 幂等订阅器。

`GeneralPage`（原 PowerPage 已并入）现在的写法是：setter 里订阅，`OnUnloaded` **只退订 `LanguageChanged`，不退订 `PropertyChanged`**。因为从来没退订，所以不会冻数据，但页面卸载后仍在收事件。

**要做什么**：把 `GeneralPage` / `PersonalizePage` / `ServicesPage` / `UpdatePage` / `AboutPage` 统一到 `PropertyChangedSubscription` 那套，加载时幂等订阅、卸载时退订。

### 6. `docs/` 里有两张过期截图

`ia-power.png`（480×450）和 `ia-meta.png`（480×735）是旧信息架构留下的，对应已经删掉的 `PowerPage` 和 `MiniMetaPage`。`ia-verify.ps1` 现在不再生成这两张，README 也没引用它们，但它们还躺在仓库里。

**要做什么**：删掉这两个文件，或者确认没人引用后一起清掉。

### 7. 更新页的「更新说明」是英文夹具

`update-verify.ps1:571` 的 notes 是本地测试服务器喂的英文文本：

```
Update page verification build.
- real state machine, determinate progress
- source shown from the manifest hit
- notes area scrolls past 240 DIP
```

不是真实清单内容。真实发布时 `CHANGELOG.md` 那节是中文。

**要做什么**：夹具换成中文多行文本，顺便验证 CJK 换行和 240 DIP 滚动。

### 8. 服务列表没有风险分级

`OemBrand.ServicePurpose` 给了 16 项的中文说明，但没有「停掉会怎样」的分级。用户看到 14 个服务全都能停，实际上有几个停了会破坏热键和灯效。

**要做什么**：给每个服务标一个风险等级（安全 / 影响热键 / 影响风扇 / 绝对不要停），危险项单独置灰。

### 9. `docs/UPDATE-DESIGN.md` 是调研稿

标题还是 Predex 时期写的，内容里有 `manifest-alpha.json`、「立即检查」按钮、`UpdateFloatWindow`、`%TEMP%\PredexBackup` 等与现实现不符的东西。已经加了「调研稿，以 RELEASE.md 与代码为准」的标注，但没有重写。

---

## 六、关键文件索引

| 关注点 | 文件 |
|---|---|
| 更新状态机 | `src/Performix.App/Services/Update/UpdateStateMachine.cs` |
| 更新会话 / 下载 / 校验 | `src/Performix.App/Services/Update/UpdateSession.cs`、`Services/UpdateClient.cs` |
| 自动更新调度 | `src/Performix.App/Services/Update/UpdateAutoRunner.cs`、`UpdateAutoSchedule.cs` |
| 更新信息窗 | `src/Performix.App/Views/UpdateInfoWindow.xaml(.cs)` |
| 替换脚本 | `scripts/invoke-update.ps1` |
| 安装器 | `src/Performix.Installer/` |
| 窗口行为 | `src/Performix.App/Shell/ShellWindow.xaml.cs`、`Services/WindowBehaviorSettings.cs` |
| GPU 能力判定 | `src/Performix.Core/Gpu/GpuModeCapabilities.cs`、`GpuDisplayRoute.cs` |
| OSD 定位 | `src/Performix.App/Views/ModeOsdPlacement.cs` |
| 设置页 | `src/Performix.App/Pages/SettingsPage.xaml` 等六个页面 |
| 品牌与服务 | `src/Performix.App/Services/OemBrand.cs`、`OemBrandProbe.cs` |
| 发布流程 | `docs/RELEASE.md`、`scripts/publish-release.ps1` |
| 验收脚本 | `src/Performix.App/docs/ia-verify.ps1`、`update-verify.ps1` |

---

## 七、上手第一步

```powershell
# SDK（这台机器上的）
$sdk = 'G:\DeepSeek DSH\DSH Works\Project\PredatorSense\tools\dotnet-sdk\dotnet.exe'

# 编译（必须带 -m:1 -nodeReuse:false，否则 MSBuild 节点复用会假失败）
& $sdk build 'Performix.slnx' -c Debug -m:1 -nodeReuse:false

# 测试
& $sdk test 'tests\Performix.Tests\Performix.Tests.csproj' -c Debug --no-restore -m:1 -nodeReuse:false

# 验收（屏幕必须亮着）
$exe = 'src\Performix.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Performix.exe'
pwsh -NoProfile -File 'src\Performix.App\docs\ia-verify.ps1' -Exe $exe
pwsh -NoProfile -File 'src\Performix.App\docs\update-verify.ps1' -Exe $exe
```

当前基线：编译 0 警告 0 错误，测试 1068 通过 / 31 跳过 / 0 失败。
