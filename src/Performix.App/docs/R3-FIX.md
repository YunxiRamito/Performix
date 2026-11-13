# R3 施工单落地报告（10 条 P0 + 模式改名/换图标 + 上轮欠账）

日期：2026-11-13 05:2x（本机 Acer Predator PH317-55，150% 缩放 = 144 DPI）
依据：`docs\R3-MAKEOVER.md`（Claude Opus 5.5 对 r2 五张图逐页写）+ 用户追加的模式改名要求。
范围：`app\experiments\Predex.WinUI` 的页面 + 样式 + 服务（壳的标题栏/窗口行为另有一份
`docs\R3-SHELL-CHROME.md`，本轮在其基础上只补 H1/H4/X1-footer/X5）。
红线：`app\Fork\**`、`PreySense.Core`、`PreySense.Tests`、`tools\**` 未动；没点任何 Apply / 模式 tile /
GPU tile（没有任何 EC / powercfg / NVAPI 写入）。`ShellWindow.xaml.cs` 里 `_services.*` 订阅区没动。

## 0. 结论

10 条 P0 全部落地；模式改名（节能/静音/均衡/性能/狂暴）+ 两个新图标（性能=仪表盘 EC4A、狂暴=闪电 E945）
落地并真机截图确认；上轮欠账（子页内边距 / 黄字第二个 accent / 温度单位 / 自动刷新开关态）一并清掉。
`-t:Rebuild` **0 warning / 0 error**。二级页窗口封顶沿用另一 agent 已验证的 520 DIP，并新增 **固定 44 DIP 操作栏**。

## 1. 10 条 P0 逐条

| # | 改法（一句） | 证据 |
|---|---|---|
| H1 | GPU 排加 14 DIP 区头行（左「GPU 模式」12 SemiBold Secondary，右当前值 11 Muted），选中态复用 `ModeTileButtonStyle`；展开高 **280 → 300**，折叠仍 116（规范 6.2 已同步） | `r3fix-home-expanded.png` 480×450 = 320×300；`shell.log` `targetHeight=300DIP` |
| H2 | 「自动刷新」改真切换按钮：开 = Accent 实底 + Primary 字；关 = 透明底 + 1 DIP `#26FFFFFF`（CardStroke 色值）描边 + Secondary 字，30 高/圆角 4/11 SemiBold | `r3fix-home-expanded.png` 左下 chip |
| S1 | 电源配置两个下拉改用 `Loc.T` 本地化的 `ProfileNames`（节能/静音/均衡/性能/狂暴），值不再显示 Eco/Balanced | `r3fix-settings.png`：使用电池 = 节能 |
| T1 | 中英混排清零：13 个标签/按钮走 UiText 既有键（Boost Mode / Fan Policy / Apply to Current Mode / Restore Windows Defaults / Open Curve Editor…）+ 新增 zh 行（最大处理器状态 / 能效偏好 / 核心驻留下限·上限 / 核心·显存频率偏移 / 恢复默认 (0/0 MHz)）；Boost Mode 覆盖为 **睿频模式**；Boost 下拉值经 `Loc.T`（已启用/已禁用…） | `r3fix-tuning.png` + `r3fix-tuning-scrolled.png` |
| T2 | 两个按钮等分、间距 8、高 30、字 **12**；中文译完后文字完整（「应用到当前模式」「恢复 Windows 默认」「恢复默认 (0/0 MHz)」） | `r3fix-tuning-scrolled.png` |
| T3 | 「电源来源」下拉改为跨值列 `Grid.ColumnSpan=2` + Stretch + `MinWidth 96`，箭头完整 | `r3fix-tuning.png` 电源来源行 |
| L1 | 区域 1–4 改为**选中态表达**：选中 = Accent 实底 + Primary 字，未选中 = 中性底 + Secondary 字；「同步所有区域」打开时四个一起选中；不再用区域颜色填底 | `r3fix-lighting.png` / `-scrolled.png` |
| L2 | 5 个 ColorPicker 全部 `IsColorPreviewVisible=False`（右侧预览竖条不再溢出卡片），色环 240–300 → **160×160** 居中 | `r3fix-lighting-scrolled.png` |
| X1 | 二级页封顶（壳 `SecondaryPageMaxHeightDip=520`，已由壳改造落地）+ **页面级操作栏固定 44 DIP**：Fans / Lighting 的 Apply/Reset 移出 ScrollViewer、命名 `PageFooter`，壳在量高度时把它的 `ActualHeight` 计入 | `r3fix-fans.png` / `r3fix-lighting.png` 底部按钮常驻；`shell.log` 每页 content/wanted/cap 行 |
| X2 | 卡片内边距四边 12 统一走 `CardStyle`（R2 已做，本轮逐页复核）：滑块拇指完整、卡片标题不再贴边 | 四张 `r3fix-*.png` |

## 2. 模式改名 + 换图标（用户新要求）

| 项 | 结果 |
|---|---|
| 名称 | 节能 / 静音 / 均衡 / 性能 / **狂暴**（原「极速」）；英文源串 `Eco / Quiet / Balanced / Performance / Beast` |
| 文案来源 | `Services\LocalizationService.cs` 新增 `TextOverrides`（**五语言**：zh-CN / en / hi-IN / es-ES / fr-FR），优先于 UiText；`Turbo` 保留为别名并同样映射到「狂暴」，因为 Core 的 `ProfileManager.SourceProfileNames` 仍会吐出 `Turbo` |
| 图标 | 「狂暴」= `\uE945`（闪电，渲染确认）；「性能」= `\uEC4A`（仪表盘/速度计，渲染确认）；节能 E8BE / 静音 E708 / 均衡 E9E9 不动 |
| 图标选取方法 | 把 Segoe Fluent Icons 的 `E800–EBFF` 全区间渲成对照表，再对候选 24 枚以 56 px 放大复核，`EC4A` 是唯一真正的仪表盘造型 |
| 硬件路径 | 五个 tile 全部仍走同一个 `SetModeCommand`（参数 6/0/1/4/5），**没有新开任何写路径**；「狂暴」本轮只改 UI 语义 |
| 出现位置 | 首页模式排、调校页模式排、设置页电源配置下拉、风扇页配置模式下拉（全部经同一张表） |

## 3. 上轮欠账

| 欠账 | 结果 |
|---|---|
| 子页内边距 | `CardStyle` padding = `GapGroup`(12)，四页复核，滑块拇指完整 |
| 黄字第二个 accent | 调校页「恢复…」按钮改 `TuningSecondaryButtonStyle`（Primary 字 + ControlFill 底 + CardStroke 描边），全页删除黄色；红色只留给灯效页的破坏性 Reset |
| 温度单位 | 标题栏 `CPU 71 °C  GPU 53 °C`：标签 11 Secondary、值 14 SemiBold Primary(Tabular)、单位 11 Muted，组内 4 / 组间 12；版本号因此从标题栏移到壳的展开态左下角（壳改造已做） |
| 自动刷新开关态 | 见 H2 |

## 4. 构建 / 真机

```
& <repo>\tools\dotnet-sdk\dotnet.exe build Predex.WinUI.csproj -p:Platform=x64 -nodeReuse:false -t:Rebuild
  已成功生成。 0 个警告 / 0 个错误
```

`docs\r3fix-verify.ps1`（PrintWindow + UIA；滚动后窗口 rect 不动 = 操作栏确实固定在窗外层）：

| 页 | 窗口（物理 px） | DIP | 右下角 |
|---|---|---|---|
| Home 折叠 | 480×174 | 320×116 | (2536,1344) |
| Home 展开 | 480×450 | 320×300 | (2536,1344) |
| Settings | 480×780 | 320×520 | (2536,1344) |
| Tuning | 480×780 | 320×520（content 883 → cap） | (2536,1344) |
| Fans | 480×780 | 320×520（content 725 → cap） | (2536,1344) |
| Lighting | 480×780 | 320×520（content 645 → cap） | (2536,1344) |

截图：`r3fix-home-collapsed.png`、`r3fix-home-expanded.png`、`r3fix-settings.png`、`r3fix-tuning.png`、
`r3fix-fans.png`、`r3fix-lighting.png`，外加每页 `-scrolled.png`（滚动后操作栏仍在、窗口不动）。

## 5. 没做 / 没改（P1/P2，附理由）

1. **S2（USB 关机充电改下拉）**：仍是 0/10/20/30 步进滑块 + 标签下「已禁用」。改下拉要重接
   `SetUsbChargingAsync` 的提交时机（现在是「松手才写」），属于写路径行为改动，本轮不动。
2. **S3 的开关右侧状态**已统一成开/关，「上限 80%」已放到标签下一行；多档位数值本身仍由滑块表达（同 S2）。
3. **T6（滑块改两行式）**：8 行全部重排会改动 Tuning 的整页行高与滚动位置，本轮只保证 T1 译完后文字不再截断。
4. **F1**：核对过不是绑定错误——`FansPage.UpdateLiveRpm()` 读 `TelemetryCoordinator.CurrentSnapshot` 的
   `CpuRpm` / `GpuRpm` 两个字段（与首页 `cpu=…/4109 gpu=…/N/A` 的 trace 同源），两台风扇同速是硬件读数。
5. **F2/F3/F4/F5/F6、L5/L8、X3（返回键焦点框）、X6（调校页圆圈「?」仍是第二套帮助图标）、X7（卡片间距）、
   X8（下拉字号/右对齐）**：P1/P2，未做。
6. **X1 的 640 与壳改造的 520**：施工单写「子页最大高 640」，另一 agent 的壳改造按「高度 ≤ 520 DIP」验收口径
   封顶 520 并已出图验证。我保留 520（不推翻已验收交付），只补了 44 DIP 固定操作栏——**封顶值需要 lead 定夺**。
7. **窗口不再可拖拽**：壳改造删掉了 `SetTitleBar` 与拖拽处理（其验收要求），不是本轮回归。
