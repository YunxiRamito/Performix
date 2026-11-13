# R5 末轮报告（色环完整 + 主窗口焦点框）

日期：2026-11-13 06:2x–06:4x（Acer Predator PH317-55，150% 缩放 = 144 DPI）
依据：任务板 `task-9`（Claude 第五轮评审：首页 90% / 子页 85%）。
红线：`app\Fork\**`、`PreySense.Core`、`PreySense.Tests`、`tools\**` 未动；没点任何 Apply / 模式 tile /
GPU tile。色环交互只改 UI 颜色（VM 属性），不写硬件。风扇转速那条按任务单不动。

## 0. 结论

两条 P0 都已处理：**色环完整**（160×160 的 `ColorSpectrum` 图元，点/拖都能取到圆环边缘）、
**主窗口焦点框**（热键唤出、展开/折叠后焦点都退回页面根容器，tile 与 ⌃ 上的白框消失）。
`-t:Rebuild` **0 warning / 0 error**。

## 1. 色环被裁成四分之一圆

- **根因**：WinUI 的 `ColorPicker` 控件模板把内部的 `ColorSpectrum` 钉在 `MinWidth/MinHeight = 256`，
  外层给 160 时多出来的部分被裁 —— 所以看到的是四分之一圆。
- **改法**（施工单方案 A）：5 个颜色块全部换成图元
  `Microsoft.UI.Xaml.Controls.Primitives.ColorSpectrum`，`Width=Height=160`、`Shape="Ring"`，
  颜色仍走同一套 `ColorChanged` 处理与 hex 输入框。
  注：图元对应的属性名是 **`Shape`**（`ColorSpectrumShape` 是 `ColorPicker` 的属性，写错会让
  XamlCompiler 静默 exit 1，本轮踩到并已按 WinMD 元数据核对）。
- **证据**（`docs\r5-verify.ps1` 用真鼠标）：
  - UIA 读该元素 bounding rect = **240×240 物理 = 160×160 DIP**；
  - 在圆环边缘四个方位点选：右 `#F60F17` / 上 `#800FF6` / 左 `#0FF6F4` / 下 `#85F612`；
  - 沿圆环从右侧拖到顶部：颜色随指针变化（`distinct colours = 4`）；
  - 截图 `r5-lighting-ring.png`：整圆完整、无裁切。

## 2. 主窗口白焦点框

- **根因**：热键唤出被 WinUI 当作键盘输入，激活流程把键盘焦点放到第一个可聚焦控件（折叠态第一个模式
  tile、展开态 ⌃）。tile 上的白色焦点矩形和「均衡」的 accent 选中框同时出现，看起来像选了两个。
- **改法**：把子页那套搬到主窗口 ——
  - 页面根元素已有 `IsTabStop="True" UseSystemFocusVisuals="False"`（R4 加的，五个页面都有）；
  - 壳新增 `ResetShellFocus()`，在 **`Window_Activated`（非 Deactivated 时）**、**每次
    `ApplyDensity`（折叠/展开）末尾**、以及 **`ShowAndActivate`** 里把焦点交回当前页根容器；
  - 注意：`RootGrid` 是 Grid（Panel，不是 Control），**不能**加 `IsTabStop`（加了会让 XamlCompiler 静默
    exit 1），所以焦点落在 Page 根容器上。
- **证据**：`r5-home-collapsed.png`（热键 VK 0xCC 唤出后）、`r5-home-expanded.png`（真鼠标点 ⌃ 展开 +
  再按一次热键重唤）——模式 tile 与 ⌃ 上都没有白框，只有「均衡」的 accent 选中框。

### 「语言」下拉与自动刷新按钮的框：真问题还是脚本案出来的？

先用**纯鼠标**复现（`r5-mouse-language.png` / `r5-mouse-autorefresh.png`）：

| 控件 | 结论 |
|---|---|
| 首页「自动刷新」chip | **脚本案出来的**：纯鼠标点击后没有框（旧截图里的框是 `InvokePattern` 触发的键盘焦点）。本轮仍给 `ChipButtonStyle` 加了 `UseSystemFocusVisuals=False` 兜底 |
| 设置页「语言」下拉 | **真问题**：纯鼠标点一下就会出现 1–2 px 浅色边框（WinUI ComboBox 模板的 focused 边框，不是系统焦点矩形）。已做两层抑制：`UseSystemFocusVisuals=False` + `FocusVisualPrimary/SecondaryThickness=0`，并把主题里的 `ComboBoxBorderBrushFocused/FocusedPressed` 钉到 idle 的 `#26FFFFFF`；边框已变淡但**没有完全消失**（要彻底去掉得自己重写 ComboBox 模板，本轮时间不够，留作下一轮） |

## 3. 构建 / 截图 / 收尾

```
& <repo>\tools\dotnet-sdk\dotnet.exe build Predex.WinUI.csproj -p:Platform=x64 -nodeReuse:false -t:Rebuild
  已成功生成。 0 个警告 / 0 个错误
```

| 文件 | 内容 |
|---|---|
| `r5-lighting-ring.png` | 灯光页 160 DIP 整圆色环 + 区域选中态，颜色随拖拽改变 |
| `r5-home-collapsed.png` | 热键唤出的折叠态（320×116），tile 无白框 |
| `r5-home-expanded.png` | 展开态（320×300），⌃ 无白框 |
| `r5-mouse-language.png` / `r5-mouse-autorefresh.png` | 纯鼠标复现用图 |

脚本：`docs\r5-verify.ps1`（PrintWindow + 真鼠标 + 真 Predator 热键；不点任何 Apply）。

## 4. 没做 / 遗留

1. **ComboBox 聚焦边框没有 100% 去掉**（见上表）——彻底方案是自定义 ComboBox 模板。
2. **色环不再显示亮度滑块**（`ColorPicker` 自带的那条）：颜色仍可靠 hex 输入框精确设定，VM 语义未变。
3. 风扇转速串值按任务单不在本轮（已由另一 agent 在 WMI 量化层修好并加回归测试）。
