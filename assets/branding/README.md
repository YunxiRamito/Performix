# Branding assets — placeholders

`performix-logo.png`（256×256）与 `performix.ico`（16/32/48/64/128/256）是**脚本生成的占位图**：
深色圆角方块 + 字母「P」，没有任何 OEM 视觉元素。

它们存在的唯一目的是让构建（`ApplicationIcon`、标题栏 BrandMark、托盘图标）有合法素材可用。
替换为原创品牌图标时，只需覆盖这两个文件，并同步：

- `src/Performix.App/Assets/appicon.ico`（WinUI 程序图标 / 托盘）
- `src/Performix.App/Assets/performix-mark.png`（320 DIP 标题栏品牌标）
- `src/Performix.Legacy.Wpf/appicon.ico`（WPF 回滚版）

不要放回任何 Acer Predator 爪形标志或其他厂商商标图形 —— 原因见
[../docs/LICENSING.md](../docs/LICENSING.md) §3.1。
