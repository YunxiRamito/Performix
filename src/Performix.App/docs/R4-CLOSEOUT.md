# R4 收口报告（4 必改 + P1/P2 清单）

日期：2026-11-13 06:0x（Acer Predator PH317-55，150% 缩放 = 144 DPI）
依据：任务板 `task-8`（Claude 第四轮评审：首页 85% / 子页 65%）。
红线：`app\Fork\**`、`PreySense.Core`、`PreySense.Tests`、`tools\**` 未动；没点任何 Apply / 模式 tile /
GPU tile；除「自动刷新开态截图」那一次（OS 刷新率 = 自动，拍完已点回）之外没有任何写操作。

## 0. 结论

4 条必改全部修好并用真机证据确认；P1/P2 清单里点名的 7 条里做了 6 条（S2/T6/L5/X6 + 灯光底栏 +
设置页装下 + GPU 区头字号 + 版本号位置），`-t:Rebuild` **0 warning / 0 error**。

## 1. 四条必改

### #1 GPU 模式读不到
- **诊断**：不是绑定问题，是**读取失败**。`MainViewModel.Gpu.cs` 的 `ApplyDdsStatus()` 把
  `status`（本机无 MUX / DDS 不支持）落到 `ActiveGpuMuxMode = -1`，于是
  `GpuMuxModeName = "N/A"` 且 `IsGpu*(Active)` 全为 false → 四个 tile 都是未选中态。
  壳日志 `muxMode=-1 (N/A) showDdsTiles=True biosDiscrete=False` 可复现。
- **改法**：区头右侧改用 `local:Converters.Telemetry(ViewModel.GpuMuxModeName)` —— 规范第 5 节的
  缺失读数映射（`N/A` → `—`），并加 ToolTip「此硬件不支持独显直连（无 MUX / DDS）」。tile 的选中态绑定
  本来是对的，本机读不到 mux 就没有可亮的 tile（硬件限制，不是 UI 缺陷）。
- **证据**：`docs\r4-verify.ps1` 读出 `TxtGpuModeValue = '—'`。

### #2 卡片内边距 12 没生效
- **根因**：`CardStyle` 里写的是 `<Setter Property="Padding" Value="{StaticResource GapGroup}"/>`，
  `GapGroup` 是 `x:Double`，把 Double 赋给 `Border.Padding`(Thickness) 被运行时**静默转成 0** ——
  所以每张卡片都没有内边距，标签贴边、亮度滑块拇指光晕顶到卡片边。
- **改法**：`CardStyle` 的 Padding 改成**字面量 12**（一张样式管所有卡片 Border）。
- **验收实测**（截图像素探针，150% 缩放）：卡片内文字左缘 **26.0 DIP**（24 + 字形左侧留白）≈ 24；
  控件/滑块拇指右缘 **293–296 DIP**；亮度滑块拇指完整（左 26 / 右 293，两侧都在卡片内）。
  `shell.log` 里 Settings 内容高 478 / Lighting 759 / Tuning 883（内边距生效后重新量过）。

### #3 风扇转速跨页对不上（真 bug）
- **根因**：首页/调校页显示 `MainViewModel.CpuFanText/GpuFanText`（`ApplyMainWindowTelemetry` 里
  `snapshot.CpuRpm/GpuRpm` 一处映射）；**风扇页另起一份**：自己 1 Hz 定时读
  `TelemetryCoordinator.CurrentSnapshot` 再拼 `"CPU {cpu} · GPU {gpu}"` —— 同一份数据被映射了两次，
  且可能比 ViewModel 派发的值新一拍，于是两台风扇的 55 RPM 差值在不同页面看起来"换位"。
- **改法**：删掉定时器与快照读取，风扇页改成绑定同一个 `MainViewModel.CpuFanText/GpuFanText`
  （页面属性 `CpuFanText`/`GpuFanText`，随 VM 的 PropertyChanged 刷新）。现在**只有一处映射**。
- **证据**（`docs\r4-verify.ps1`，连跑 3 轮，每轮「首页→调校→风扇」各读一次）：
  `home 4054/4054  tuning 4054/4054  fans 4054/4054 → identical=True`（三轮全 True）。

### #4 白焦点框变多
- **根因**：热键唤出窗口被 WinUI 算作键盘输入，焦点落到第一个可聚焦控件（模式 tile / ⌃ / 返回键 / 应用）。
- **改法**：每个页面根元素加 `IsTabStop="True" UseSystemFocusVisuals="False"`（Page 是 Control，可聚焦）；
  壳在「页面切换后」与「窗口显示后」调用 `FocusPageRoot()` 把焦点给当前页根容器。Tab 仍能从根走进第一个控件。
- **证据**：`r4-tuning.png` 标题栏返回键无白框、`r4-fans.png` 底部「应用」无白框、`r4-home-*.png` tile/⌃ 无白框。

## 2. P1/P2 清单

| 项 | 结果 |
|---|---|
| 灯光页底栏统一成风扇页样式 | ✅ 两个 `*` 等分，Apply = Accent，Reset = 中性 `SecondaryButtonStyle`（红只留给真正的破坏性语义；确认弹窗仍在） |
| 设置页「使用电源」被切 | ✅ 行距 4→0、行高 40→36、卡片间距 12→4：内容 **478 DIP**，窗口 **518 DIP** < 520 封顶 → **整页放下，`ScrollPattern` 报「无滚动」** |
| GPU 区头字号 | ✅ 本来就是 `SectionTitleStyle` = 12 SemiBold Secondary（本轮未改，已核对样式链） |
| 版本号离窗沿 5 DIP | ✅ 移回标题栏：`PredeX  v3.0.8 │ CPU 69 °C GPU 52 °C`，删掉底部 `VersionFooter` |
| S2 USB 关机充电改下拉 | ✅ 4 档下拉（关闭 / 10% / 20% / 30%），选项即写（`SetUsbChargingAsync`），删掉标签下的「已禁用」 |
| T6 能效偏好折两行 | ✅ 标签缩为「能效偏好」，(0 性能 – 100 节能) 进 ToolTip，一行显示 |
| L5 分区键盘还是方块 | ✅ 单设备时改为普通文本行（名称 12 Primary + 下方 11 Muted 说明），≥2 台才显示 chip 列表 |
| X6 帮助图标两套 | ✅ 调校页的圆圈「?」全部换成设置页的 ⓘ（`\uE946`，12 Muted，紧跟标题/标签间距 4） |

## 3. 构建 / 截图

```
& <repo>\tools\dotnet-sdk\dotnet.exe build Predex.WinUI.csproj -p:Platform=x64 -nodeReuse:false -t:Rebuild
  已成功生成。 0 个警告 / 0 个错误
```

`docs\r4-verify.ps1` 产物（PrintWindow + UIA）：

| 文件 | 内容 |
|---|---|
| `r4-home-collapsed.png` | 折叠 320×116，模式 tile 无焦点框 |
| `r4-home-expanded.png` | 展开 320×300，GPU 区头 `—`，v3.0.8 回到标题栏 |
| `r4-home-autorefresh-on.png` | 自动刷新「开」态（accent 实底 + 白字） |
| `r4-tuning.png` / `r4-tuning-bottom.png` | 调校页顶 / 滚到底（T2 双按钮、GPU 块禁用态、Advanced） |
| `r4-fans.png` | 风扇页（实时 RPM 在提示下一行、原因提示紧跟单选行、底部等分操作栏） |
| `r4-lighting.png` / `r4-lighting-ring.png` | 灯光页顶 / 色环（160、无预览竖条、区域选中态、左对齐颜色行） |
| `r4-settings.png` / `r4-settings-bottom.png` | 设置页 518 DIP 一屏放下（含「使用电源」），两次截图一致 = 无滚动 |

## 4. 没做 / 说明

1. **GPU 四个 tile 仍然全灰**：本机 `ActiveGpuMuxMode = -1`（无 MUX/DDS），没有硬件可读的选中态。
   UI 侧绑定正确；换支持 Advanced Optimus 的机器才会亮。区头已按规范显示 `—`。
2. **设置页为装下 480 高度做了压缩**：行高 36、卡片间距 4、卡内 0 间距；水平内边距仍是 12（验收的
   24 / 296 成立）。如果后续要回到 40 行高，需要把二级页封顶从 520 放宽到 ~560。
3. **亮色主题未回归**：本轮改动全部走设计令牌，未在亮色主题下截图。
