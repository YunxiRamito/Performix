# 当前页面树（as-is）

> 从 `Shell\ShellWindow.xaml.cs` 的 `PageTypes` 映射 + 各页 `NavigateTo` 调用点读出，不是设计稿。

```
首页 Home                         ← 启动页；折叠 116 / 展开 300 DIP
 ├─ 灯光 Lighting                 ← 首页底部图标
 ├─ 游戏浮窗 Overlay              ← 首页底部图标
 ├─ 风扇 Fans                     ← 首页底部图标
 ├─ 进阶调校 Tuning               ← 首页底部图标
 │    └─ 风扇 Fans                ← 调校页里的「打开曲线编辑器」
 └─ 设置 Settings                 ← 首页底部图标
      └─ 主题 Theme               ← 设置页里的「主题」行
```

## 实际层级

```
第 1 层   Home
第 2 层   Lighting / Overlay / Fans / Tuning / Settings
第 3 层   Theme（只从 Settings 进）
```

- 一共 **7 个页面**：`HomePage` / `LightingPage` / `OverlayPage` / `FansPage` / `TuningPage` / `SettingsPage` / `ThemePage`
- 导航 key 与类名一一对应（`PageTypes` 字典，`ShellWindow.xaml.cs:42-50`）
- `Fans` 有**两个入口**：首页底部图标、调校页里的「打开曲线编辑器」
- `Theme` 是唯一的第 3 层页

## 返回行为

- 返回键回哪一级由 `ShellWindow.xaml.cs` 的 `_currentTag` 逻辑决定：
  - 从 **Settings → Theme**：返回回 **Settings**
  - 其余二级页（Lighting / Overlay / Fans / Tuning）：返回回 **Home**
- `NavigateTo("Home")` 是回到根，不带返回栈（`ShellWindow.xaml.cs:823`）

## 窗口与状态

- 折叠 320×116、展开 320×300 只在 **Home** 生效
- 二级页统一 320×520 DIP 封顶，内容超出页内滚动
- 标题栏：图标 + `PredeX` + 页面名 + 展开箭头（无最小化/最大化/关闭按钮）
- Esc / 点别处 → 收起；托盘图标、Predator 键 → 显隐；狂暴键（ACPI）→ 切狂暴

## 页面里还没有子页的东西

- `Lighting` 里的**单键编辑**目前是页内区块，没有独立子页
- `Settings` 还没拆分成「常规 / 电源 / 主题 / 更新 / 关于」等二级页（现在是一页平铺，主题行跳到 `Theme`）
