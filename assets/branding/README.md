# Branding assets — previous generation (not used by the build)

`performix-logo.png`（256×256）与 `performix.ico`（16/32/48/64/128/256）是**上一代的几何占位图**：
深色圆角方块 + 字母「P」，没有任何 OEM 视觉元素。

构建**不再引用**这里的两个文件。现役图标是自绘的码表图（蓝色超椭圆底 + 白色码表弧与指针），
母版 `../brand/gauge-26b/performix-icon-4k.png`，由 `../brand/build-icons.py` 一条命令重建：

- `src/Performix.App/Assets/appicon.ico`（WinUI 程序图标 / 托盘）
- `src/Performix.App/Assets/performix-mark.png`（16 DIP 标题栏品牌标，只有码表字形）
- `src/Performix.Legacy.Wpf/appicon.ico`（WPF 回滚版，与上面同一份字节）
- `assets/brand/performix*.png|ico`、`assets/brand/preview-*px-x8.png`（放大样张）

留在这里的旧文件只作历史对照；要换图标请改母版位图后跑 `python -B assets/brand/build-icons.py`，
不要手动覆盖单个尺寸。

不要放回任何 Acer Predator 爪形标志或其他厂商商标图形 —— 原因见
[../../docs/LICENSING.md](../../docs/LICENSING.md) §3.1。
