# Performix

<p align="center">
  <img src="assets/brand/performix-app.png" alt="Performix 图标" width="128" height="128">
</p>

> **⚠️ 早期测试阶段**a
>
> 目前只在一台机器上验证过(PH317-55)。如果你用的是别的机型,非常欢迎把适配思路、日志、报错发到 [Issue](https://github.com/YunxiRamito/Performix/issues/new?template=bug_report.yml)——哪怕是「我试了、不行、卡在哪」这种也很有用。

**Performix 是一个给 Windows 游戏笔记本用的控制中心。不用再打开厂商那个又大又慢的控制中心,就能直接调风扇、性能模式、键盘灯效和屏幕刷新率。**

适合想让笔记本更安静、更凉或更能打,又不想在后台装一堆服务的人。目前只适配了一款机型(见[适配列表](#适配列表)),其它型号和品牌正在陆续加入,欢迎你来帮忙。

## 简介

Performix 可以帮你:

- **调风扇**:自己设定转速曲线,想安静还是想压温度,由你决定。
- **切换性能模式**:从省电安静到火力全开,一键切换。
- **改键盘灯效**:静态、动态灯效都能设置。
- **换屏幕刷新率**:在省电和流畅之间切换。
- **随时看状态**:托盘图标和悬浮窗实时显示温度、转速等信息。
- **开机自动恢复**:你上次调好的设置,开机后会自动用上。

它不会做的事:

- 不改机器的固件,不装驱动,不超频,不破解。
- 不会自己偷偷联网。只有你在更新页点按钮时才会检查更新,也不会静默自动更新。

想了解它的工作原理?技术文档都放在 [docs](docs/) 文件夹里,从[架构说明](docs/ARCHITECTURE.md)看起即可。

## 安装教程

安装这一步不用你自己来。下面这段提示词会交给 AI Agent 去做,你只要复制、粘贴、等它跑完。

为什么让 Agent 装:装之前要确认机型、要补一个微软的运行组件、要确认托盘图标真的起来了;出问题还要看日志、判断卡在哪一步——这些对普通用户都太难,对 Agent 只是一次读文件加几条命令。

### 安装方式:把下面这段提示词粘给你的 AI Agent

把下面这一整块复制下来,发给你的 AI Agent(能读文件、能跑命令的那种,Copilot、Claude、Cursor、DeepSeek 都行),然后等它回报结果。

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
   - Performix-win-x64.zip:解压后整个放到 %LOCALAPPDATA%\Performix\app\<版本号>\ 里。版本号用发布页上写的那个,例如 0.1.0-alpha.2,最后路径类似 %LOCALAPPDATA%\Performix\app\0.1.0-alpha.2\Performix.exe。这个目录的最外层必须能直接看到 Performix.exe。
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

同一份提示词也放在 [docs/INSTALL-WITH-AGENT.md](docs/INSTALL-WITH-AGENT.md),里面还写了为什么建议这么做、Agent 会碰哪些目录。

**不想用 Agent 的话:** 自己打开 [Releases 页面](https://github.com/YunxiRamito/Performix/releases),下载 `PerformixSetup.exe` 双击安装,它会自己把运行时装上。装好的程序在 `%LOCALAPPDATA%\Performix\app\<版本号>\` 下,开始菜单里也能找到。发布流程和这些文件的来龙去脉写在 [docs/RELEASE.md](docs/RELEASE.md)。

## 适配提示词

你的机型不在列表里,或者列表里的机型用着不对劲,请告诉我们。这是目前最能帮到项目的事。

为什么需要你的信息:每台笔记本控制风扇、灯效的方式都不一样,同一个品牌的不同型号也常常不同。硬件指令发错有风险,所以 Performix 不会拿别的机型的做法来「试试看」,每个机型都要靠真机实测。

### 用着有问题时,请在 Issue 里提供这些信息

[点这里打开适配反馈表单](https://github.com/YunxiRamito/Performix/issues/new?template=bug_report.yml),照着表单填就行。表单里的内容就是下面这张表,你也可以先对照着把信息准备好:

| 信息 | 怎么找到 |
| --- | --- |
| 机型全称 | 按 `Win` + `R`,输入 `msinfo32`,看「系统型号」 |
| BIOS 版本 | 同一个窗口里的「BIOS 版本/日期」 |
| 处理器和显卡 | 打开任务管理器,在「性能」页里查看 |
| Windows 版本 | 按 `Win` + `R`,输入 `winver` |
| Performix 版本 | 程序里的更新页会显示 |
| 原厂控制中心 | 机器自带的控制软件叫什么、什么版本(例如 PredatorSense)。没有装也请写明 |
| 出了什么问题 | 你点了什么,期望发生什么,实际发生了什么。涉及哪项功能:风扇、性能模式、灯效、刷新率还是悬浮窗 |
| 复现步骤 | 一步一步写下来,别人照着做能看到同样的问题 |
| 截图或录屏 | 可选,但很有帮助 |
| 日志文件 | 程序目录里扩展名为 `.log` 的文件,以及 `%LOCALAPPDATA%\Performix\logs\` 里的 `update.log`、`update-check.log` |

发日志和截图前,请先看一眼内容,不要带上账号、序列号等个人信息。

懒得手抄的话:上面[安装](#安装教程)那段提示词里已经写了这一条。让 Agent 去读日志、把 Issue 整理好,你只要看一眼内容再点提交。

### 没出问题也来说一声

没出问题也欢迎来告诉我们。如果你的机型用起来一切正常,发一条标题带「适配反馈」的 Issue,写上机型和哪些功能可用,我们就能把它加进适配列表。对早期项目来说,「能跑」和「不能跑」一样有价值——只有知道哪些机器是真的通了,才敢往下扩。

### 想自己动手?欢迎提 PR

- **小改动**:错别字、文档、说明不清楚的地方,直接提 [PR](https://github.com/YunxiRamito/Performix/pulls) 就行,不用先问。
- **适配新机型**:建议先发一条 Issue 说明你要做哪个机型,避免重复劳动。PR 里请附上这台机器的实测记录,可以参考 [PH317-55 的实测记录](docs/FINDINGS-PH317-55.md)。每个新机型需要的具体要求,写在[架构说明](docs/ARCHITECTURE.md)的「未来扩展」一节。

**PR 说明:** 新建 PR 时,页面里会自动出现一份[说明模板](.github/PULL_REQUEST_TEMPLATE.md)。按里面的几个问题简单写清「改了什么、为什么改、怎么验证的」就行,不用写得很正式。

不管是一条 Issue、一张截图还是一个 PR,都会让更多人的笔记本用上 Performix。

## 适配列表

| 品牌 | 机型 | 处理器 / 显卡 | 状态 | 验证过的功能 | 说明 |
| --- | --- | --- | --- | --- | --- |
| Acer | Predator PH317-55(Helios 300) | Intel i9-11900H / RTX 3070 Laptop | 已适配 | 风扇曲线、性能模式、键盘灯效、温度与转速读取、屏幕刷新率 | 首个实测机型,在 BIOS V1.14、PredatorSense 3.0.3196 环境下测试,详见[实测记录](docs/FINDINGS-PH317-55.md) |

表里没你的机型,不代表不能用,只是没人试过。同型号但处理器或显卡配置不同的机器,也欢迎反馈使用情况。想让你的机型出现在这张表里,请参考上面的[适配提示词](#适配提示词)。

## 首次使用建议

先切换一下性能模式,确认温度和风扇转速都正常,再去自定义风扇曲线。风扇声音或温度不对劲时,先退出 Performix,再看下面的[常见问题](#常见问题)。

## 更新

打开程序里的更新页,点「检查更新」,有新版本时再点「下载并安装」。程序会自动退出、替换文件并重新打开。

## 卸载

1. 退出 Performix,并打开任务管理器确认里面已经没有 `Performix.exe`。
2. 删掉 `%LOCALAPPDATA%\Performix\app\` 下面那个版本文件夹(例如 `%LOCALAPPDATA%\Performix\app\0.1.0-alpha.2\`)。当初用 `PerformixSetup.exe` 装的话,也可以运行 `PerformixSetup.exe --uninstall`,它会删快捷方式和程序目录,并把设置文件搬到 `%LOCALAPPDATA%\Performix\` 再告诉你位置。

删掉程序目录后,系统里还会留下两处很小的文件。它们不影响系统,也不会让程序继续运行,你可以留着,也可以手动清理:

| 位置 | 里面是什么 | 要不要清 |
| --- | --- | --- |
| `%LOCALAPPDATA%\Performix\logs\` | 更新日志 | 不用清。想清就整个删掉 |
| `%LOCALAPPDATA%\Predex\` | 界面外观和窗口状态 | 不用清。以后重新装的话,会接着用这里的外观设置 |

手动清理的方法:按 `Win` + `R`,输入 `%LOCALAPPDATA%` 后回车,在打开的文件夹里找到上面两个文件夹,删掉即可。`Predex` 是程序早期的名字,所以文件夹名和现在不一样。

## 常见问题

### 风扇声音或温度不对劲,怎么把风扇还给机器自带的自动模式?

先退出 Performix,再运行恢复脚本 `Restore-AutoFan.ps1`,它会把风扇交还给机器自带的自动模式。

这个脚本只在仓库里,不在安装包里。它还依赖同一个文件夹里的 `PSHardware.ps1`,所以两个文件要一起下载:

1. 打开[仓库的 scripts 文件夹](scripts/),把 [Restore-AutoFan.ps1](scripts/Restore-AutoFan.ps1) 和 [PSHardware.ps1](scripts/PSHardware.ps1) 都下载下来,放在同一个文件夹里。点链接打开的是文件预览页,在页面右上角点下载按钮(向下的箭头)即可。懒得一个个下的话,也可以在仓库首页点「Code」→「Download ZIP」,解压后进 `scripts` 文件夹。
2. 安装 PowerShell 7(Windows 自带的蓝色 PowerShell 5 不行)。可以在微软商店搜索「PowerShell」安装。
3. 以管理员身份打开 PowerShell 7,进入存放脚本的文件夹,运行:

   ```powershell
   pwsh -ExecutionPolicy Bypass -File .\Restore-AutoFan.ps1
   ```

运行成功后会打印当前的温度和风扇转速。这个脚本只对适配列表里的机型有效,其它机型运行它没有意义。

不会操作,或者运行后还是不对,请到 [Issues](https://github.com/YunxiRamito/Performix/issues/new/choose) 告诉我们。

---

Performix 以 [GPL-3.0](LICENSE) 协议开源,许可细节见 [docs/LICENSING.md](docs/LICENSING.md)。Acer、Predator 是宏碁股份有限公司的商标,本项目与宏碁没有关联。
