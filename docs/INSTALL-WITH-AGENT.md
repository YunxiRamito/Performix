# 让 AI Agent 帮你装 Performix

这份文档是 [README](../README.md) 里「安装教程」那一节的展开版:同一段提示词,加上一段「为什么建议这么装」。

## 为什么用 Agent 装

Performix 不是双击完事的那种软件,它的安装有三道普通人迈不过去的坎:

- **要先确认机型。** 每台笔记本控制风扇、灯效的方式都不一样,同一个品牌的不同型号也常常不同。装机之前得先知道这台机器在不在[适配列表](../README.md#适配列表)里,不在的话后面出问题要按另一套流程处理。
- **要补一个微软的运行组件。** 程序依赖 .NET 10 桌面运行时,缺了就打不开,而这东西要单独下载、单独安装、还要点管理员确认。
- **要判断「装好了」到底是什么样。** 程序装完不会弹「安装成功」,它只是安静地待在托盘区。托盘图标起没起来、温度和转速有没有数字,才是真正的验收标准。

这三件事对普通用户来说都太抽象。对 Agent 来说,它们只是读一个文件、跑一条命令、看一眼输出。

真正需要 Agent 的时候是**装完之后**:机型不适配、运行时报错、界面空白、读不到温度——这些现场用户很难描述清楚,但日志里全都有。Agent 能直接读日志、自己定位、改配置,能修的提 PR,修不了的把机型、BIOS 版本、日志、复现步骤整理成一条 Issue。这比让用户手抄一段红色报错有用得多。

还有一个现实原因:这个项目的适配面本来就靠社区反馈一点点铺开。Agent 装完顺手就把机型、系统版本、哪些功能能用这些信息收集好了,发出去就是一条可用的适配反馈。

## 提示词

把下面这一整块复制给 Agent,不要分开发。

```text
帮我在 Windows 11 上装好 Performix。

Performix 是一个给游戏笔记本用的小工具:调风扇、切性能模式、改键盘灯效、换屏幕刷新率,托盘图标看状态。它不改固件、不装驱动、不超频。仓库在 https://github.com/YunxiRamito/Performix

安装过程你自己走完,别每一步都问我。只有需要管理员权限、或者需要我做一个选择的时候,再停下来问一句。

第一步,先搞清楚要装什么
1. 读仓库根目录的 README.md,再读 docs/RELEASE.md。弄清这个程序是做什么的、装到哪个目录、有哪些前置条件。
2. 查我的机器型号,和 README 里的「适配列表」对一下,告诉我这台机器在不在列表里。不在也继续装,但后面出问题要按第五步处理。

第二步,准备运行环境
3. 检查本机有没有 .NET 10 桌面运行时(程序必需,是微软的一个组件)。
   在 PowerShell 里看一眼:C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App 下有没有 10.x 开头的目录,或者跑 dotnet --list-runtimes,看有没有 Microsoft.WindowsDesktop.App 10.x。
4. 缺的话,从 https://dotnet.microsoft.com/download/dotnet/10.0 下载 .NET 10 Desktop Runtime 的 Windows x64 版本装上。这一步会弹管理员确认,装之前先跟我说一声。

第三步,下载并安装
5. 打开 https://github.com/YunxiRamito/Performix/releases,找最新的那个发布版本。
6. Release 里有两个可用的东西,选一个:
   - PerformixSetup.exe:直接跑就行。它会自己补运行时,装到 %LOCALAPPDATA%\Performix\app\<版本号>\ 下,并建一个开始菜单快捷方式。
   - Performix-win-x64.zip:解压后整个放到 %LOCALAPPDATA%\Performix\app\<版本号>\ 里。版本号用发布页上写的那个,例如 0.1.0-alpha.1,最后路径类似 %LOCALAPPDATA%\Performix\app\0.1.0-alpha.1\Performix.exe。这个目录的最外层必须能直接看到 Performix.exe。
7. 别装到 C:\Program Files,别装到需要管理员权限才能写的目录。程序会把设置保存在自己的目录里,装在当前用户目录下最省事。
8. 运行时 Windows 可能提示「Windows 已保护你的电脑」(程序没买商业签名证书)。这种点「更多信息」,再点「仍要运行」。

第四步,确认装好了
9. 启动 Performix.exe。第一次会弹管理员权限确认(调风扇必须有这个权限),点「是」。
10. 确认右下角托盘区(时间旁边)出现 Performix 图标,点开能看到面板。
11. 等半分钟,看面板上的温度和风扇转速有没有数字。有数字说明能读到硬件,装成功了。

第五步,出问题你自己往下查
12. 如果我的机型不在适配列表里,或者报错、界面空白、读不到温度、切模式没反应:
    - 先自己看日志,别让我手抄报错。日志有两个地方:程序目录里扩展名为 .log 的文件(和 Performix.exe 同一层),以及 %LOCALAPPDATA%\Performix\logs\(更新相关的是 update.log 和 update-check.log)。
    - 自己定位原因。能修的,改完整理成 Pull Request 提到仓库,说明改了什么、为什么这么改、怎么验证的。
    - 修不了的,帮我整理成一条 Issue 提到 https://github.com/YunxiRamito/Performix/issues(表单地址:https://github.com/YunxiRamito/Performix/issues/new?template=bug_report.yml),写清机型、BIOS 版本、Windows 版本、.NET 运行时版本、Performix 版本、什么现象、复现步骤、相关日志、截图。
    - 提交 Issue 或 PR 之前,先把内容给我看一遍,确认里面没有我的账号、序列号、机器名这类私人信息。

第六步,这些事不要做
13. 不要静默改系统设置:注册表、电源计划、服务、开机启动项。要改先说明,等我说可以。
14. 不要装或更新驱动,不要刷 BIOS,不要超频,不要关 Windows Defender,不要改安全策略。
15. 不要为了「试试看」在我机器上跑别的机型的硬件指令。

最后用几句大白话告诉我:装到哪了、怎么打开、哪些功能能用、哪些还不能用。
```

## Agent 会碰哪些东西

装完之后文件落在这些地方,想自己核对可以对着看:

| 位置 | 里面是什么 |
| --- | --- |
| `%LOCALAPPDATA%\Performix\app\<版本号>\` | 程序本体,`Performix.exe` 在这个目录最外层 |
| `%LOCALAPPDATA%\Performix\app\<版本号>\preysense.json` | 你的设置(风扇曲线、性能模式、灯效等) |
| `%LOCALAPPDATA%\Performix\app\<版本号>\*.log` | 运行日志,出问题时最先看这里 |
| `%LOCALAPPDATA%\Performix\logs\` | 更新相关日志(`update.log`、`update-check.log`) |
| `%LOCALAPPDATA%\Predex\` | 界面外观和窗口状态 |
| `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Performix.lnk` | 开始菜单快捷方式(用 `PerformixSetup.exe` 装才有) |

## 不想用 Agent

打开 [Releases 页面](https://github.com/YunxiRamito/Performix/releases),下载 `PerformixSetup.exe` 双击运行,它会自己检查并补上运行时,然后装到上面那个目录。装完在开始菜单里打开,或者双击 `%LOCALAPPDATA%\Performix\app\<版本号>\Performix.exe`。

发布包里每个文件的用途、更新是怎么推的,写在 [docs/RELEASE.md](RELEASE.md)。

## 装不上或者用不了

- 装机过程中的报错、卡住的地方,按 README 里的[适配提示词](../README.md#适配提示词)把信息备齐,发到 [Issues](https://github.com/YunxiRamito/Performix/issues/new?template=bug_report.yml)。
- 风扇在退出程序后没有回到机器自带的自动模式,先看 README 的[常见问题](../README.md#常见问题),那里有一个恢复脚本。
