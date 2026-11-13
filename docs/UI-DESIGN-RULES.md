# Predex 主窗口 UI 设计规则（可执行版）

> 适用：`app/experiments/Predex.WinUI/`（WinUI 3 / Windows App SDK，单窗口折叠式悬浮面板）。
> 来源：① Emil Kowalski `emilkowalski/skills`（MIT）提炼；② Dafeiyu-Go 参考实现实测数值。
> 本文只是规范。改代码前先读第 6 节改造清单。
> 单位统一 **DIP**（除 `AppWindow.MoveAndResize` 的物理像素，见 4.4）。

---

## 0. 一页速查

| 项 | 规定值 | 禁止 |
| --- | --- | --- |
| 窗口宽 | 320（固定，两态一致） | 折叠/展开改宽 |
| 折叠高 | 116 | — |
| 展开高 | 300（R3：GPU 模式区头行 +20） | — |
| 窗口外层圆角 | 由 DWM `DWMWCP_ROUND` 画（≈8） | XAML 里给整窗 16 圆角 Border |
| 窗口背景 | 透明（露出 Mica/材质）或单色不透明兜底 | 半透明深色蒙层压住材质 |
| 圆角刻度 | 控件 4 / 卡片 6 / 内容面 8 | 12 以上 |
| 间距刻度 | 4 / 6 / 8 / 12 / 16 | 20 以上 |
| 字号刻度 | 11 / 12 / 14 / 16（4 级封顶） | 9.5、10.5、20 混用 |
| 颜色层级 | 3 级文本 + 1 个 accent | 5 个 neon 色同时当正文色 |
| UI 动效时长 | ≤ 300ms（详见 1.2） | 400ms 以上的常规交互 |
| 入场缓动 | `ease-out` 强曲线 (0.23, 1, 0.32, 1) | `ease-in`（0.4, 0, 1, 1）入场 |
| 窗口尺寸过渡 | 220ms，(0.16, 1, 0.3, 1) | 无动画直接跳变 |
| 内容淡入 | 160ms，(0.23, 1, 0.32, 1) | `scale(0)` 进场 |

---

## 1. 动效规则（Emil → WinUI 3 落地）

### 1.1 先过「该不该动」的闸门

来源：`skills/emil-design-eng/SKILL.md` L69–76；`skills/animate/SKILL.md` L41–50。

| 触发频次 | 决定 | Predex 对应 |
| --- | --- | --- |
| 100+ 次/天（快捷键、命令面板） | **不动** | 全局热键唤出/收起 |
| 数十次/天（hover、列表导航、模式切换） | 极轻或不做 | 5 档模式切换的 hover / 选中态 |
| 偶尔（展开、弹层、设置） | 标准动画 | **折叠↔展开**、设置窗 |
| 罕见（首次引导、结果反馈） | 可以加戏 | 首次展开提示 |

必须能说出目的（反馈/空间一致/状态指示/避免跳变）。说不出就删。
**键盘触发的动作是取消资格项，不是判断题**：`F12/Predator 键` 唤出窗口用滑入动画可以（用户按的是"叫人"，不是"翻菜单"），但同一窗口内的模式切换不要做位移表演。

### 1.2 时长

来源：`emil-design-eng` L127–135、`review-animations/STANDARDS.md` L41–49。

| 场景 | 区间 | Predex 取定 |
| --- | --- | --- |
| 按钮按下反馈 | 100–160 | 120 |
| 小浮层 / tooltip | 125–200 | 160 |
| 下拉、选择器 | 150–250 | 180 |
| 模态、抽屉 | 200–500 | 240 |
| **窗口高度变化（折叠↔展开）** | 200–250 | **220** |
| 展开内容淡入 | 120–200 | **160** |
| 收起内容淡出 | 80–140 | **100** |

硬上限：常规 UI 动效 **≤ 300ms**。超过必须写理由。

### 1.3 缓动曲线（直接抄控制点）

来源：`emil-design-eng` L112–118。

| 语义 | CSS 控制点 | WinUI 3 Composition | WinUI 3 XAML 近似 |
| --- | --- | --- | --- |
| 强 `ease-out`（入场/反馈，默认） | `(0.23, 1, 0.32, 1)` | `CreateCubicBezierEasingFunction(new(0.23f,1f), new(0.32f,1f))` | `ExponentialEase { EasingMode=EaseOut, Exponent=6 }` |
| 强 `ease-in-out`（屏内位移/尺寸变化） | `(0.77, 0, 0.175, 1)` | `(0.77f,0f) → (0.175f,1f)` | `ExponentialEase { EasingMode=EaseInOut }` |
| 抽屉曲线 | `(0.32, 0.72, 0, 1)` | `(0.32f,0.72f) → (0f,1f)` | 无 |
| 系统弹窗曲线（Dafeiyu 实测采用） | `(0.16, 1, 0.3, 1)` | `(0.16f,1f) → (0.3f,1f)` | 无 |
| 线性（进度条、持续运动） | — | `CreateLinearEasingFunction()` | `LinearEasingFunction` |

**关键事实**：`Microsoft.UI.Xaml.Media.Animation` 里**没有**任意三次贝塞尔的类（只有 Power/Quadratic/Cubic/Exponential/Back/Bounce/Elastic + EasingMode）。
→ 需要**精确曲线**时用 Composition（`Compositor.CreateCubicBezierEasingFunction`），或用 `DispatcherQueueTimer` 逐帧自己算贝塞尔（参考实现就这么干，见 2.2）。XAML Storyboard 只用于"曲线不敏感"的场合。

禁用：`ease-in` 做入场。它在用户最专注的头 60ms 里几乎不动，同样的 200ms 会「感觉」比 ease-out 慢一倍。
禁止凭空发明曲线（`animate/SKILL.md` L32）——只用上表。

### 1.4 什么该动 / 不该动

来源：`animate/SKILL.md` L83–96；`review-animations/STANDARDS.md` L110–117。

| 动 | 不动 |
| --- | --- |
| `Opacity` | `Padding` / `Margin`（触发布局） |
| `Offset` / `Transform`（位移、缩放 0.98→1） | `Width` / `Height` 的 XAML 动画（需 `EnableDependentAnimation`，掉帧） |
| 窗口尺寸（逐帧 `MoveAndResize`，唯一例外） | 任何"每 3 秒循环"的呼吸动效 |
| 选中态的颜色/底色 | 正文文本的颜色动画 |
| 内容区进出（fade + 4 DIP 位移） | 图标旋转（除非表示加载） |

唯一允许直接改尺寸的场景是**手风琴式展开**——因为位移没有等价 transform 表达（`animate/SKILL.md` L85）。Predex 的折叠/展开正属此类，但必须逐帧改窗口、不要试图用 Storyboard 动 `Height`。

### 1.5 入场/出场与窗口尺寸变化的推荐做法

| 规则 | 落地 |
| --- | --- |
| 永不用 `scale(0)` 进场 | 内容进场 `Opacity 0→1` + `Scale 0.98→1`（`emil-design-eng` L215–232） |
| 进出同路径、可被打断 | 展开从下往上顶开，收起时就往下收；中途反向不得回零重播 |
| 尺寸变化时锚点固定 | 右下角屏幕坐标钉死，只动左上边缘（参考实现做法，见 2.2） |
| 内容比窗口晚一点出现 | 窗口开始 60ms 后再淡入内容，避免"内容被裁切着长大" |
| 收起时内容先走 | 内容 100ms 先淡出，窗口 200ms 随后缩（非对称：决定慢、响应快，`animate/SKILL.md` L148） |
| 列表用系统过渡 | 用 `RepositionThemeTransition` / `AddDeleteThemeTransition`，别手搓逐项动画 |
| 交错 ≤ 80ms | 展开时新出现的 3 组，延迟 0 / 40 / 80 |

### 1.6 降级动效（`prefers-reduced-motion` 的 WinUI 等价物）

来源：`emil-design-eng` L531–540；`apple-design/SKILL.md` L209–224。

```csharp
// 每次动效启动前读一次，不缓存（用户可能中途在设置里改）
bool motion = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
```

| 开关 | 行为 |
| --- | --- |
| `AnimationsEnabled == true` | 全量：窗口 220ms + 内容 160ms 位移淡入 |
| `AnimationsEnabled == false` | 窗口尺寸**直接** `MoveAndResize`（0ms）；内容只做 120ms `Opacity` 交叉淡入；**删掉位移与缩放** |

注意是「更少更轻」，不是「全关」——纯透明度过渡保留。
附带：`UISettings.AdvancedEffectsEnabled == false` 时不做 Mica 之上的任何模糊叠加层。

### 1.7 性能红线

| 规则 | 理由 |
| --- | --- |
| 逐帧用 `DispatcherQueueTimer`，间隔 16ms | 对齐 60Hz |
| 进度按**帧数**推进，不按 `Stopwatch` 墙钟 | UI 线程被插件加载卡住时，墙钟会算出"已到点"导致瞬移（参考实现注释同款结论，见 2.2） |
| 动画期间不重建可视树 | 展开区元素常驻，只切 `Visibility` / `Opacity` |
| 逐帧 `MoveAndResize` 要 try/catch 吞掉 | 窗口销毁竞态会抛 |

---

## 2. 参考实现拆解：Dafeiyu-Go

### 2.1 数值总表

| 项 | 值 | 出处 |
| --- | --- | --- |
| 信息窗宽 | 380 DIP（通知态 480） | `source/InfoWindow.cs:42-43` |
| 信息窗高 | 150（进度态）/ 104（紧凑态）/ 上限 480 | `InfoWindow.cs:46-49`、`:684` |
| 描述每多一行 | +16 | `InfoWindow.cs:52` |
| 描述最多行 | 4（进度态 2） | `InfoWindow.cs:55`、`:934` |
| 屏幕边距 | 16 | `InfoWindow.cs:57` |
| 内容内边距 | 18,12,18,12（常规）/ 18,20,18,20（通知） | `InfoWindow.cs:170`、`:634` |
| 图标格 | 圈 36 放 40 格 | `InfoWindow.cs:69-70` |
| 圆角：内容面 | 8 | `CornerRadiusHelper.cs:73-76` |
| 圆角：卡片 | 6 | `CornerRadiusHelper.cs:68-71` |
| 圆角：控件 | 4 | `CornerRadiusHelper.cs:63-66` |
| 圆角：搜索框 | 17（34 高的一半，胶囊） | `CornerRadiusHelper.cs:83-86` |
| 设置窗默认 | 1200 × 720 | `SettingsWindow.xaml.cs:36-37` |
| 设置窗最小 | 800 × 560 | `SettingsWindow.xaml.cs:38-39` |
| 设置窗工作区余量 | 48 | `SettingsWindow.xaml.cs:40` |
| 设置卡片 | Padding 16,12 / 圆角 `OverlayCornerRadius` / 描边 1 | `SettingsWindow.xaml:60-66` |
| 设置行高 | 64，列距 24 | `SettingsWindow.xaml:68-71` |
| 设置页标题 | 28 SemiBold（**大窗专用，不要搬进小窗**） | `SettingsWindow.xaml:48-52` |
| 标题栏 | `ExtendsContentIntoTitleBar = true` + `SetTitleBar(AppTitleBar)`，透明背景，Logo 20×20 + 14 SemiBold 标题 + 左侧 16 内边距 | `SettingsWindow.xaml.cs:147-148`、`SettingsWindow.xaml:708-732` |

### 2.2 窗口尺寸是怎么变的（重点）

**没有 `SizeToContent`，没有 Storyboard 动 `Height`。** 做法是：自己算曲线，逐帧调 `AppWindow.MoveAndResize`。

| 环节 | 实现 | 出处 |
| --- | --- | --- |
| 改尺寸入口 | `Resize(w, h, animate)` → 算目标矩形 → `StartBoundsAnimation` | `InfoWindow.cs:829-898` |
| 帧循环 | `DispatcherQueueTimer`，间隔 16ms | `InfoWindow.cs:502-511` |
| 进度 | `progress = (ticks * 16) / duration`，**按帧数**不用墙钟 | `InfoWindow.cs:513-522` |
| 缓动 | 自己二分求解 `cubic-bezier(0.16, 1, 0.3, 1)` | `InfoWindow.cs:588-620` |
| 插值对象 | 插 **右边缘/下边缘 + 宽 + 高**，再反推 x/y | `InfoWindow.cs:525-534` |
| 应用 | `AppWindow.MoveAndResize(RectInt32)` | `InfoWindow.cs:537` |
| 时长 | 尺寸变化 **220ms**；整窗滑入滑出 260ms | `InfoWindow.cs:64`、`:61` |
| 首帧未渲染时 | 直接 `ApplyBounds`，不播动画，避免"边滑入边改尺寸"的叠加 | `InfoWindow.cs:854-860` |
| 滑入播放中收到改尺寸 | 只改动画目标，不打断曲线 | `InfoWindow.cs:862-875` |
| 尺寸 = 当前 | 直接 return（幂等） | `InfoWindow.cs:877-880` |

**可复用结论**：右下角锚定 = 「插右边缘和下边缘，别插左上角」。这正是 Predex 需要的（窗口就贴工作区右下）。
**为什么不照抄 `Storyboard`**：动 `Width/Height` 要 `EnableDependentAnimation=true`，每帧触发布局+绘制，220ms 的掉帧肉眼可见；`MoveAndResize` 每帧也只做一次，但内容和窗口同步长大，观感是"窗口在长"而不是"内容在挤"。

### 2.3 材质与「没有黑框」的做法

| 手法 | 细节 | 出处 |
| --- | --- | --- |
| 材质控制器 | 手写 `MicaController` / `DesktopAcrylicController`（`Mica`/`MicaAlt`/`AcrylicThin`/`AcrylicBase` 四种），不用 `Window.SystemBackdrop` 简写 | `LauncherAppearance.cs:17-20`、`:220-274` |
| 主题跟随 | `SystemBackdropConfiguration.Theme` 由应用主题解析 | `LauncherAppearance.cs:214-218` |
| 不支持材质 | 回退到不透明色：暗 `#202020` / 亮 `#F3F3F3`，或系统 `Background` 色 | `LauncherAppearance.cs:298-314` |
| 根层兜底 | 最外 `Grid` 背景**等于**卡片底色，并给自己描 1px **同色**边 | `InfoWindow.cs:293-300` |
| 为什么要同色边 | 无边框窗口客户区最外 1–2 物理像素是 DWM 画的边框，默认白色 → 只能拿同色边盖掉 | `InfoWindow.cs:1299-1303` |
| DWM 染色 | `DWMWA_BORDER_COLOR` + `DWMWA_CAPTION_COLOR` 一起设成内容底色 | `InfoWindow.cs:1316-1326` |
| 圆角 | `DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND`；Win10（build < 22000）设 1（不圆角），且控件圆角全部归 0 | `InfoWindow.cs:1309-1314`、`CornerRadiusHelper.cs:41-51` |
| 窗口壳 | `OverlappedPresenter.SetBorderAndTitleBar(false, false)` + `IsResizable=false` + `IsAlwaysOnTop=true` | `InfoWindow.cs:155-162` |
| 内容铺满 | `ExtendsContentIntoTitleBar = true`，内容层自己画 | `InfoWindow.cs:308` |
| 阴影 | **不画**。靠 DWM 系统阴影 + 材质分层，不加自绘 DropShadow | 全文件无 Shadow 相关实现 |

**一句话**：黑框的来源通常是「XAML 画了一圈大圆角深色 Border，而窗口本身又是方角 + DWM 还有白边」。解法是三层同色对齐：内容面圆角 = DWM 圆角 ≈ 8，最外层再补一层同色 1px 盖住 DWM 边。

### 2.4 信息窗的贴边 / 圆角 / 无边框

| 项 | 做法 | 出处 |
| --- | --- | --- |
| 贴边 | 手工算工作区右下角，`Margin = 16` | `InfoWindow.cs:57`、`:348-360` |
| 无边框 | `SetBorderAndTitleBar(false,false)`，不可缩放 | `InfoWindow.cs:155-159` |
| 不进任务栏 | `WS_EX_TOOLWINDOW` 一类扩展样式 | 与 `MainWindow.xaml.cs:116-119` 同款思路 |
| 尺寸随内容 | `Measure` 内容 → `DesiredSize + 2` → `Resize(...,animate:true)` | `InfoWindow.cs:936-938` |
| 右下角不动 | 只插右/下边缘 | `InfoWindow.cs:475-499` |

---

## 3. 布局节奏与视觉层级

来源：`emil-design-eng` L26–36（细节复合）、`apple-design` L226–234（字体）、`ui-cheatsheet.md` L1–15。

### 3.1 间距刻度（只用这四个数）

| Token | 值 | 用在哪 |
| --- | --- | --- |
| `GapInline` | 4 | 图标与文字之间 |
| `GapTight` | 6 | 同一排 tile 之间 |
| `GapGroup` | 12 | 分组内容与分隔线之间（左右内边距） |
| `GapBlock` | 16 | 区块之间 / 窗口最大内边距 |

规则：**任何 Margin 只能由 4/6/8/12/16 组成**。出现 `14,0,0,0`、`12,8,12,10` 这种非刻度值即为缺陷。

### 3.2 分组：卡片 or 留白 + 分隔线

| 场景 | 用什么 | 理由 |
| --- | --- | --- |
| 同质选项（5 档模式） | **tile 底色 + 间距**，无外框 | 选项本身就是"块"，再套卡片就是卡片套卡片 |
| 异质区块（性能 / GPU / 显示 / 入口） | **1px 分隔线**（或纯 12 DIP 留白） | 分区靠间距，不靠再画一层框 |
| 需要浮起（下拉、浮层、复制按钮） | 卡片：圆角 6 + `CardBackgroundFillColorDefaultBrush` + 1px `CardStrokeColorDefaultBrush` | 只有"浮在别的层上"才配框 |

**同一个视图里，卡片描边和分隔线最多只留一种。** 现有壳两条都用了（见 6.1 F1）。

### 3.3 视觉层级上限

| 维度 | 上限 | Predex 取定 |
| --- | --- | --- |
| 字号 | **4 级** | 11 说明 / 12 正文 / 14 区块标题 / 16 页面标题 |
| 字重 | **2 级** | Regular、SemiBold |
| 文本颜色 | **3 级** | Primary `#F2F2F2`、Secondary `#A8A8BC`、Muted `#7C7C90` |
| 强调色 | **1 个** | 只标「当前选中」和「可点」，不做装饰 |
| 圆角 | **3 级** | 4 / 6 / 8 |

降权手段优先级：**改颜色 > 改字重 > 改字号**。不要靠"缩小到 9.5"来降权——9.5 DIP 在 125% 缩放下发虚。

等级映射：

| 角色 | 字号 | 字重 | 颜色 |
| --- | --- | --- | --- |
| 窗口标题 `PREDEX` | 12 | SemiBold | Primary（+ `CharacterSpacing=120`） |
| 区块标题 `性能模式` | 12 | SemiBold | Secondary |
| 主数据 `62°` | 14 | SemiBold | Primary，`Typography.NumeralAlignment="Tabular"` |
| tile 标签 | 11 | SemiBold | Primary（选中）/ Secondary（未选中） |
| 单位/次级 `°C` | 11 | Regular | Muted |
| 版本号 | 11 | Regular | Muted |

对比度：Muted `#7C7C90` 在 `#0A0A12` 上约 4.6:1，是下限；现有 `#5A5A70` 约 2.3:1，不达标（见 6.1 F6）。

---

## 4. 「不好看」反模式清单（对着现有壳挑毛病用）

来源：`emil-design-eng` L660–676；`review-animations/STANDARDS.md` L174–193；`break-ui/SKILL.md` L96–115。

### 4.1 结构类反模式

| # | 反模式 | 症状 | 正确做法 |
| --- | --- | --- | --- |
| P1 | **整窗外套大圆角 Border** | 圆角 16 而 DWM 圆角 ≈8，四角露出方角黑三角；Border 描边 1px 再加 DWM 白边 = 双线 | 删掉整窗 Border；圆角交给 DWM；最外层最多补 1px 同色边 |
| P2 | **半透明深色蒙层压材质** | `#B2060810` 盖住 Mica，看起来就是一坨黑 → "黑框套黑框" | 背景设透明（露材质）或干脆不透明纯色，二选一，别半吊子 |
| P3 | **卡片套卡片** | 外层 Border 里每个 tile 又各是一层 Border + 底色 | 只在与外框不同层级时才给第二层框 |
| P4 | **1px 描边遍地** | 外框有边、分隔线有边、tile 也有边，三条线抢注意力 | 一屏只留一种边框语言 |
| P5 | **全展开没有折叠态** | 一上来就 8 个 Row 全显示，窗口必然又高又空 | 折叠态只留一排，其余按需展开 |
| P6 | **留白分布不均** | 上 8 下 10、左边 12 右边 14、某一行单独 14 | 全量对齐 4/6/8/12/16 刻度 |

### 4.2 排版类反模式

| # | 反模式 | 症状 | 正确做法 |
| --- | --- | --- | --- |
| P7 | **标题过大** | 小窗里塞 20 甚至 28 号字 | 小窗页标题 ≤ 16 |
| P8 | **字号差不多但层级不清** | 9.5 / 10 / 10.5 / 11 / 12 全在一屏 | 只留 4 级，差 ≥2 |
| P9 | **次要信息靠缩小降权** | 9.5 DIP 发虚 | 降颜色 + 保持 11 |
| P10 | **强调色泛滥** | 5 个 neon 色同时当正文/图标 | accent 只标选中 |
| P11 | **数字跳动** | 温度刷新时列宽抖 | `Typography.NumeralAlignment="Tabular"` |
| P12 | **全大写 + 大字距滥用** | `PERFORMANCE MODE` + `CharacterSpacing=80` 到处都是 | 只给窗口标题用，区块标题用正常间距 |

### 4.3 动效类反模式

| # | 反模式 | 症状 | 正确做法 |
| --- | --- | --- | --- |
| P13 | 入场用 `ease-in` | 头 60ms 不动，感觉卡 | `ease-out` 强曲线 |
| P14 | `scale(0)` 进场 | 像从虚空蹦出来 | `scale(0.98)` + `opacity 0` |
| P15 | 出场比入场还慢 | 关窗拖沓 | 出场 = 入场的 0.6–0.8 倍 |
| P16 | 缓动是"默认值" | `(0.4,0,0.2,1)` 这种随便写的 | 只用 1.3 表里的四个 |
| P17 | 窗口尺寸靠 `Height` 动画 | 掉帧、内容挤压 | 逐帧 `MoveAndResize` |
| P18 | 忽略 reduced motion | 系统关动效了还在弹 | 见 1.6 |

### 4.4 窗口/DPI 类反模式

| # | 反模式 | 正确做法 |
| --- | --- | --- |
| P19 | 硬编码物理像素 | 常量写 DIP，`ToPhysicalPixels(dip, scale)` 转换，`scale = GetDpiScale()`（`SettingsWindow.xaml.cs:5902-5908` 写法） |
| P20 | 不夹工作区 | `Math.Clamp(desired, 1, workArea.Width - margin)`（同上 `:5910-5917`） |
| P21 | 位置写死 420/490 | 尺寸作为唯一真源，位置由尺寸反推（见 6.1 F2） |
| P22 | 展开后跑到屏幕外 | 锚右下角、向上长；触底时改锚点重算 |

---

## 5. 极值 / 边界

来源：`break-ui/SKILL.md` L20–28（用真人会产生的数据，不用 `aaaa`）、L117–125（truncate/wrap/clamp）。

| 场景 | 规定 | WinUI 属性 |
| --- | --- | --- |
| 长设备名（`PREDATOR PHN16-71-...`） | 单行截尾 + ToolTip 给全量 | `TextTrimming="CharacterEllipsis"` `MaxLines="1"` `ToolTipService.ToolTip` |
| 温度/风扇 `N/A` | 显示 `—`，**不留空行占位** | 绑定层转 `—` |
| 0 数据（未读到 GPU） | 一行 11 号 Muted 文案，不弹窗、不占 68 高 | — |
| 数值溢出（`--` / `999`） | 保留 3 字符宽 + Tabular，不换行 | `NumeralAlignment="Tabular"` |
| 刷新率列表为空 | ComboBox `IsEnabled=false` + 显示 `—` | — |
| 超窄窗口（< 320） | 320 为最小宽，低于此不做响应式，靠最小尺寸焊死 | `PreferredMinimumWidth` |
| 窄到放不下 5 tile | 5→5 等分压缩到 52 宽为下限，不再缩 | tile `MinWidth=52` |
| 行高不足（缩放到 200%） | 不用固定 `Height`，容器用 `MinHeight` | — |
| 长本地化文案（德语） | tile 标签允许两行，禁用 `TextTrimming` on tile | `TextWrapping="Wrap"` `MaxLines="2"` |
| 高 DPI（175%） | 所有 1px 描边用 DIP，DWM 边靠同色盖 | — |
| 亮色主题 | 不许只做暗色：全部颜色走 `ThemeResource` 或成对定义 | 现有壳全写死暗色（见 6.1 F7） |

---

## 6. 主窗口布局规范

### 6.1 折叠态（默认唤醒形态）

```
┌──────────────────────── 320 DIP ────────────────────────┐
│ ▲ PREDEX  1.4.2      CPU 62° GPU 55°      ⚙   ⌄   ✕    │ 40  标题栏 / 拖动区
├─────────────────────────────────────────────────────────┤  1   分隔线（12 内缩）
│                                                         │ 10
│  ┌──────┐┌──────┐┌──────┐┌──────┐┌──────┐               │
│  │  ⬡   ││  ☽   ││  ◈   ││  ⚡  ││  🔥  │               │ 52  模式 tile ×5
│  │ ECO  ││QUIET ││ BAL  ││ PERF ││TURBO │               │
│  └──────┘└──────┘└──────┘└──────┘└──────┘               │
│                                                         │ 12
└─────────────────────────────────────────────────────────┘
        总高 116 DIP
```

| 区域 | 尺寸 | 内边距 / 间距 | 字号 | 圆角 |
| --- | --- | --- | --- | --- |
| 窗口 | 320 × 116 | — | — | DWM ≈8 |
| 标题栏 | 320 × 40 | 左 12 / 右 6；元素间 8 | 12 SemiBold | — |
| 版本号 | Auto | 与标题 6 | 11 Regular Muted | — |
| 温度组 | Auto | 组内 4，组间 12 | 11（值 14 SemiBold） | — |
| 标题栏按钮 | 28 × 28 | 间距 4 | 图标 12 | 4 |
| 展开按钮 `⌄` | 28 × 28 | 距关闭按钮 4 | 图标 12 | 4 |
| 分隔线 | 1 高，左右内缩 12 | 上 0 / 下 10 | — | — |
| 模式 tile | 54 × 52（5 等分，gap 6） | 内容内边距 4,6 | 图标 16 / 标签 11 | 6 |
| 底内边距 | 12 | — | — | — |

布局算术：`40 + 1 + 10 + 52 + 12 = 115`，取 **116**。

### 6.2 展开态（R3：280 → 300，新增 GPU 模式区头行 14 + 间距 6）

```
┌──────────────────────── 320 DIP ────────────────────────┐
│ ▲ PREDEX  1.4.2      CPU 62° GPU 55°      ⚙   ⌃   ✕    │ 40   标题栏（⌄ 转 ⌃）
├─────────────────────────────────────────────────────────┤  1
│ 性能模式                                   风扇 2400     │ 14   区头行
│  ┌──────┐┌──────┐┌──────┐┌──────┐┌──────┐  ┌──────┐    │      ← 折叠态最后一行常驻
│  │  ⬡   ││  ☽   ││  ◈   ││  ⚡  ││  🔥  │  │      │    │ 52
│  └──────┘└──────┘└──────┘└──────┘└──────┘  └──────┘    │
├─────────────────────────────────────────────────────────┤  1   以下为展开区
│ GPU 模式                                   自动          │ 14
│  ┌────────┐┌────────┐┌────────┐┌────────┐              │
│  │  AUTO  ││ HYBRID ││ ADV.OP ││  dGPU  │              │ 48
│  └────────┘└────────┘└────────┘└────────┘              │
├─────────────────────────────────────────────────────────┤  1
│ 刷新率   [ 144 Hz  ▾ ]                        [AUTO]    │ 30
├─────────────────────────────────────────────────────────┤  1
│ [ 灯光 ]  [ 浮窗 ]  [ 风扇 ]  [ 调校 ]                   │ 28
└─────────────────────────────────────────────────────────┘
        总高 300 DIP（R3 前为 280，GPU 模式区头行 +20）
```

| 新增区域 | 尺寸 | 内边距 / 间距 | 字号 | 圆角 |
| --- | --- | --- | --- | --- |
| 区头行 | 320 × 14 | 左右 12；左右两段之间 `*` 撑开 | 12 SemiBold | — |
| GPU tile | 69 × 48（4 等分，gap 6） | 内边距 4,6 | 图标 16 / 标签 11 | 6 |
| 刷新率行 | 320 × 30 | 左右 12；标签与控件 12；chip 与 ComboBox 6 | 11 | 控件 4 / ComboBox 4 |
| ComboBox | 96 × 30 | — | 11 | 4 |
| AUTO chip | 44 × 30 | 内边距 10,0 | 11 SemiBold | 4 |
| 入口按钮 | 69 × 28（4 等分） | 图标与文字 4 | 11 | 4 |

布局算术（R3）：`40+1 +(14+52+10)+(68) +(1) +(14+44+6) +(1) +(14+48+6) +(1) +(10+30+10) +(1) +(8+28+8) = 298`，取 **300**（GPU 区头行 14 + 间距 6）。

给实现者：**折叠态就是展开态的前 116 DIP**，展开只是让下面 164 DIP 从无到有。不要维护两套布局——同一棵可视树 + `VisualStateManager`（`Collapsed` / `Expanded`），只切展开区的 `Visibility` 与窗口高度。

### 6.3 两条关键动效参数

| 通道 | 属性 | 时长 | 延迟 | 缓动 | 备注 |
| --- | --- | --- | --- | --- | --- |
| **窗口高度变化** | `AppWindow.MoveAndResize`，逐帧 16ms，锚右下 | **220ms** | 0 | `(0.16, 1, 0.3, 1)` | 展开往上长；高度取整；`scale` 换算 |
| **内容区淡入** | `Visual.Opacity` 0→1 + `Offset.Y` −4→0 | **160ms** | **60ms** | `(0.23, 1, 0.32, 1)` | 3 个新组各 +0/40/80 交错 |
| 内容区淡出 | `Visual.Opacity` 1→0 | **100ms** | 0 | `(0.23, 1, 0.32, 1)` | 收起时先走 |
| 收起窗口 | `MoveAndResize` | **200ms** | 60ms | `(0.16, 1, 0.3, 1)` | 比展开短，响应快 |
| 展开按钮图标 | `⌄`↔`⌃` 旋转 180° | 180ms | 0 | `(0.23, 1, 0.32, 1)` | 用 `RotateTransform` |

总感知时长：展开 220ms（内容在 280ms 内收尾）；收起 260ms。都在 300ms 预算内。

### 6.4 现有 WinUI 壳需要改哪些地方

对照 `app/experiments/Predex.WinUI/MainWindow.xaml`（338 行）与 `Styles/Resources.xaml`（322 行）。

| # | 位置 | 现状 | 改成 | 反模式 |
| --- | --- | --- | --- | --- |
| F1 | `MainWindow.xaml:18-20` | 整窗 `Border` `CornerRadius=16` + `BorderThickness=1` + 深色背景 | 删掉该 Border 的圆角与描边；最外层只留背景（透明或纯色） | P1 P3 |
| F2 | `MainWindow.xaml:19` + `Resources.xaml:9` | `WindowBg = #B2060810` 半透明压在 `MicaBackdrop BaseAlt`（`:13-15`）上 | 二选一：`Transparent` 露 Mica，或不透明 `#0A0A12`。禁止半吊子蒙层 | P2 |
| F3 | `MainWindow.xaml:23-32` | 8 个 `RowDefinition` 全量常驻，无折叠态 | 改为 折叠(116) / 展开(280) 两态，`VisualStateManager` 切换 | P5 |
| F4 | `MainWindow.xaml:24` | 标题栏 `Height="46"` | `40` | 刻度 |
| F5 | `MainWindow.xaml:44,56,68` | `Margin="14,0,0,0"` / `"0,0,6,0"` / `"0,0,6,0"`、`Spacing="8"/"6"/"2"` | 统一 `12` 左内距、组内 `4`、组间 `12` | P6 |
| F6 | `Resources.xaml:16` | `TextMuted = #5A5A70`（对比度 ≈2.3） | `#7C7C90`；`TextSecondary` `#9898B0`→`#A8A8BC` | 可读性 |
| F7 | `Resources.xaml:9-31` | 全写死暗色 hex，无 `ThemeResource` 回退 | 至少成对定义亮/暗，或全走 `ThemeResource` | P-亮色 |
| F8 | `MainWindow.xaml:83,174` vs `:248` | 模式区 `Margin="12,8,12,10"`，显示区 `Margin="14,8,14,10"` | 全部 `12` | P6 |
| F9 | `MainWindow.xaml:171,245,274` | 3 条 1px 描边分隔线，配合 F1 的外框 = 4 条线 | 保留分隔线，去掉 F1 外框描边；或反之 | P4 |
| F10 | `MainWindow.xaml:105,120,132,144,156` | 每个 tile 都是 `Border`(圆角 8 + 底色) 里套 `Button` | 去掉外层 `Border`，`Button` 直接用 `ModeTileButtonStyle`（`Resources.xaml:203-243`）的底色 | P3 |
| F11 | `MainWindow.xaml:109,121,133,145,157` | `Button Height="68"` | `52` | 紧凑 |
| F12 | `MainWindow.xaml:195,207,219,231` | GPU tile `Height="56"` | `48` | 紧凑 |
| F13 | `MainWindow.xaml:113-114` 等 | tile 标签 `FontSize="9.5"`，与 `Resources.xaml:65` 的 `FontSizeTile=9.5` 硬编码重复 | 标签统一 `11`，并用资源而非字面量 | P8 P9 |
| F14 | `MainWindow.xaml:113` 等 | 图标写死 `FontSize="16"` / `"14"`（`:161`）不统一 | 统一 16 | 一致性 |
| F15 | `MainWindow.xaml:89-90,180-181,255-257` | `SectionTitle FontSize=9.5` + `CharacterSpacing=80` | `12 SemiBold`，去掉 `CharacterSpacing` | P12 |
| F16 | `MainWindow.xaml:267-270` | `ComboBox Height="34" CornerRadius="6"` | `Height="30" CornerRadius="4"` | 刻度 |
| F17 | `MainWindow.xaml:277-282` | Footer 两行（34 + 5 + 34）占 73 高 | 合并成一行 4 个入口（28 高），见 6.2 | 紧凑 |
| F18 | `MainWindow.xaml:313-331` | 第二行用 `2*/1*` 分列，Tuning 与 Quit 比例失衡 | 4 等分 | 一致性 |
| F19 | `MainWindow.xaml:45` | `Text="▲"` `FontSize="10"` `#FF4040` 直接写死 | 用 glyph + `BrandBrush`（`Resources.xaml:69`） | 硬编码 |
| F20 | **缺失** | 无展开/折叠按钮 | 标题栏加 28×28 `⌄`，`AutomationProperties.Name="展开"` | 功能 |
| F21 | `MainWindow.xaml.cs:125` | `AppWindow.Resize(new SizeInt32(420, 490))` 物理像素写死 | 常量写 DIP，`ToPhysicalPixels(dip, GetDpiScale())` | P19 |
| F22 | `MainWindow.xaml.cs:128-135` | `PositionAboveTaskbar` 里重复硬编码 `420`/`490` | 用当前 `AppWindow.Size` 反推，并加 `Math.Clamp` 夹工作区 | P20 P21 |
| F23 | `MainWindow.Animation.cs:42,74` | `if (h < 1) h = 490;` 回退值写死 | 用当前状态（折叠 116 / 展开 280）的 design 常量 | 一致性 |
| F24 | `MainWindow.Animation.cs:52` | 滑入 420ms | 收短到 **260ms**（>300ms 的常规 UI 动效不合规） | P-时长 |
| F25 | `MainWindow.Animation.cs:80-84` | 滑出用 `(0.4, 0, 1, 1)` 标准 ease-in | 出场也应该 `ease-out`（`(0.23,1,0.32,1)`），只是更快 | P13 P16 |
| F26 | `MainWindow.Animation.cs:41,73` | 从 `ContentPanel.ActualHeight` 取位移距离 | 位移距离用窗口高，取 `RootGrid.ActualHeight` | 正确性 |
| F27 | **新增** | 无 reduced-motion 分支 | 见 1.6 `UISettings.AnimationsEnabled` | P18 |
| F28 | **新增** | 无 DWM 圆角/边框染色（仅 `DwmSetWindowAttribute` 的 round 在 `MainWindow.xaml.cs:121-123`） | 补 `DWMWA_BORDER_COLOR` 同色覆盖，避免白边 | P1 |

> 顺带：`MainWindow.xaml.cs:121-123` 已经设了 `DWMWCP_ROUND`，但 XAML 又画 16 圆角的大 Border —— 两个圆角不一样大，就是"黑框"观感的一半来源。

---

## 7. 验收清单

改完后逐条对，全过才算完成。

| # | 检查项 | 判定 |
| --- | --- | --- |
| 1 | 折叠态窗口高 = 116±2，宽 = 320 | 打点截图 |
| 2 | 展开态窗口高 = 300±2（R3） | 同上 |
| 3 | 展开/收起往返后，右下角屏幕坐标不变（±1px） | 截图 diff |
| 4 | 展开动画总时长 ≤ 300ms | 录屏数帧 |
| 5 | 中途点两次展开按钮，不出现「回零重播」 | 手动快速点击 |
| 6 | 系统「关闭动画效果」后，窗口瞬切、内容只淡入 | 设置里关 |
| 7 | 四角无黑边、无双线（DWM 圆角 ≈ 内容圆角） | 亮色壁纸下截图 |
| 8 | 全屏无 `12` 以外的大留白；无 `>16` 的 Margin | 搜 XAML |
| 9 | 字号只在 {11,12,14,16} 内 | 搜 XAML |
| 10 | 温度刷新时数字列不抖 | 录屏 |
| 11 | `N/A` 显示 `—`，不留空行 | 断设备复现 |
| 12 | 175% DPI 下 1px 线不消失、文字不裁切 | 改缩放 |
| 13 | 亮色主题下文字可读 | 切主题 |
| 14 | 每个可点元素 ≥ 28×28，且有 hover / pressed 态 | 手测 |
| 15 | 展开区元素常驻（只切 `Visibility`），不重建 | 查代码 |

---

## 附：引用与出处

| 内容 | 出处 |
| --- | --- |
| 动效决策框架、时长表、强曲线 | `skills/emil-design-eng/SKILL.md` L62–145 |
| 组件细节、`scale(0)`、origin、交错 | 同上 L197–344、L605–637 |
| 审查清单 | 同上 L660–676 |
| Standards 精确数值 | `skills/review-animations/STANDARDS.md` L5–187 |
| 构建流程、属性与“高度仅限手风琴” | `skills/animate/SKILL.md` L39–193 |
| 频率闸门 | 同上 L41–50 |
| 苹果流体/弹簧/材质/字体 | `skills/apple-design/SKILL.md` L84–96、L179–197、L226–234 |
| 边界数据与失败特征 | `skills/break-ui/SKILL.md` L20–28、L96–125 |
| 微规则（tabular/tracking） | `ui-cheatsheet.md` L1–15 |
| 参考实现：窗口尺寸动画 | `Dafeiyu-Go/.../source/InfoWindow.cs:475-560`、`:829-898` |
| 参考实现：材质与无边框 | `Dafeiyu-Go/.../source/InfoWindow.cs:285-332`、`:1299-1331`；`LauncherAppearance.cs:17-20,214-314`；`CornerRadiusHelper.cs:1-115` |
| 参考实现：大窗范式（不照搬到小窗） | `SettingsWindow.xaml:60-120,708-733`；`SettingsWindow.xaml.cs:36-40,5902-5933` |
