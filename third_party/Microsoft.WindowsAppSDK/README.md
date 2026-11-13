# third_party/Microsoft.WindowsAppSDK

这里只放**许可与声明原文**，不放二进制、不放源码。

- 组件：`Microsoft.WindowsAppSDK` 1.6.250205002（NuGet）
- 许可：Microsoft Software License Terms（包内 `license.txt`）
- 来源：NuGet 包 `microsoft.windowsappsdk/1.6.250205002` 原样复制的 `license.txt`、`NOTICE.txt`
- 上游：<https://github.com/microsoft/windowsappsdk>

说明：

- `Performix.App` 以 `WindowsAppSDKSelfContained=true` 把 Windows App Runtime 打进输出目录，
  属于官方支持的再分发方式，不修改任何 Microsoft 二进制。
- 界面使用 **Segoe Fluent Icons / Segoe MDL2 Assets** 系统字体绘制图标字形，
  **不随仓库分发字体文件**；字体随 Windows 授权。
- 依赖链里的其他 Microsoft 包（`Microsoft.Windows.SDK.BuildTools`、`Microsoft.Web.WebView2` 等）
  由 NuGet 按其各自许可还原，不进入仓库。
