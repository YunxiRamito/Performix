# Predex 检查更新 / 下载安装 设计方案

> 只读调研 + 方案，不改任何源码。调研对象：同作者的 `Dafeiyu-Go`（启动器 + 安装器双项目）。
> 结论先行：**照抄 Dafeiyu 的「三档源 + 组内并行 / 组间串行 + 清单强制 SHA-256 + .part 续传 + 脚本延迟替换」骨架**，但**不要**照抄它的独立 info-host 进程（Predex 单进程 + 托盘，收益不抵成本，见 2.5）。
>
> 文档约定：
> - `Click-To-Run/` = `Dafeiyu-Go/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run/`
> - `Setup/` = `Dafeiyu-Go/Dafeiyu-Go-DeepSeek-Harness-Setup/`
> - `Predex/` = `PredatorSense/app/experiments/Predex.WinUI/`（当前在跑的 WinUI 3 前端）
> - 所有行号基于本次阅读时的文件内容。

---

## 0. 一页速览

| 主题 | Dafeiyu-Go 的做法 | Predex 该抄什么 |
|---|---|---|
| 清单入口 | 仓库根 `manifest.json`，`versions/manifest-<ver>.json` 归档 | 抄：`manifest.json` 固定协议入口 + `manifests/` 归档 |
| 国内源 | gh-proxy.com / ghproxy.net / ghfast.top / jsDelivr + 官方 API 兜底 | 抄顺序与「探活 + 只信 206」判据，**弃 ghfast**（实测全超时） |
| 清单拉取 | 组内并行、组间串行，8s 总预算 / 4s 每组 | 原样抄 |
| 版本比较 | 自研 SemVer 2.0 子集，支持 `0.1.0-alpha.1`、build metadata 不参与 | 直接抄实现思路 |
| 通道 | 不同文件名（`manifest.json` / `manifest-preview.json`） | 抄，扩成 stable/beta/alpha 三个文件 |
| 校验 | 清单 sha256 必填，缺失直接拒绝下载 | 抄，另加 `sizeBytes`（Predex 要多一层大小先行校验） |
| 下载 | `%TEMP%` 下 GUID staging、`.partN` 分片、If-Range 续传、20s 停滞看门狗 | 抄（分片可先只上单连接 + 续传） |
| 进度 | 独立 `info-host` 进程 + 380×150 浮窗，下载占 65% 条 | 形态抄，进程模型不抄 |
| 替换 | PowerShell 延迟替换脚本 + 备份 + schtasks 重启 | 抄脚本骨架，重启方式简化（Predex 本来就提权） |
| 签名 | **未做**（SignPath 免费申请被拒，见 `Click-To-Run/SIGNING.md:1-23`） | 同样不做，但文档里不许把 sha256 说成"签名" |

---

# 1. 参考实现事实（Dafeiyu-Go）

## 1.1 更新源：用了哪些、怎么排序、怎么 fallback、超时与重试

### 1.1.1 源列表（启动器侧）

`Click-To-Run/source/GitHubAccelerator.cs:22-55`：

```csharp
internal static readonly AcceleratorSource[] Sources = new AcceleratorSource[]
{
    new AcceleratorSource { Id = "backend", Name = "后端服务器加速",
        Prefix = "https://202.189.21.218:8787/api/download?url=", SupportsRanges = true },
    new AcceleratorSource { Id = "ghproxy",   Name = "ghproxy.net",  Prefix = "https://ghproxy.net/" },
    new AcceleratorSource { Id = "gh-proxy",  Name = "gh-proxy.com", Prefix = "https://gh-proxy.com/",
        // 实测只有它回 206（支持 Range），所以分片下载优先挑它。
        SupportsRanges = true },
    new AcceleratorSource { Id = "ghfast",    Name = "ghfast.top",   Prefix = "https://ghfast.top/" },
    new AcceleratorSource { Id = "jsdelivr",  Name = "jsDelivr",     SupportsArchive = false },
};
```

- **没有自建 CDN**（没有 `cdn.xxx.com` 之类的静态资源域名）。唯一自有基础设施是**后端服务器** `https://202.189.21.218:8787`（`Click-To-Run/source/BackendDownloadSource.cs:15`），本质是「带证书 pin 的下载代理」，不是 CDN。
- **GitCode 未找到**：全项目 `gitcode` 零命中。国内镜像就是上面三个 GitHub 反代 + jsDelivr + npmmirror（npm 维度）。
- 可加速的 host 白名单：`GitHubAccelerator.cs:74-82`（`github.com` / `raw.githubusercontent.com` / `codeload.github.com` / `objects.githubusercontent.com` / `gist` / `github-releases`）。

### 1.1.2 排序与 fallback

| 场景 | 规则 | 证据 |
|---|---|---|
| 「自动」（默认档） | 启动时逐源测一次延迟 → 按**实测延迟升序**排；超时/没测到的排后面（稳定排序） | `GitHubAccelerator.cs:161-205` |
| 用户自选某源 | 选中的排第一，其余按默认顺序兜底，**不再按延迟重排** | `GitHubAccelerator.cs:139-159` |
| 选「后端服务器加速」 | **只返回自己，绝不回退到其它源** | `GitHubAccelerator.cs:142-145`、`UpdateMetadataReader.cs:31-36` |
| 选「官方源」 | 只给原始地址，不套任何前缀 | `GitHubAccelerator.cs:221-233`（`IsEnabled` 判定）、`UpdateSupport.cs:176-177,185-189` |
| 默认顺序常量 | `ghproxy → gh-proxy → ghfast → jsdelivr` | `GitHubAccelerator.cs:61-67` |
| 测速 | 串行测（并发会互抢带宽），单源 10s 超时，结果落盘 `AcceleratorProbe.json` | `AcceleratorLatency.cs:57,132-135,183-197` |
| 何时重测 | 启动 + 网络可用性/地址变化 + 用户改代理，10 秒防抖 | `AcceleratorLatency.cs:340-348,381,410` |

> 坑：`ghfast.top` 位置是**实测证据**决定的，不是审美。`HANDOVER.md:1076-1086` 有整张实测表：`codeload 0.65s ✅ / ghproxy 2.7s ✅ / gh-proxy 2.8s ✅ / ghfast 超时 ❌ / 直连归档超时 ❌ / jsDelivr(raw) 超时 ❌`。安装器侧见 `Setup/src/DshInstaller.Shared/Install/MirrorSource.cs:273-282`，注释直接写「ghfast **每次都超时**，直接删掉，别再让用户等它」，最终池子只剩 `gh-proxy.com → ghproxy.net`。

### 1.1.3 清单拉取的「组」结构

`Click-To-Run/source/UpdateSupport.cs:39-90` `ResolveManifestGroups()`：

- 优先读同目录 `launcher.json` 的 `updateManifestUrls`（字符串或数组），**每个 URL 各自成为一个优先级组**（`UpdateSupport.cs:42-61`），且过期配置不许打穿当前源选择（`:52-59`）。
- 否则：`branch=main`，`Repositories = { 当前仓库, Legacy 旧仓库 }`（`:27-33`），正式通道只要 `manifest.json`，预览通道先 `manifest-preview.json` 再退回 `manifest.json`（`:63-66`）。
- 每个仓库 → 一组 raw 候选（`GitHubAccelerator.RawCandidates`），然后**额外**追加一组 `https://api.github.com/repos/<repo>/releases/latest`（`:79-88`），注释写明理由：API 只当稳定兜底，放低优先级组，免得慢镜像被"更旧的 release 响应"抢答。

### 1.1.4 超时 / 并发预算（清单）

`Click-To-Run/source/UpdateMetadataReader.cs:15-18`：

```csharp
internal const int TotalBudgetMilliseconds = 8000;   // 整次清单预算
internal const int GroupBudgetMilliseconds = 4000;   // 每一组预算
internal const int MaximumResponseBytes = 8 * 1024 * 1024;
private const int MaximumConcurrentRequests = 6;
```

- **组内并行**（最多 6 条同时在飞），**组间串行**：一组跑完（或 4s 超时）才进下一组（`:53-98`）；先拿到合法清单的赢，其余任务被 `group.Cancel()` 掐掉，不等待。
- 后端模式给后续候选 +500ms 延迟错峰（`:61-63`）。
- 单请求 `Timeout`/`ReadWriteTimeout` 都设成组预算（`:151-152`）。
- 失败文案：全超时 → 「更新检查超时，请稍后重试或切换下载源。」；否则 → 「更新源暂不可用：<最后一条原因，裁到 160 字> 详细原因见日志。」（`:105-118`）。

### 1.1.5 超时 / 重试（下载）

| 参数 | 值 | 证据 |
|---|---|---|
| 连接+响应头超时 | 15s（`request.Timeout`） | `DownloadSupport.cs:165` |
| 单次读写超时 | 30s（`ReadWriteTimeout`） | `DownloadSupport.cs:165` |
| 停滞判定 | **20s 没有新字节**就 `Abort()` 换源 | `DownloadSupport.cs:83,172-192` |
| 每分片重试 | 2 次（`MaxRetryPerSegment`），退避 0.4s 递增 | `DownloadSupport.cs:84,399-401` |
| 分片数 | 4 线程（`DefaultThreads`，最小分片 512KB） | `DownloadSupport.cs:80-81` |
| 自动重试（安装器包） | `automaticRetries: Int32.MaxValue` | `InstallerUpdateService.cs:689` |
| 安装器引擎响应头超时 | 8s；停滞 20s；换源间隔 1.2s | `DownloadEngine.cs:86-104` |
| 安装器引擎「慢源换源」 | 连续 5s 低于 60 KB/s 判死；后台测速要**快 1.2 倍**才值得换 | `DownloadEngine.cs:610-628` |
| 分段（8 连接） | 8 条连接**合计** < 60 KB/s 才算源没带宽 | `SegmentedDownloader.cs:16,57,371` |

历史教训（`CHANGELOG.md:491-496`）：1.3.21 之前单源连接超时是 10 分钟，「遇到连不上的镜像（`ghfast.top`）会对着进度条卡十分钟才换下一个源」，后来压到 15s 连接 / 3 分钟整体。

### 1.1.6 后端服务器的证书 pin（自建基础设施的唯一范例）

`Click-To-Run/source/BackendDownloadSource.cs`：

- `:15-17` `BaseUrl = https://202.189.21.218:8787`，`Pin = 2c72…6f0b`（SPKI 公钥 SHA-256）。
- `:112-120` 校验方式是 `SHA256(certificate.PublicKey.ExportSubjectPublicKeyInfo())` 与 pin 做 `CryptographicOperations.FixedTimeEquals`，**不做系统链校验**；`:106-110` 只有后端 URL 走 pin，其它地址仍要系统证书校验通过。
- `:84-104` 后端下载是两段式：先 `GET /api/download?url=`，再轮询 `/api/download/status?` 直到 `ready=true`，最长 15 分钟。
- 代价：这套东西**要一台服务器 + 自有 CA + 15 分钟预算**，Predex 现在没有发布流程，不该碰（见 4）。

---

## 1.2 版本比较：SemVer 解析、是否支持预发布、"有新版"判定、灰度/强制

### 1.2.1 解析实现

`Click-To-Run/source/ProductVersion.cs`，注释写明「SemVer 2.0 子集」，三个产品（启动器清单 / DSH 本体 / 安装器）共用一份实现，避免规则漂移（`:6-11`）。

| 能力 | 实现 | 证据 |
|---|---|---|
| 可选前缀 `v`/`V` | 先剥掉 | `:106-110` |
| build metadata | `+` 之后整段丢弃，不参与比较 | `:112-116`、`:30-31` 注释 |
| 预发布 | 第一个 `-` 之后整段存 `PreRelease`（**支持 `alpha.1` / `rc.1`**） | `:118-128` |
| 核心段数 | **2~4 段**都接受（`1.5.4`、`1.5.4.0`、`1.7.1.0`） | `:130-134` |
| 数字解析 | `Int32.TryParse(NumberStyles.None)`，负数/空段拒绝 | `:140-174` |
| 预发布比较 | 无预发布 > 有预发布；逐段比较：纯数字按数值（先比去零长度再字典序），数字段 < 非数字段，其余 `String.CompareOrdinal`；段数多者更大 | `:63-92,187-217` |
| 无法解析 | `Compare` 直接返回 0（等于"不比"），`IsValid` 让调用方区分"真相同"和"读到垃圾" | `:14-27,32-40` |

### 1.2.2 "有新版"判定

`Click-To-Run/source/UpdateSupport.cs:496-505`：`IsNewer(remote, local) => Compare(remote, local) > 0`。
调用点 `WinUIProgram.cs:2491-2509`：不新 → `UpdateUiActivity.UpToDate` + 可选通知「启动器已经是最新版 vX」→ 结束。

安装器侧另有一层策略（`InstallerUpdateService.cs` + `tests/UpdateIntegrity.Regression/Program.cs:132-161`），值得抄的判断表格：

| 本地 | 远端 | 结论 |
|---|---|---|
| `1.5.4` | `1.5.4` | UpToDate |
| `1.6.0-rc.1` | `1.6.0` | **Update**（release 压过自己的预发布） |
| `1.5.4` | `1.6.0-rc.1` | Update（预发布也大于旧稳定版） |
| `1.7.1.0` | `1.7.1` | UpToDate（四段版本能对上 tag） |
| 本地读不出 | 有新版 | **UnknownLocal：不重装**，只在用户显式点"修复安装器"时才下 |
| 任意 | 远端读不出 | UpToDate：**绝不因为远端异常触发替换** |

### 1.2.3 通道

| 通道机制 | 事实 | 证据 |
|---|---|---|
| 启动器通道 | `LauncherChannel ∈ {Auto, Stable, Preview}`（默认 Stable） | `LauncherSettings.cs:48`、`Backup/DymLauncherBackupContext.cs:35` |
| 通道落地方式 | **不同文件名**：Preview 先读 `manifest-preview.json`，没有就**静默**退回 `manifest.json` | `UpdateSupport.cs:63-66`；`HANDOVER.md:1142` 明确记了这个坑 |
| DSH 本体通道 | 用 npm dist-tags：`latest / next / alpha`，设置里叫正式版/预览版/内测版 | `WinUIProgram.cs` 通道串、`SettingsWindow.xaml.cs:6865-6874` |
| 更新模式 | `LauncherUpdateMode ∈ {Install, Check, Off}`（默认 Install） | `LauncherSettings.cs:33` |
| 检查频率 | `UpdateInterval ∈ {EveryStart, ThreeDays, SevenDays, OneMonth, Off}`，默认 EveryStart | `LauncherSettings.cs:45`、`WinUIProgram.cs:2409-2422` |
| 手动检查 | 绕过频率限制、可接管被服务提示占用的信息窗 | `WinUIProgram.cs:3064-3071`、`1967-1987` |

### 1.2.4 灰度 / 强制更新：**未找到**

- 清单里**没有** `mandatory` / `force` / `minimumVersion` / 灰度百分比字段（`UpdateSupport.cs:277-350` 只解析 `version/sha256/subDirectory/notes/assets`）。
- 只有"启动期自动装"（`LauncherUpdateMode=Install`，`WinUIProgram.cs:2433-2540`），用户可以在设置里改成「仅检查」或关闭（`:2435-2442`）。
- 结论：**Dafeiyu 没有强制更新能力**。Predex 若要"最低支持版本"，属于新增能力，不是照抄。

---

## 1.3 清单格式

### 1.3.1 线上入口

`Click-To-Run/manifest.json`（当前 1.8.2，全 11 行）：

```json
{
  "version": "1.8.2",
  "sha256": "7d30c37e7a5cb053a263caf9b97f109e61382bd4233d4cca6e96eabec2d443f6",
  "subDirectory": "",
  "assets": {
    "github": "https://github.com/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run/releases/download/v1.8.2/Dafeiyu-GO.1.8.2.zip",
    "mirrors": []
  },
  "notes": "优化了已知问题（连接页面）",
  "releasedAt": "2026-10-16T17:04:38.7360939Z"
}
```

`Click-To-Run/manifests/README.md:3-7` 规定：根目录 `manifest.json` 是**稳定协议入口，不准移动**；`versions/manifest-<版本>.json` 归档历史（按原字节保留，可能含早期草稿）；**发版顺序 = 先实际下载并校验最终包 → 写版本清单 → 再更新根入口**。

### 1.3.2 解析规则（`UpdateSupport.cs:277-350`）

| 字段 | 处理 | 证据 |
|---|---|---|
| `version` | 必填，必须能 `ProductVersion.IsValid` | `:330-334` |
| `sha256` | 必填，必须 64 位 hex（`InstallerUpdateService.IsValidSha256`） | `:336-340` |
| `subDirectory` | 可选，解压后进入子目录 | `:294,574-577` |
| `assets.github` | 走 `Mirrorize()` → 展开成「镜像前缀 × N + 原地址」 | `:297-301` |
| `assets.mirrors[]` | 人工挑的额外镜像，去重后追加 | `:303-318`（线上一直是空数组） |
| `notes` | 直接当更新日志正文 | `:295` |
| 简化兼容 | 只有 `{version, url}` 也认 | `:320-328` |
| 解析方式 | **正则抠字段**（不是反序列化），并先校验顶层是 JSON 对象 | `:284-295,465-491` |

> 注意：解析是宽松正则，`assets` 段只取第一个 `{...}`（`MatchNestedString`，`:479-491`），结构一变容易读错——Predex 建议改用 `JsonSerializer` + 强类型（见 2.2）。

### 1.3.3 GitHub API 兜底格式

`UpdateSupport.cs:195-257` 把 `releases/latest` 转成内部结构：
- 版本取 `tag_name`，剥 `v`（`:202-210`）。
- sha256 只认 `assets[].digest` 的 `sha256:` 前缀（`:217-225`）——**这是关键：没有 digest 就不许当安装清单**。
- 只有 `.zip` 资产算数（`:215-216`、`:231-240`）。
- 正文取 `body` 当 notes（`:242-246`）。
- 缺有效版本/哈希直接失败：「GitHub Release 缺少有效版本或 ZIP 的 SHA-256，不能作为安装清单。」（`:227-228`）。

### 1.3.4 架构 / 平台与文件大小

- **启动器清单里没有架构字段，也没有文件大小**：全项目假定 `win-x64` 单架构。
- 有大小 + 平台字段的是 **DSH 完整包清单**，这是最好的模板（`Setup/src/DshInstaller.Shared/Install/DshBundleDeployment.cs:200-225`）：

```csharp
string version = value.GetProperty("version").GetString();
string hash = value.GetProperty("sha256").GetString();
string file = value.GetProperty("fileName").GetString();
long size = value.GetProperty("sizeBytes").GetInt64();
int node = value.GetProperty("nodeMajor").GetInt32();
// 校验：精确版本、platform == "win-x64"、hash 64hex、1 ≤ size ≤ 1GiB、
//      fileName 只是文件名且以 .zip 结尾
```

并且**先比大小、再算哈希**（`:218-225`）：

```csharp
if (new FileInfo(archive).Length != manifest.SizeBytes) throw new InvalidDataException("DSH 完整包大小与清单不一致。");
...
if (!string.Equals(hash, manifest.Sha256, ...)) throw new InvalidDataException("DSH 完整包 SHA-256 校验失败。");
```

- 安装器自己也有清单，逐条目带 sha256：`Setup/src/DshInstaller.Shared/Install/InstallManifest.cs:325,641`。

### 1.3.5 通道怎么带

**不是字段，是文件名**（见 1.2.3）。同一个仓库里并发维护 `manifest.json` 与 `manifest-preview.json` 两个协议文件。

---

## 1.4 下载与校验

### 1.4.1 落到哪个目录

`Click-To-Run/source/UpdateSupport.cs:516-524`：

```csharp
string stagingRoot = Path.Combine(Path.GetTempPath(), "DeepSeekHarnessUpdate",
    "launcher-" + Guid.NewGuid().ToString("N"));   // 每次尝试独占一个 GUID 目录
string downloadPath = Path.Combine(stagingRoot, "launcher.download"); // 扩展名故意不写死
string extractPath  = Path.Combine(stagingRoot, "new");
```

- 独占 GUID 目录的理由写在注释里（`:516-517`）：暂停中的下载/安装器可能还占着兄弟目录，**每次尝试只拥有自己那一个**。
- 成功后由替换脚本清理 staging；失败**保留**用于诊断（`:791-794`）。
- 清理时会验路径归属（父目录 + `launcher-` 前缀 + GUID）并对 reparse point 直接放弃（`UpdateSupport.cs:675-698`、`DownloadSupport.cs:28-78`）。

### 1.4.2 进度怎么算

- **同时有两种口径**：`DownloadProgressInfo` 带 `BytesReceived / TotalBytes / BytesPerSecond`，`TotalBytes = -1` 表示未知（`DownloadSupport.cs:10-24`），此时 `Percent` 返回 **-1**，文案退化成「1.2 MB/s · 已收 8.4 MB」。
- 速度是 EMA 平滑：0.4s 窗口，`speed = speed*0.55 + instant*0.45`（`:405-428`）。
- 界面**不用渲染层的百分比，用自己算的加权**：下载占进度条 **65%**，见 1.5。

### 1.4.3 分片 / 续传

`Click-To-Run/source/DownloadSupport.cs`：

| 环节 | 做法 | 行号 |
|---|---|---|
| 候选再排序 | 先把「已知支持 Range」的源提到前面，探测不过再退回单连接 | `:107-113,152-160` |
| 能力探测 | `Range: bytes=0-0`，必须 **206 + `Content-Range: bytes 0-0/<total>` + `ContentLength==1`**，且资源身份取 `ETag`（弱 ETag `W/` 丢弃）或退化 `Last-Modified` | `:193-230` |
| 分片 | 按字节均分，落 `.part<k>`，旁边写 `.validator`（`total\nidentity`），identity 变了就全部重下 | `:283-307` |
| 分片请求 | `If-Range: <identity>`；校验 206 / Content-Range / 长度 / ETag 是否变化 | `:349-372` |
| 合并 | 每片长度必须精确匹配，合并后总长必须等于 total，否则「拒绝合并」 | `:331-347` |
| 整文件 | 必须 HTTP 200，`ContentLength` 溢出或不足都报错 | `:239-282` |
| 断点 | `.part` 跨"同一次任务的多轮换源"保留；任务结束/失败在 `finally` 删除 `.part` | `:144-150` |
| 重定向 | **允许**（`AllowAutoRedirect = true`），但注意 Range 语义：`AutomaticDecompression = None`（`:166-167` 有注释） |

安装器侧更狠一点（`Setup/src/DshInstaller.Shared/Install/DownloadEngine.cs`）：`.part` + `Range` 续传 + 停滞看门狗 + 后台测速择优选路，注释 `:76-79` 总结为「多准备源 + 失败就换源重试，已经下到一半的数据靠 `.part` 保留，换源也能接着下」。

### 1.4.4 sha256 在哪一步校验

**在「拿到 ZIP」之后、解压之前**，且口径统一：清单 hash 是对 **zip** 算的，npm 那条路先把 tgz 里的 zip 解出来再算（`UpdateSupport.cs:392-453` 的 `MaterializeZip`，按 gzip 魔数 `1F 8B` 判断内容而不是猜扩展名）。

```csharp
// UpdateSupport.cs:560-568
if (!string.IsNullOrEmpty(manifest.Sha256))
{
    string actual = ComputeSha256(packagePath);
    if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
    {
        error = "下载的更新包校验失败(期望 " + manifest.Sha256 + ",实际 " + actual + ")";
        return null;
    }
}
```

安装器版本更聪明：`validateCompleted` 回调**在下载引擎内部**就在换源重试循环里做校验，哈希不对就抛异常 → 引擎自动换下一个源（`Setup/src/DshInstaller.Shared/Install/BuiltInSteps.cs:603-619`）：

```csharp
validateCompleted: path => {
    string candidate = MaterializeLauncherArchive(context, path, release.Version);
    if (!string.IsNullOrEmpty(release.Sha256)) {
        string actual = HashFile(candidate);
        if (!string.Equals(actual, release.Sha256, ...))
        { TryDelete(candidate); throw new InvalidDataException("启动器包校验失败，切换下载源" ...); }
    }
    archive = candidate;
}
```

### 1.4.5 校验失败 / 下载失败怎么处理

| 失败点 | 行为 | 证据 |
|---|---|---|
| 清单缺 sha256 | **拒绝下载**：「启动器更新包缺少有效的 SHA-256，已拒绝下载或安装。」 | `UpdateSupport.cs:513-514` |
| 哈希不匹配（启动器） | 返回 null → UI「更新失败」4 秒 → 结束，不重试 | `UpdateSupport.cs:560-568`、`WinUIProgram.cs:2585-2591` |
| 哈希不匹配（安装器） | 删文件 → 引擎换下一个源重试 | `BuiltInSteps.cs:610-615` |
| 所有候选都不通 | 「所有下载候选都没通。」→ 上层「更新失败」 | `DownloadSupport.cs:141` |
| 缺主程序 | 「更新包里没有 DeepSeek Harness.Core.exe」 | `UpdateSupport.cs:579-584` |
| 缺 SHA-256（只有 302 拿到的版本号） | 「只取得安装器版本，未取得可信 SHA-256；不能自动安装，请重试更新检查。」 | `InstallerUpdateService.cs:118,372-380` |

---

## 1.5 进度 UI

### 1.5.1 形态：独立进程 + 独立小窗

- 进度窗**不是**在主进程里 new 的 XAML 窗口：`InfoWindowClient` 把 `info-host/`（`DafeiyuGo.Info.exe` + dll + pri + xbf）**复制到临时缓存目录再启动**（`Click-To-Run/source/InfoWindowClient.cs:110-145`），用**同名管道**推 `{Command, Title, Detail, Percent, ...}` JSON（`source/InfoHost/Program.cs:14-16`），启动后 8s 内连不上就放弃。
- 更新替换时会退出主进程，为了窗口不死，靠 **`--info-session=` 交接**：替换脚本把 session 传给重启后的新实例（`UpdateSupport.cs:634-637`），新实例 `AttachInheritedHelper` 接管同一个 helper 进程（`InfoWindowClient.cs:210-214`）。
- 窗口建不出来**不能**阻断更新：最多等 8s，失败只写日志「信息窗口创建失败(继续无窗更新)」（`WinUIProgram.cs:2715-2748`）；服务提示正占用时非手动检查直接跳过（`:2709-2713`）。

### 1.5.2 尺寸与随进度变化

`Click-To-Run/source/InfoWindow.cs:42-64`：

| 常量 | 值 | 用途 |
|---|---|---|
| `WindowWidth` | 380 | 更新/工作中 |
| `NoticeWindowWidth` | 480 | 公告类（自适应高度 120–480） |
| `WindowHeight` | 150 | 更新态**最小**高度 |
| `CompactHeight` | 104 | 紧凑态 |
| `ExtraDetailLineHeight` | 16 | 描述每多一行加高 |
| `ResizeDurationMs` | 220 | 尺寸动画时长 |

- 进度条：`Height = 6`（`:257-262`）；左侧还有个 `ProgressRing`（`:192-196`）。
- **高度随内容动画变化**：`ApplyStateSize()` 先 `_panel.Measure()` 拿 `DesiredSize`，`Resize(...)` 走 `AppWindow.MoveAndResize` 逐帧动画（220ms 贝塞尔），更新态 `minimum = WindowHeight`，其余 `CompactHeight`（`:931-938`、`:482-549`）。
- 位置：工作区右下角，`Margin` 边距（`:1418-1443`）。

### 1.5.3 百分比 / 文案 / 速度 / 剩余时间

| 项 | 事实 | 证据 |
|---|---|---|
| 进度条取值 | `percent < 0` → `IsIndeterminate = true`；否则 `Value = min(percent,100)` | `InfoWindow.cs:1019-1027` |
| **下载只占 65%** | `percent = received * 65.0 / total` | `WinUIProgram.cs:2568` |
| 文案 | 「步骤 1/2 · Dafeiyu-GO 12.4MB/230MB」；安装阶段「步骤 2/2 · 安装器 …」 | `WinUIProgram.cs:2569-2572`、`:3387,3396,3427` |
| 安装完 | 直接 `Update(..., 100)` + 「安装 Dafeiyu-GO 中 / 请不要关闭计算机」 | `WinUIProgram.cs:2606-2609` |
| 最短可见时间 | 下载完 `Thread.Sleep(2000)`，否则缓存命中时用户看不到窗 | `WinUIProgram.cs:2611-2613` |
| 速度 | `DownloadSupport.FormatSpeed`（`1.5 MB/s` / `0 KB/s`）+ `FormatSize` | `DownloadSupport.cs:429-441` |
| 剩余时间 | **没有 ETA**。全项目「剩余」只在余额/邀请有效期里出现 | `WinUIProgram.cs:6285`（余额）、`SettingsWindow.Connections.cs:538`（倒计时） |
| 安装器的进度 | 单步百分比折算成总进度 `(index + percent/100)/totalSteps*100`，且**只增不减** | `Setup/src/DshInstaller/Pages/ProgressPage.xaml.cs:448-459,745-760` |

### 1.5.4 失败态怎么呈现

- 进度窗**切成结果态**：红叉 + 标题「更新失败」+ 原因，停留 4 秒后滑出关闭（`WinUIProgram.cs:2588-2591`；结果态实现在 `InfoWindow.cs:734-768`，绿勾/红叉/黄叹号，`StartResultTimer` 3 秒自动滑出）。
- 设置窗「更新页」的结果条另有一套（见 1.8），**失败时按钮文案回到「立即检查」**，用户自己再点一次。

---

## 1.6 安装 / 替换

### 1.6.1 换掉正在运行的程序：PowerShell 延迟替换脚本

`Click-To-Run/source/UpdateSupport.cs:604-673` `ApplyUpdateAndExit()` → `StartApplyUpdate()`：

1. 写 `%TEMP%\DeepSeekHarnessUpdate\apply-update.ps1` 与 `restart-launcher.cmd`。
2. `Process.Start(powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ...)`（`:661-671`）。
3. 调用方（主进程）随后自己退出：`_launcherUpdateRestartPending = true; InvokeOnUi(ExitApplication);`（`WinUIProgram.cs:2616-2619`）。
4. 注释直接写明理由：「必须由脚本干替换的活 —— 正在运行的程序没法覆盖自己的 exe。」（`:600-603`）。

### 1.6.2 脚本做了什么（`BuildApplyScript`，`UpdateSupport.cs:707-797`）

| 步 | 动作 | 行号 |
|---|---|---|
| 1 | 等目标 PID 退出，**最长 90 秒**，每 400ms 轮询；再多等 2s | `:731-737` |
| 2 | 整目录备份到 `%TEMP%\DeepSeekHarnessBackup-<yyyyMMddHHmmss>` | `:738-740` |
| 3 | 递归 copy `new/*` → 安装目录，逐文件 try/catch 计 `$fail` | `:743-755` |
| 4 | 只有 0 失败才清理「已废弃的旧 helper 文件」（新旧对比 path 差集） | `:758-770` |
| 5 | **重启走计划任务**：`schtasks /create /rl highest /sc once /st 23:59 /it` → `/run` → 3s 后 `/delete`，再用 `start /d <dir>` 拉起带 `--no-browser --updated=<版本>` | `:779-790`、`:639-643` |
| 6 | 0 失败且 staging 不是 reparse point 才删 staging，否则保留诊断 | `:791-794` |
| 7 | 全程写 `installDir\logs\update.log`（脚本内 `W()` 函数） | `:716-724` |

两个硬约束写在注释里：
- **脚本里只用英文**：脚本是 UTF-8 无 BOM，PowerShell 5.1 会按 ANSI 读，中文字面量会读坏（`:700-706`）。给用户看的中文交给重启后的进程弹通知。
- 重启用 `schtasks /rl highest` 而不是直接 start：注释说该脚本上下文未必带管理员令牌，直接 start 会被 UAC 拦（不可见的 UAC 弹窗 = 托盘回不来）（`:772-783`）。

### 1.6.3 安装器自更新（EXE 自解压）

`Click-To-Run/source/InstallerUpdateService.cs`：
- setup EXE 是「bootstrapper + 追加的 ZIP payload（签名版后面还会追加 Authenticode 证书）」（`:28-31` 注释）。
- 流程：下载 → **先 sha256** → `FindZipStart` 定位末尾 ZIP 起点 → 抽成 `payload.zip` → 解压 → 校验 `DSH-Installer.exe` / `DSH-Uninstall.exe` 存在 → `ApplyPayload` 覆盖 `.installer` 目录（`:645-737,756-843,925-971`）。
- **安装器失败不阻断启动器更新**：先准备启动器，再准备安装器，安装器出问题只发警告（`WinUIProgram.cs:2594-2604`）。

### 1.6.4 签名校验：**没有**

- `Click-To-Run/SIGNING.md:1-12`：「现状：不签名（2026-09-27 起）。SignPath Foundation 的免费签名申请未通过。」
- `:19-23`：「当前本机包仍未签名。ZIP 和下载哈希校验确认分发内容一致，**不代表 Authenticode 签名**。」
- 代码里**没有** `WinVerifyTrust` / `CheckSignature` 之类调用；`grep Authenticode` 只命中 `InstallerUpdateService.cs:29` 的一句注释。
- 所以整条信任链 = **HTTPS + 固定仓库 URL + 清单 sha256**。清单本身没有签名，被投毒的仓库能同时改包和 hash —— 这点必须在 Predex 文档里写清（见 4）。

---

## 1.7 坑：文档里记录过的失败案例

| # | 坑 | 现象 / 根因 | 证据 |
|---|---|---|---|
| 1 | 加速前缀不能乱套 | 把前缀套在 `python.org` 上 → ghproxy 403、gh-proxy 404、其余全超时，「白等 40 秒，还把本来能直连的地址给耽误了」 | `Setup/.../LauncherFeed.cs:102-107` |
| 2 | 有的镜像不支持 Range | 实测只有 `gh-proxy.com` 回 206；`ghproxy.net` 只回 200；codeload / ghfast / 直连都回 200 | `Click-To-Run/source/AcceleratorLatency.cs:26-30`、`Setup/.../MirrorSource.cs:273-277` |
| 3 | 死源排序错误 = "下载不动" | 把 `codeload` 排到直连归档后面，虚拟机日志要白等两条死路 | `HANDOVER.md:1086,1124` |
| 4 | 单源超时太长 = 卡 10 分钟 | 1.3.21 之前连接超时 10 分钟，用户对着进度条发呆 | `CHANGELOG.md:491-496` |
| 5 | 停滞看门狗 | `Timeout` 只管握手、`ReadWriteTimeout` 只管单次读，「连得上、响应头秒回、正文不吐」的镜像两边都不触发；虚拟机实测一条候选卡了 **6 分 18 秒** | `HANDOVER.md:1092,1098` |
| 6 | 未知总长度导致假进度 | 镜像/chunked 时 `TotalLength = -1`，进度条卡在起始值而文案照跳 | `HANDOVER.md:1068` |
| 7 | 分母用错 = 假进度 | DSH 更新是"替换"不是"从零装"，早期拿总大小当分母，出现 `503.8 / 230` 两个假读数 | `HANDOVER.md:1065,1134` |
| 8 | WinUI 窗口不能在后台线程建 | 直接 new → `RPC_E_WRONG_THREAD (0x8001010E)`，进度窗永远弹不出来 | `CHANGELOG.md:502-506` |
| 9 | 进度窗能把更新拖死 | 1.3.19 一次自递归调用直接爆栈，更新还没读清单就中断；后来"窗口建不出来也照常更新" | `CHANGELOG.md:518-525`、`WinUIProgram.cs:2696-2748` |
| 10 | 文件被占用 | 替换前必须等进程退出；`Update-Dist.ps1` 直接报「dist 被文件锁占用，先在托盘 Quit」 | `Click-To-Run/source/UpdateSupport.cs:731-737`；Predex 同类：`src/Update-Dist.ps1:25-33`、`app/build-winui.ps1:48-54` |
| 11 | 杀软隔离/误报 | 火绒日志确认旧临时端口测试 DLL 被报 Backdoor/Steam；回归 DLL 被安全软件隔离，验证边界写进交接 | `HANDOVER.md:44,1713` |
| 12 | 未签名 → SmartScreen | 首次运行弹「Windows 已保护你的电脑」，文档教用户点「更多信息→仍要运行」，或按 Release 里的 SHA256 自己核 | `SIGNING.md:8-12` |
| 13 | 自签证书 / pin | 后端服务器用 SPKI pin + 自有 CA（`/api/download/ca`），**其它地址一律系统证书校验**，不接受任意自签/过期证书 | `BackendDownloadSource.cs:106-143`、`HANDOVER.md:491,513` |
| 14 | 代理绕过 | 下载引擎的 `HttpClient` 曾是 `static readonly` 建一次用到死，用户填了代理也不生效，「日志里一句都看不出来」；后来改代理自动重建 | `DownloadEngine.cs:110-149` |
| 15 | 代理范围误判 | 代理设置/在线源/地域三套判断互相污染，最后定成「看档位不看地域」，**唯一例外是 api.github.com 不带 Token 走第三方** | `HANDOVER.md:1072` |
| 16 | 清单通道静默退回 | `manifest-preview.json` 不存在时预览通道静默用正式清单，用户以为切成功 | `HANDOVER.md:1142`、`UpdateSupport.cs:63-66` |
| 17 | 302 只能拿版本拿不到 hash | `/releases/latest` 的 302 Location 里只有 tag，没有可信 hash → 只许提示重试，不许安装 | `InstallerUpdateService.cs:372-380,118` |
| 18 | GitHub 匿名额度 | 匿名 API 60 次/小时，一次全量目录约 45 次请求 → 必须缓存 | `HANDOVER.md:1067`、`InstallerUpdateService.cs:44-48`（12 小时缓存） |
| 19 | PS 脚本中文被读坏 | 无 BOM UTF-8 脚本被 PS 5.1 按 ANSI 读，中文字面量直接坏 | `UpdateSupport.cs:700-706` |
| 20 | 同版本重复下载/共享暂存互删 | 修复"更新共享暂存互删、同版本 DSH 下载覆盖、分片初始化异常留下孤儿下载线程" | `HANDOVER.md:837` |

---

## 1.8 设置窗「更新页」的状态机与文案

### 1.8.1 状态模型

`Click-To-Run/source/UpdateUiSnapshot.cs:3-23`：

```csharp
internal enum UpdateUiActivity { Idle, Checking, Installing, UpToDate, Available, Completed, Failed }

internal sealed class UpdateUiSnapshot {
    public UpdateUiActivity Activity { get; set; } = UpdateUiActivity.Idle;
    public string Version { get; set; } = string.Empty;
    public string ProgressText { get; set; } = string.Empty;   // "下载中 12.4 MB / 230 MB"
    public string Detail { get; set; } = string.Empty;         // "请不要关闭软件" / 失败原因
    public double Progress { get; set; }
    public bool IsIndeterminate { get; set; }
}
```

**启动器 / DSH / 安装器 / 插件 四张卡共用这一个结构**（`SettingsWindow.xaml.cs:2457-2477` 把它们凑成数组判 busy）。

### 1.8.2 状态 → UI 映射（`SettingsWindow.xaml.cs:2584-2672` `ApplyUpdateState`）

| 状态 | 按钮文案 | 按钮可用 | 进度区 | 结果条 |
|---|---|---|---|---|
| Idle | 立即检查 | ✅ | 隐藏 | 关闭 |
| Checking | **检测更新中** | ❌ | 显示，`IsIndeterminate = true` | 关闭 |
| Installing | **更新中** | ❌ | 显示，`Value = Progress`，文案 `ProgressText · Detail`（相同则去重） | 关闭 |
| UpToDate | 立即检查 | ✅ | 隐藏 | 成功「已是新版本」+ 版本/通道 |
| Available | **立即更新** | ✅ | 隐藏 | 成功「发现新版本」+ 版本/通道 |
| Completed | 立即检查 | ✅ | 隐藏 | 成功「操作完成」+ Detail |
| Failed | 立即检查 | ✅ | 隐藏 | **Error**「检查或更新失败」+ Detail |

要点：
- **没有独立的"重试"按钮**：失败后主按钮文案回到「立即检查」，点它即重试（`:2662-2666`）。插件/技能卡片另有「重试」小按钮（`:4087-4094`、`:9788-9802`），文案统一成「<原因>（可点卡片上的「重试」再试一次）」。
- 进度区存在时不显示结果条，避免两个 InfoBar 打架（`:2601-2607`）。
- 预览模式可以钉状态目检：`--settings-preview=Updates:Installing` 等 6 个相位（`:2479-2582`）。

### 1.8.3 页面结构（`Click-To-Run/source/SettingsWindow.xaml`）

| 区域 | 控件 | 行号 |
|---|---|---|
| 导航项 | `UpdatesNavItem` | `:868` |
| 更新页容器 | `UpdatesPage`（内含 `Pivot UpdatesTabs`） | `:4008-4009` |
| 检查频率卡 | `UpdateIntervalComboBox` | `:4030` |
| 启动器卡 | `LauncherUpdateSummaryText` / `LauncherUpdateModeComboBox`（Install 自动更新·Check 仅检查） / `LauncherUpdateResultInfoBar` / `CheckLauncherUpdateButton` / `LauncherUpdateProgressPanel + Text + Bar` | `:4118-4157` |
| DSH 卡 | `DshUpdateSummaryText` / `DshUpdateModeComboBox` / `DshUpdateResultInfoBar` / `CheckDshUpdateButton` / `DshUpdateProgressPanel…` | `:4181-4227` |
| 安装器卡 | `InstallerUpdateModeComboBox` / `CheckInstallerUpdateButton` / `InstallerUpdateProgressPanel + Text + Bar` | `:4432-4453` |
| 在线引擎（源选择） | `UpdateSourceComboBox`（大陆 CDN 加速 / 后端服务器加速 / 自定义 / 官方源） | `:1868-1885` |
| 高级设置（钉具体源） | `AcceleratorSourceSelector` RadioButtons（自动 / ghproxy.net / gh-proxy.com / ghfast.top / jsDelivr） | `:1896-1904+` |

### 1.8.4 入口与自动检查

- 设置窗导航「更新」+ 主页卡片上的「检查更新」（`:1157`）。
- 启动期：`Defer("检查启动器更新", ...)` 一组延迟任务（`WinUIProgram.cs:1938-1945`），受 `UpdateInterval` + `LastUpdateCheckUtc` 控制（`:2409-2422`），成功/失败结果写进启动摘要（`:2056-2078`）。
- 手动点检查：`_manualUpdateWindowRequested` 让进度窗可以抢过"服务提示"占用（`WinUIProgram.cs:3064-3071`）。

---

# 2. 给 Predex 的设计方案

> 前提事实（现状）：Predex 是**单窗口 + 托盘**的 WinUI 3 应用，窗口是 320 DIP 宽的右下角面板（`Shell/ShellWindow.xaml.cs:63-71,304-315`）；**已提权**（`app.manifest:7` `requireAdministrator`）；`Version 3.0.8`（`Predex.WinUI.csproj:23-25`）；已有 `UpdateCheckService` 但**只查 GitHub API 然后开浏览器**（`Services/UpdateCheckService.cs:21-24,70-80`），比较用 `Version.TryParse`（`:54-57`，**`0.1.0-alpha.1` 会直接判失败**）；`InfoWindowService` 已被降级成 InfoBar toast（`Compat/InfoWindowServiceCompat.cs:1-18`）；OSD 浮层是「窗口=卡片」48 DIP 高、底部居中、惰性创建复用（`Views/ModeOsdWindow.xaml:12-26`、`.xaml.cs:72-96`）。

## 2.1 源列表与顺序

### 2.1.1 清单源（谁来告诉我们"有新版"）

| 优先级 | 源 | 地址（占位） | 我们能控制？ | 备注 |
|---|---|---|---|---|
| 组 1（并行 3 条） | jsDelivr CDN | `https://cdn.jsdelivr.net/gh/<OWNER>/Predex@main/manifest.json` | ❌ 第三方 | 国内部分网络超时，只做"可能很快"的候选 |
| 组 1 | gh-proxy | `https://gh-proxy.com/https://raw.githubusercontent.com/<OWNER>/Predex/main/manifest.json` | ❌ | 实测唯一稳定回 206 的 |
| 组 1 | ghproxy | `https://ghproxy.net/https://raw.githubusercontent.com/<OWNER>/Predex/main/manifest.json` | ❌ | |
| 组 2 | raw 直连 | `https://raw.githubusercontent.com/<OWNER>/Predex/main/manifest.json` | ❌ | 海外/企业网常直连更快 |
| 组 3 | GitHub API | `https://api.github.com/repos/<OWNER>/Predex/releases/latest` | ❌ | 只能兜底；**没有 digest 就不许装** |

- **组的语义照抄**：组内并行（≤6 条）、组间串行、每组 4s、总 8s（`UpdateMetadataReader.cs:15-18,53-98`）。
- `<OWNER>/Predex` 是**必须由你后续填**的占位：现在代码里指的是上游 `hammadzaigham/PreySense`（`Services/UpdateCheckService.cs:21-24`），那不是我们的发布通道。

### 2.1.2 安装包源（谁来给字节）

| 顺序 | 源 | 说明 |
|---|---|---|
| 1 | `gh-proxy.com` 包前缀 | **唯一实测支持 Range 的**，优先让它吃分片 |
| 2 | `ghproxy.net` 包前缀 | 只回 200 → 只能单连接整文件 |
| 3 | `objects.githubusercontent.com` 直连 | Release 资产真身，海外/公司网可用 |
| 4 | jsDelivr | **不要用于 zip 资产**（只服务仓库内文件、且不稳定）；只用于清单 |

排序策略（建议与 Dafeiyu 一致）：

- 默认「自动」= 启动时测一轮延迟，按实测升序；没测到的排后面（`GitHubAccelerator.cs:161-205`、`AcceleratorLatency.cs:132-197`）。
- 用户可钉死某一个源（设置里「高级设置」→ 单选），钉了就不许自动重排（`GitHubAccelerator.cs:139-159`）。
- 提供「官方直连」档：只给原始 GitHub 地址，一个前缀都不套（`UpdateSupport.cs:176-177,185-189`）。

**明确不做的源**：`ghfast.top`（实测全超时，Dafeiyu 自己也从安装器池里删了）、自建 CDN、GitCode（参考实现里不存在）。

## 2.2 清单字段设计

### 2.2.1 协议入口与归档

```
Predex/                                   ← 发布仓库（新建，占位）
├─ manifest.json                          ← stable 协议入口（不许改名、不许移动）
├─ manifest-beta.json                     ← beta 通道
├─ manifest-alpha.json                    ← alpha 通道
└─ manifests/
   ├─ 0.1.0-alpha.1.json                  ← 历史归档，按原字节保留
   └─ 0.2.0.json
```

理由抄自 `Click-To-Run/manifests/README.md:3-7`：协议入口固定 → 老客户端永远能升级；归档不覆盖 → 可审计。

### 2.2.2 JSON 示例（`manifest-alpha.json`，对应我们要发的 `0.1.0-alpha.1`）

```json
{
  "schema": 1,
  "channel": "alpha",
  "prerelease": true,
  "version": "0.1.0-alpha.1",
  "publishedAt": "2026-11-13T06:00:00Z",
  "notes": "首个 WinUI 预览版：托盘、OSD、风扇曲线。",
  "releaseUrl": "https://github.com/<OWNER>/Predex/releases/tag/v0.1.0-alpha.1",
  "assets": [
    {
      "platform": "win-x64",
      "fileName": "Predex-0.1.0-alpha.1-win-x64.zip",
      "sizeBytes": 86432177,
      "sha256": "9f2c0b8e4d5a6f718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9",
      "url": "https://github.com/<OWNER>/Predex/releases/download/v0.1.0-alpha.1/Predex-0.1.0-alpha.1-win-x64.zip",
      "mirrors": [
        "https://gh-proxy.com/https://github.com/<OWNER>/Predex/releases/download/v0.1.0-alpha.1/Predex-0.1.0-alpha.1-win-x64.zip",
        "https://ghproxy.net/https://github.com/<OWNER>/Predex/releases/download/v0.1.0-alpha.1/Predex-0.1.0-alpha.1-win-x64.zip"
      ]
    }
  ]
}
```

### 2.2.3 字段规则表

| 字段 | 必填 | 规则（不满足 = 整份清单作废） | 抄自 |
|---|---|---|---|
| `schema` | ✅ | 当前只接受 `1` | 新增（Dafeiyu 没有，靠宽松正则兼容，我们不这么干） |
| `channel` | ✅ | `stable` / `beta` / `alpha`，必须与用户设置**严格相等** | 2.1.3 通道 |
| `prerelease` | ✅ | 与 `version` 是否带 `-` **必须一致**（防"标 stable 却发 alpha"） | 新增 |
| `version` | ✅ | SemVer 2.0 子集，允许 `0.1.0-alpha.1`；build metadata 忽略 | `ProductVersion.cs:95-185` |
| `publishedAt` | ✅ | ISO-8601 UTC，仅展示 | Dafeiyu 的 `releasedAt` |
| `notes` | ✅ | 纯文本/Markdown，展示用 | `manifest.json` |
| `releaseUrl` | ⬜ | 只在"打开 README"时用 | 新增 |
| `assets[]` | ✅ | 至少 1 条 `platform == "win-x64"` | `DshBundleDeployment.cs:209-215` |
| `assets[].sizeBytes` | ✅ | `1 ≤ size ≤ 1 GiB`，**先比大小再比哈希** | `DshBundleDeployment.cs:211,220` |
| `assets[].sha256` | ✅ | 64 hex；缺失**直接拒绝下载** | `UpdateSupport.cs:336-340,513-514` |
| `assets[].fileName` | ✅ | 只能是文件名（不许带路径/非法字符），`.zip` 结尾 | `DshBundleDeployment.cs:212-213` |
| `assets[].url` / `mirrors[]` | ✅ | 只接受 `https`，且 host ∈ 白名单（`github.com` / `*.githubusercontent.com` / 我们自己的域名） | `UpdateSupport.cs:185-189` |
| 刻意**不加** | — | `mandatory` / `force` / 灰度百分比 / 增量补丁 / `subDirectory`（我们用固定放置结构） | 见 4 |

> 与 Dafeiyu 的差别：它用正则抠字段（`UpdateSupport.cs:284-295`），我们改用 `JsonSerializer` + `[JsonRequired]`，避免 `assets` 多一层嵌套就解析漂移。**清单要能被单测 dump 出来的 JSON 逐项反向验证。**

## 2.3 版本通道（stable / beta / alpha）

### 2.3.1 三个通道

| 通道 | 清单文件 | 版本形态 | 谁能看到 | 我们当前要发的 |
|---|---|---|---|---|
| stable | `manifest.json` | `0.1.0`（无预发布） | 默认 | 之后 |
| beta | `manifest-beta.json` | `0.2.0-beta.1` | 用户手动选 | 可选 |
| alpha | `manifest-alpha.json` | **`0.1.0-alpha.1`** | 用户手动选（默认隐藏在产品设置里） | **首发就用这个** |

### 2.3.2 版本判定表（决定"要不要提示/能不能装"）

| 本机 | 通道 | 远端 | 动作 |
|---|---|---|---|
| `0.1.0-alpha.1` | alpha | `0.1.0-alpha.2` | 提示更新 |
| `0.1.0-alpha.1` | alpha | `0.1.0` | 提示更新（release 压过自己的预发布，抄 `UpdateIntegrity.Regression/Program.cs:139`） |
| `0.1.0` | stable | `0.1.0` | 已是最新 |
| `0.1.0` | stable | `0.1.1-alpha.1` | **通道不匹配 → 忽略**（不许 stable 用户被预发布顶上去） |
| `0.2.0`（本地更新） | alpha | `0.1.0-alpha.5` | 已是最新（**绝不降级**，抄 `:137-138`） |
| 任意 | 任意 | 清单 version 解析失败 | 失败态「更新源返回的版本号无法识别」+ 保留当前安装 |

### 2.3.3 通道默认值与切换

- 默认 `alpha`（首发阶段），进入正式发布后默认改 `stable`，并在设置里显式标注「内测通道，可能崩」。
- 通道切换**只影响下一次检查**，不触发立即下载。
- **不做强制更新**：参考实现没有，我们也不加（见 4）。

## 2.4 下载 / 校验 / 替换流程

### 2.4.1 主流程（从点击到重启）

```
[0] 触发：启动期（受检查频率控制）/ 用户点「检查更新」/ 托盘菜单
      ↓
[1] 选清单源 → 组内并行 ≤6、组间串行、每组 4s、总 8s      ← 1.1.3 / 1.1.4
      ↓ 失败 → 失败态「更新源暂不可用：<原因>」，写日志
[2] 解析 + 逐字段校验（2.2.3 全部规则）                     → 缺 sha256/size 立即失败
      ↓
[3] 版本判定（2.3.2）→ 不新 → 「已是最新 vX · <通道>」，结束
      ↓
[4] 用户确认（对话框：「发现 v0.1.0-alpha.2（86.4 MB），现在更新？」）  ← 不做静默
      ↓ 取消 → Idle
[5] 预检：磁盘可用 ≥ sizeBytes*2.2；%TEMP% 可写；安装目录可写；
      除本进程外没有别的 Predex.exe 在跑（否则提示先退出另一份）
      ↓ 失败 → 失败态（可修复的原因直接写出来）
[6] staging：%TEMP%\PredexUpdate\<GUID>\  → package.download
      ↓ 多源轮换 + .part 续传 + 20s 停滞看门狗（1.4.3）
[7] 落盘长度校验 == sizeBytes
      ↓ 不等 → 删文件、换下一个源重试（最多 3 轮）
[8] SHA-256 校验（对 zip）
      ↓ 不等 → 删文件、换源重试；3 轮全失败 → 失败态「校验失败」
[9] 解压到 staging\new\ + 断言 Predex.exe / resources.pri 存在
      ↓ 缺 → 失败态「更新包结构不对」
[10] 写 staging\update.json（版本、来源 URL、哈希、时间、日志路径）
[11] 拉起 apply-update.ps1（隐藏窗口）→ 本进程退出
[12] 脚本：等 PID 退出（≤90s）→ 备份 dist → 逐文件覆盖 → 写日志
      ↓ 有失败 → 保留 staging + 备份，不重启，写 update.log
[13] 重启 Predex.exe --updated=0.1.0-alpha.2
[14] 新进程：托盘气泡「Predex 已更新到 v0.1.0-alpha.2」+ 更新进度浮窗切成成功态
```

### 2.4.2 失败分支表

| 阶段 | 失败原因 | 用户看到 | 自动动作 | 数据处置 |
|---|---|---|---|---|
| 拉清单 | 全源超时 | 「更新检查超时，请稍后重试或切换源」 | 无 | 无 |
| 拉清单 | 有响应但 JSON 非法 | 「更新源返回的数据无法识别」+ 日志 | 换下一组 | 无 |
| 字段 | 缺 `sha256`/`sizeBytes` | 「更新清单不完整（缺少校验值），已停止」 | **不下载** | 无 |
| 版本 | 解析失败 | 「更新源返回的版本号无法识别」 | 无 | 无 |
| 下载 | 单源失败 | 进度浮窗文案显示「切换下载源…」 | 换源，最多 3 轮 | 删 `.part` |
| 下载 | 长度不符/停滞 | 「下载中断，正在重试（2/3）」 | 续传 or 换源 | 保留 `.part` |
| 校验 | SHA-256 不符 | 「安装包校验失败，已丢弃」 | 删文件 + 换源重试 | 删包 |
| 解压 | 缺主程序 / zip 损坏 | 「更新包结构不对，已停止」 | 不清 staging（诊断） | 保留 |
| 替换 | 进程 90s 未退出 | 日志「waiting for pid … to exit」超时后仍尝试 | 继续尝试 | 备份已在 |
| 替换 | 复制失败 | 重启后浮窗/托盘「更新未完成，已回滚」 | 用备份恢复 | 保留 staging |
| 重启 | 启动失败 | 托盘「更新后启动失败」+ 日志路径 | 提示手动运行 `dist-prev` 备份 | 备份 |

### 2.4.3 放置结构与"什么算被替换"

| 项 | 值 |
|---|---|
| 安装根 | `%LOCALAPPDATA%\Predex\app\`（占位；或安装目录，最终由发布形态定） |
| 用户数据 | **绝不在更新范围内**：`preysense.json`、profile、日志、`dist` 之外的一切 |
| 更新范围 | zip 里有什么就覆盖什么；**只增不删**（先照抄 Dafeiyu 的「复制成功才清理废弃 helper」策略） |
| 保留白名单 | `preysense.json`、`logs\`、`backups\`（脚本里显式跳过） |
| 备份 | `%TEMP%\PredexBackup-<yyyyMMddHHmmss>\` 整目录一份（抄 `UpdateSupport.cs:738-740`） |
| 锁 | 单一互斥体 `Predex.Update`，第二个实例只提示"正在更新" |

## 2.5 进度浮窗的形态（建议）

### 2.5.1 结论

**做独立的小浮窗 `UpdateFloatWindow`，不复用 `ModeOsdWindow`，也不塞进 320 DIP 的 shell。**

理由（每条都能对上现状）：

| 维度 | ModeOsdWindow 现状 | 更新进度需要 | 结论 |
|---|---|---|---|
| 高度 | 固定 **48 DIP** 卡片（`ModeOsdWindow.xaml.cs:74`） | 标题 + 2~3 行文案 + 进度条 + 失败原因 | ❌ 复用 |
| 进度条 | 无 | 需要（6 DIP 高细条，照抄 `InfoWindow.cs:257-262`） | ❌ |
| 生命周期 | 3s 自走 + hover 暂停（`:34-36`） | 一直挂到用户看到结果，失败态不许自动消失 | ❌ |
| 宽度 | 160–280 DIP 内容驱动（`:75-77`） | 需要稳定宽度容纳「12.4 MB / 86.4 MB · 3.1 MB/s」 | ❌ |
| 交互 | 不抢焦点、不可点（`:24-25`） | 失败态要有「重试 / 打开日志」按钮 | ❌ |

但**机制**照抄 OSD，这几点是 OSD 踩过坑换来的：

| 机制 | 具体 | 证据 |
|---|---|---|
| 窗口 = 卡片 | WinUI 3 顶层窗口不能逐像素透明，窗口比卡片大就会露出黑板 | `ModeOsdWindow.xaml:12-26` |
| 单 HWND 复用 | 首次显示创建，之后只换内容、重放动画 | `ModeOsdWindow.xaml.cs:28-33` |
| 帧驱动位移 | `CompositionTarget.Rendering` + Stopwatch 真时长插值 + 16ms 兜底定时器（帧计数驱动实测 490ms 偏差失败） | `:13-20,99` |
| 分层 alpha 淡入淡出 | `WS_EX_LAYERED + LWA_ALPHA`，不能动卡片的 `Opacity` | `:18-22` |
| 窗口样式 | 无边框、`WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW`、置顶、不进任务栏/Alt+Tab | `:24-25` |
| DWM 圆角 | `DWMWCP_ROUND` + 自己 8 DIP 内圆角 | `:11-14`（Predex 已有 `Predex.WinUI` 的 DWM 常量，见 3） |
| 降级 | `UISettings.AnimationsEnabled == false` → 不位移、只 83ms 淡入 | `:37-38` |
| 位置 | 主屏工作区**底部居中**，卡片底边距工作区底 24 DIP | `:26-27,82-83` |

### 2.5.2 建议规格

| 项 | 建议值 | 参照 |
|---|---|---|
| 宽 × 高 | 380 × 148 DIP（紧凑 380 × 104；失败态 380 × 176） | `InfoWindow.cs:42-52` |
| 位置 | 底部居中，距工作区底 24 DIP（**与 OSD 同底边**，视觉统一） | `ModeOsdWindow.xaml.cs:82-83` |
| 圆角 | 8 DIP（Win11 风格）；Win10 风格 → 0 | 3.2 节 |
| 材质 | 跟随全局（Mica/Acrylic/纯色），高对比度 → 纯色 | 3.2 节 |
| 进度条 | 6 DIP，`IsIndeterminate = percent < 0` | `InfoWindow.cs:257-262,1019-1027` |
| 标题 | 「更新 Predex」/ 失败「更新失败」 | 1.5.3 |
| 文案行 1 | `步骤 1/3 · 下载 12.4 / 86.4 MB · 3.1 MB/s` | `WinUIProgram.cs:2569-2572` + `FormatSpeed` |
| 文案行 2 | `步骤 2/3 · 校验中…` / `步骤 3/3 · 安装中，请不要关闭计算机` | `:2606-2609` |
| 百分比 | 见 2.5.3 权重表 | `:2568` |
| 动画 | 进场 250ms / 淡出 167ms / 尺寸变化 220ms，cubic-bezier | `InfoWindow.cs:64`、`ModeOsdWindow.xaml.cs:90-106` |
| 最短停留 | 下载完至少 2s 再跳安装态 | `WinUIProgram.cs:2611-2613` |
| 失败态 | 红叉 + 原因（≤80 字）+「重试」「打开日志」两个按钮，**不自动消失** | 参照 `InfoWindow.cs:734-768` 结果态但去掉自动滑出 |

### 2.5.3 百分比权重（单条进度条，三段合成）

| 段 | 区间 | 依据 |
|---|---|---|
| 下载 | 0 – 70% | `received/total * 70`；总长未知时用"已收字节 + 不定态环"（`DownloadSupport.cs:13-24` 的 `-1` 语义） |
| 校验 | 70 – 80% | 大包 sha256 要 1~3s，给 10% 免得条子卡住 |
| 解压 + 等待替换 | 80 – 100% | 解压按文件数累计；写脚本后直接 100% + 「即将重启」 |

### 2.5.4 为什么**不**照抄独立 info-host 进程

- Dafeiyu 需要它是因为：进度窗要活过"启动器退出 → 脚本替换 → 新实例启动"这段无人区，而且主进程有多个窗口/多次重启（`InfoWindowClient.cs:101-145`、`UpdateSupport.cs:631-637` 的 `--info-session`）。
- Predex 的实际时间线：写脚本 → 退出（<1s）→ 脚本等 0.5s + 备份 2~5s + 复制 2~5s → 重启。**窗口消失约 5~12 秒**。
- 拿到的东西：一次性的 12 秒空窗；付出的东西：一个 helper 工程、一套命名管道协议、缓存目录拷贝、孤儿进程清理、主题透传（`InfoHost/Program.cs:14-16,27-28,67-72` 全要跟着改）。
- **判定：先不做。** 用「替换阶段浮窗关闭 → 重启后新进程用 `--updated=` 参数弹托盘气泡 + 浮窗成功态」覆盖，成本 1/10。若日后更新包变大/用户抱怨黑屏断档，再升级成 helper。

## 2.6 需要新增 / 修改的文件清单（只写方案，不动代码）

### 2.6.1 新增（全部在 `PredatorSense/app/experiments/Predex.WinUI/`）

| 文件 | 职责 | 关键点 |
|---|---|---|
| `Services/Update/SemanticVersion.cs` | 版本解析与比较 | 照 `ProductVersion.cs:95-246` 语义；支持 `0.1.0-alpha.1`、2~4 段、build metadata 忽略；`IsValid` 区分垃圾 |
| `Services/Update/UpdateManifest.cs` | 清单模型 + `JsonSerializer` 解析 | `schema/channel/prerelease/version/publishedAt/notes/assets[]`；解析失败给中文原因 |
| `Services/Update/ManifestSources.cs` | 组装清单候选组 | 组内并行 6、组间串行、每组 4s/总 8s；`<OWNER>/Predex` 占位常量 |
| `Services/Update/MirrorSelector.cs` | 测速/排序/钉源 | 照 `GitHubAccelerator.cs:133-206`；结果落盘 `%LOCALAPPDATA%\Predex\AcceleratorProbe.json` |
| `Services/Update/UpdateDownloader.cs` | 下载 + 续传 + 停滞看门狗 + 进度回调 | `.part` 续传、`Range` 能力探测、20s 停滞、`If-Range`、长度先行校验 |
| `Services/Update/PackageVerifier.cs` | 大小 + SHA-256 + zip 结构 | 先 size 后 hash（`DshBundleDeployment.cs:218-225`）；失败抛可重试/不可重试两类异常 |
| `Services/Update/UpdateInstaller.cs` | staging 管理 + 写脚本 + 启动脚本 | `%TEMP%\PredexUpdate\<GUID>`；脚本**纯英文**写入；调用后要求 app 退出 |
| `Services/Update/UpdateState.cs` | 状态机 | `enum { Idle, Checking, Available, Downloading, Verifying, Installing, Completed, Failed }` |
| `Controls/UpdateFloatWindow.xaml(.cs)` | 进度浮窗 | 380×148、卡片即窗口、单 HWND、分层 alpha、帧驱动位移（抄 OSD 机制） |
| `Services/Appearance/WindowMaterialService.cs` | 材质 + 窗口风格 + DWM 调用 | 见 3.2 |
| `Pages/UpdatesPage.xaml(.cs)` 或 `Pages/SettingsPage` 内新分组 | 更新页 | 见 2.7 |

### 2.6.2 修改

| 文件 | 改什么 |
|---|---|
| `Predex.WinUI.csproj` | `Version` / `AssemblyVersion` → 由构建注入（`-p:Version=0.1.0-alpha.1`），保证 exe 版本号与清单一致 |
| `Services/UpdateCheckService.cs` | 彻底换掉：不再 `Version.TryParse`（`:54-57`）、不再指向上游仓库（`:21-24`）、不再只开浏览器（`:70-80`） |
| `Shell/ShellWindow.xaml(.cs)` | 首页加更新卡（`UpdateCheckService.DisplayVersion` 已经在用，`:89`）；托盘菜单加「检查更新」；接 `--updated=` 参数 |
| `Pages/SettingsPage.xaml(.cs)` | 新增「更新」分组（当前仅 General 分组，`:161-188` 是主题那块） |
| `App.xaml.cs` | 启动期检查（`UpdateInterval` + `LastUpdateCheckUtc`）、`--updated=` 解析、`--no-update` 之类的测试开关（现有 `--demo=osd` 同款做法） |
| `Services/TrayIconService.cs` | 更新结果气泡、「有新版」角标 |

## 2.7 更新页（Predex 版）

| 区块 | 控件 | 文案 / 行为（照抄 Dafeiyu 的措辞风格） |
|---|---|---|
| 当前版本 | TextBlock | `v0.1.0-alpha.1 · alpha 通道` |
| 通道 | ComboBox | `稳定版 / 内测版 / 预览版`；切换后「下次检查生效」 |
| 检查频率 | ComboBox | `每次启动 / 每 3 天 / 每 7 天 / 每月 / 关闭`（`LauncherSettings.cs:45`） |
| 自动更新 | ComboBox | `自动下载安装 / 仅提示 / 关闭`（`LauncherSettings.cs:33`） |
| 主按钮 | Button | Idle「立即检查」→ Checking「检测更新中」(禁用) → Available「立即更新」→ Downloading「更新中」(禁用) → Failed「立即检查」 |
| 进度区 | StackPanel + ProgressBar | busy 时显示，文案 = `ProgressText · Detail`，重复则去重（`SettingsWindow.xaml.cs:2622-2641`） |
| 结果条 | InfoBar | UpToDate/Available/Completed = Success，Failed = **Error**；标题「检查或更新失败」 |
| 详情 | 可展开 | 更新日志 `notes` + 「打开日志文件夹」 |
| 镜像源 | ComboBox + 高级 | `自动（推荐）/ gh-proxy.com / ghproxy.net / 官方直连`；高级里显示各源延迟（`SettingsWindow.xaml:1868-1904`） |

**页面不含**：强制更新开关、灰度、跨通道降级、"修复安装"（Predex 没有安装器概念）。

---

# 3. 主题页（外观 / 材质 / 圆角 / 主题色）

## 3.1 参考实现事实

### 3.1.1 外观项清单（`Click-To-Run/source/SettingsWindow.xaml:2236-2384`）

| 外观项 | 可选值 | 控件 | 存储字段 | 行号 |
|---|---|---|---|---|
| 主题 | 浅色 / 深色 / 跟随系统 | `ComboBox ThemeComboBox`（Tag `Light/Dark/System`） | `LauncherSettings.Theme`（默认 System） | XAML `:2257-2266` |
| 窗口风格 | **Windows 10（直角）/ Windows 11（圆角）** | `ComboBox WindowStyleComboBox`（Tag `Windows10/Windows11`） | `LauncherSettings.WindowStyle` | `:2286-2294` |
| 主题色来源 | 跟随 Windows / 自选 | `ComboBox AccentSourceComboBox` + `Button`→Flyout→`ColorPicker` | `AccentSource` + `AccentColor`（默认 `#0A84FF`） | `:2319-2349` |
| 材质 | 云母 / 云母 Alt / 轻薄亚克力 / 标准亚克力 / **纯色** | `ComboBox MaterialComboBox`（Tag `Mica/MicaAlt/AcrylicThin/AcrylicBase/Solid`） | `LauncherSettings.Material`（默认 Mica） | `:2370-2381` |

存储与归一化：`Click-To-Run/source/LauncherSettings.cs:19-21`（字段与默认值）、`LauncherSettingsStore.cs:516-528`（`NormalizeChoice` 白名单，非法值落回默认）。备份分组里也带这几项：`Backup/DymLauncherBackupContext.cs:17,33`（颜色还要 `^#[0-9a-fA-F]{6}$` 校验，`:151`）。

### 3.1.2 主题：怎么落到资源上

`SettingsWindow.xaml.cs:6230-6256`：

```csharp
private void ThemeComboBox_SelectionChanged(...) {
    if (_initializing) return;
    _settings.Theme = GetSelectedTag(ThemeComboBox, "System");
    SaveSettings(); ApplyTheme();
}
private void ApplyTheme() {
    ElementTheme elementTheme = _settings.Theme switch {
        "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    LauncherAppearance.SetTheme(elementTheme);   // ← 遍历所有已注册窗口
    ApplyTitleBarButtonColors();                 // ← 标题栏按钮前景/背景也跟着换
}
```

`Click-To-Run/source/LauncherAppearance.cs:119-124,175-191`：`SetTheme` 只改静态字段再 `ApplyAll()`；`Apply()` 对每个已注册窗口做 `root.RequestedTheme = _theme` + `surface.RequestedTheme = _theme`。**所有窗口共享一份外观状态**，注释 `:24-31` 明说「设置窗改一次，托盘菜单、更新进度窗、通知窗全部跟着改，不要各存一份」。
标题栏配色：`SettingsWindow.xaml.cs:6355-6378`（`AppWindowTitleBar` 的 Button 前景/悬浮/按下/非激活四色，深浅两套）；`IsDarkTheme()` 在 `System` 时用 `UISettings.GetColorValue(UIColorType.Background).R < 128` 判定（`:6380-6394`，与 `LauncherAppearance.cs:366-386` 同口径）。

### 3.1.3 窗口风格（Win10 直角 / Win11 圆角）：资源字典 + DWM 两个口子

`Click-To-Run/source/CornerRadiusHelper.cs` 是全部逻辑：

| 项 | 事实 | 行号 |
|---|---|---|
| 风格常量 | `Windows10` / `Windows11` | `:9-10` |
| 默认值 | 按 OS build 判：`>= 22000` → Win11 风格，否则 Win10 | `:11,46-51` |
| 圆角表 | Control 4 / Card 6 / Surface 8 / Badge 4 / SearchShell 17；Win10 全部 0 | `:63-86` |
| 资源键 | `ControlCornerRadius` / `OverlayCornerRadius` / `SettingsSearchShellCornerRadius` | `:140-151` |
| 切风格 | `SetWindowStyle()`：写字段 → `ApplyApplicationResources(Application.Current.Resources)` → `LauncherAppearance.ApplyWindowFrames()` → 抛 `WindowStyleChanged` | `:103-115` |
| DWM 调用 | `ApplyWindowFrame(hwnd)`：`DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE=33, DWMWCP_ROUND=2 | DWMWCP_DONOTROUND=1, 4)` | `:12-21,117-138` |

**资源刷新有一个必须抄的细节**（`SettingsWindow.xaml.cs:6273-6285`）：只改资源字典**不会让已解析的 `ThemeResource` 表达式失效**，所以它当场把 `RequestedTheme` 翻到反色再翻回来，强制子树重解析，而不是重建 HWND：

```csharp
CornerRadiusHelper.SetWindowStyle(_settings.WindowStyle);
CornerRadiusHelper.ApplyApplicationResources(SettingsRoot.Resources);
ElementTheme requested = SettingsRoot.RequestedTheme;
SettingsRoot.RequestedTheme = SettingsRoot.ActualTheme == ElementTheme.Dark
    ? ElementTheme.Light : ElementTheme.Dark;
SettingsRoot.RequestedTheme = requested;
```

信息窗/浮窗侧还有另一组 DWM 调用（`Click-To-Run/source/InfoWindow.cs:1309-1322`）：`DWMWA_WINDOW_CORNER_PREFERENCE`（`UsesWindows11Style ? DWMWCP_ROUND : 1`）+ `DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE`。启动器主窗/托盘菜单同样（`WinUIProgram.cs:8491-8509`、`:8755-8770`，常量 `:9551-9555`）。

### 3.1.4 材质：Mica / 亚克力 / 纯色

`Click-To-Run/source/LauncherAppearance.cs`：

| 情况 | 行为 | 行号 |
|---|---|---|
| 枚举 | `Mica / MicaAlt / AcrylicThin / AcrylicBase / Solid` | `:15-22` |
| 纯色 或 高对比度 | 不挂任何 backdrop，直接给 `BackgroundSetter` 一个纯色：深色 `#202020`、浅色 `#F3F3F3`；高对比度用系统 `UIColorType.Background` | `:205-212,291-315` |
| Mica | `MicaController.IsSupported()` 才挂；`MicaKind.Base / BaseAlt`，已存在就只改 `Kind` 不重建 | `:220-246` |
| 亚克力 | `DesktopAcrylicController.IsSupported()`；`DesktopAcrylicKind.Thin / Base` | `:254-274` |
| 透明刷子 | Mica/Acrylic 时窗口内容背景设 `Transparent`，否则会盖住 backdrop | `:317-324`、`:202-203` 先清 `SystemBackdrop` |
| 销毁 | 逐个 `RemoveAllSystemBackdropTargets()` + `Dispose()`；关机路径走 `BeginShutdown` 防止遍历已释放对象 | `:326-364,113-117` |
| 失效兜底 | 任何异常 → `DisposeControllers` + `ApplySolid` | `:276-289` |
| 高对比度 | `AccessibilitySettings.HighContrast` | `:388-398` |

### 3.1.5 主题色：写到资源键，跟随系统时**移除**该键

`SettingsWindow.xaml.cs:6317-6337`：

```csharp
private void ApplyAccent() {
    bool custom = GetSelectedTag(AccentSourceComboBox, "System") == "Custom";
    CustomAccentButton.Visibility = custom ? Visible : Collapsed;
    if (!custom) {
        SettingsRoot.Resources.Remove("AccentFillColorDefaultBrush");   // ← 交回系统
        FeedbackAccentChanged(); return;
    }
    Windows.UI.Color color = AccentColorPicker.Color;
    SettingsRoot.Resources["AccentFillColorDefaultBrush"] = new SolidColorBrush(color);
    AccentColorSwatch.Background = new SolidColorBrush(color);
    FeedbackAccentChanged();
}
```

要点：**不自己画 accent 体系**，只覆盖 WinUI 已有的 `AccentFillColorDefaultBrush` 键；选「跟随系统」就把覆盖删掉让 Fluent 自己取。颜色变化实时（`AccentColorPicker_ColorChanged`，`:6302-6315`，且色板 Border 同步）。

### 3.1.6 外观怎么传到"另一个进程的窗口"

信息窗是独立进程，所以主题/材质/风格是**当参数传过去**的：`InfoHost/Program.cs:26-28` 读 `--theme/--material/--window-style`，`:67-72` 启动前 `CornerRadiusHelper.SetWindowStyle` + `LauncherAppearance.SetTheme/SetMaterial`；运行中还能随消息改（`:15` 的 `Message` 记录里有 `Theme`/`Material`/`Dark`，`:165-166` 收到就换）。**Predex 不搞独立进程，所以这一层可以省掉**（见 2.5.4）。

## 3.2 Predex 的落地设计

### 3.2.1 现状（必须先承认的差距）

| 现状 | 证据 | 差距 |
|---|---|---|
| 只有 `ThemeManager`，主题枚举是 Core 的 `ApplicationTheme { Dark, Light, Glass }` | `Services/ThemeManager.cs:11-24` | 没有「跟随系统」、没有材质/圆角/主题色 |
| 只对**单个宿主窗口**根元素设 `RequestedTheme` | `ThemeManager.cs:75-83` | 没有多窗口注册表；OSD/更新浮窗会各自为政 |
| `Glass` 判定 = `IsWindowsVersionAtLeast(10,0,22621)` | `ThemeManager.cs:25-26` | 与 Dafeiyu 的 `>= 22000` + 实际 `IsSupported()` 双检不一致 |
| `Apply(bool isLight)` 走 Core `ConfigurationService.Update(cfg => cfg.General.Theme = …)` | `ThemeManager.cs:57-73` | 持久化已经是 Core 结构 → 新字段要按同样方式加 |
| 设置页只有 Language / Theme(Dark/Light/Glass) / Run on Startup / Acer Services / Battery Limit / USB Charging / Power Profiles | `Pages/SettingsPage.xaml:125-433` | 缺窗口风格、材质、主题色 |

### 3.2.2 目标外观项（建议 4 行，正好一张卡）

| 行 | 控件 | 取值 | 默认 |
|---|---|---|---|
| 主题 | ComboBox | 浅色 / 深色 / **跟随系统** | 跟随系统 |
| 窗口风格 | ComboBox | Windows 10（直角）/ Windows 11（圆角） | 按 OS build 自动 |
| 材质 | ComboBox | 云母 / 云母 Alt / 轻薄亚克力 / 标准亚克力 / 纯色 | 云母（不支持则纯色） |
| 主题色 | ComboBox + 色块按钮→Flyout(ColorPicker) | 跟随 Windows / 自选 | 跟随 Windows |

> `Glass` 这个历史取值建议：**保留读取兼容**（老配置不会崩），UI 上映射成「云母」（或直接给一句「Glass 已并入材质选项」），不要在配置迁移时把用户的暗色设置改掉。

### 3.2.3 代码落点

| 新增/修改 | 内容 |
|---|---|
| `Services/Appearance/AppearanceService.cs`（新增） | 静态控制器，取代并包一层 `ThemeManager`。持有 `ElementTheme` / `MaterialKind` / `WindowStyle` / `AccentColor`，维护 `List<WindowRegistration>`；`Register(window, surface, backgroundSetter)`、`ApplyAll()`、`ApplyWindowFrames()`。**逐行照 `LauncherAppearance.cs:32-364`** |
| `Services/Appearance/CornerRadiusHelper.cs`（新增） | 照 `CornerRadiusHelper.cs`：`>=22000` 判默认风格、5 个圆角键、`DwmSetWindowAttribute(33, 2/1)`、`ApplyApplicationResources`。**常量可从现有 `NativeMethods`/`DWMWA_*` 复用，别再造第二份** |
| `Styles/Resources.xaml` | 把所有写死的 `CornerRadius="8"` 之类改成 `{ThemeResource OverlayCornerRadius}` / `{StaticResource ControlCornerRadius}`；这样 Win10 风格一设就全直角 |
| `Services/ThemeManager.cs` | 保留为薄适配层（`ThemeChanged` 事件、`IsLightTheme` 等既有调用点不动），内部转发到 `AppearanceService` |
| `Pages/SettingsPage.xaml` + `.xaml.cs` | 在 General 卡片区加「外观」4 行（照 `SettingsWindow.xaml:2236-2384` 的 SettingsCardStyle 结构），三个 SelectionChanged + 一个 ColorPicker |
| `Shell/ShellWindow.xaml.cs` | 建窗后 `AppearanceService.Register(this, root, brush => root.Background = brush)`；标题栏按钮配色照 `ApplyTitleBarButtonColors`（`:6355-6378`）走一遍 |
| `Views/ModeOsdWindow.xaml.cs` | 同样注册 + `ApplyWindowFrame(hwnd)`；材质不支持时回退纯色 |
| `Controls/UpdateFloatWindow.xaml.cs`（2.6.1 新增） | 同样注册；失败态不要漏掉配色 |
| `PreySense.Core/Configuration/GeneralConfig` | 新增 `WindowStyle` / `Material` / `AccentSource` / `AccentColor` 四个字段 + 默认值 + 归一化（Core 现在是 WPF/WinUI 共用，改字段要同步 WPF 侧默认值，**只写方案不动代码**） |
| `Services/ThemeManager.cs:25-26` | `IsGlassSupported` 从「只看版本」改成「版本 + `MicaController.IsSupported()` 实测」 |

### 3.2.4 三个必须记住的坑（都是 Dafeiyu 踩过的）

1. **资源改了不等于界面变了**：已解析的 `ThemeResource` 不会自己失效 → 必须做「翻一次 `RequestedTheme` 再翻回来」的强制刷新（`SettingsWindow.xaml.cs:6277-6283`）。
2. **窗口必须能重画 DWM 圆角**：风格切换时只改静态字段没用，要对**已存在的每个 HWND** 再调一次 `DwmSetWindowAttribute`（`CornerRadiusHelper.cs:113-115` → `LauncherAppearance.cs:141-160`）。OSD/浮窗是惰性创建的，创建时**必须**补一次。
3. **材质要能体面地失败**：`IsSupported()` 为 false、高对比度、或 backdrop 注册抛异常时，一律回退纯色（`LauncherAppearance.cs:202-212,276-315`），别让用户看到一个黑窗。

---

# 4. 风险与不做什么

## 4.1 明确不做

| 不做 | 理由 | 参考实现的反证 |
|---|---|---|
| **静默自动更新** | Predex 提权 + 硬件操作（风扇/模式），后台替换 + 重启会打断用户正在调的东西；且我们没有回落通道 | Dafeiyu 有 `LauncherUpdateMode=Install` 的启动期自动装（`WinUIProgram.cs:2433-2540`），但它是"启动器壳"，挂了不影响 DSH 服务 |
| **增量补丁 / 差分更新** | 包才几十 MB，收益 < 双份清单 + 补丁链的复杂度与失败率 | Dafeiyu 的补丁系统（`PatchFeedService.cs` 等）是给 DSH 插件用的，不是应用自更新 |
| **自建 CDN / 后端下载代理** | 参考实现那套要服务器 + 自有 CA + SPKI pin + `/api/download/status` 轮询（`BackendDownloadSource.cs:15-104`），Predex 连发布流程都还没有 | 等真有"每月发布 + 有用户"再谈 |
| **GitCode 镜像** | 参考实现里根本不存在（全仓库零命中），没实测数据支撑 | — |
| **强制更新 / 灰度百分比 / 最低版本硬门槛** | 清单里没有这类字段，且强制更新需要一个"能远程把人搞死"的信任链，我们还没有 | 1.2.4 已确认"未找到" |
| **Authenticode 签名校验** | 没有证书；有校验也没东西可校验 | 参考实现自身就没签名（`SIGNING.md:1-23`），我们不假装有 |
| **独立进度进程（info-host）** | 见 2.5.4 | — |
| **跨通道降级 / 自动回滚** | 参考实现明确"本地版本领先不降级"（`UpdateIntegrity.Regression/Program.cs:137-138`）；回滚只给"上一版 dist 备份 + 手动" | — |
| **静默安装脚本提权链** | Predex 进程本身 `requireAdministrator`，脚本是它的子进程（已带管理员令牌），不需要 schtasks 绕 UAC；**别照抄 `/rl highest` 那套**，多一层计划任务就多一个失败点 | `UpdateSupport.cs:772-790` 那套是因为它的脚本上下文未必提权 |

## 4.2 风险表

| 风险 | 影响 | 缓解 |
|---|---|---|
| 清单/包被投毒（我们仓库被入侵） | 用户装到恶意 exe，sha256 也会一起被换 | ① 清单内 URL host 白名单（`UpdateSupport.cs:185-189` 同款）；② 发版必须走"本地构建 → 上传 → **从线上真实下载回来核 sha256** → 才写清单"（`manifests/README.md:5`、`HANDOVER.md:161-162` 的做法）；③ 文档里不许把 sha256 说成"签名" |
| GitHub 匿名 API 限流 60 次/时 | 清单拉不到 | 12 小时缓存（`InstallerUpdateService.cs:44-48`）；API 只做兜底组 |
| 第三方镜像突然停服/限速 | 下载卡住 | 组内并行 + 20s 停滞看门狗 + 换源；镜像池写在代码里可发版修正 |
| 镜像 403/404（防盗链式拒绝） | 白等 | 前缀只套 GitHub 域名（`LauncherFeed.cs:102-107`）；套之前先探活（`GitHubAccelerator.Probe`，HANDOVER 1092） |
| 限速 → 用户以为"卡死" | 体验 | 显示真实速度 + 「切换下载源…」文案（`DownloadSupport.cs:17-23`、`BuiltInSteps.cs:599-601`） |
| 包被 Windows Defender/火绒拦 | 更新失败/文件消失 | 不静默失败：写日志 + 失败态给「打开日志」；发布说明里教用户加白（`HANDOVER.md:44` 的先例） |
| `%TEMP%` 被清理工具清掉 | staging 消失 | 脚本启动即校验 staging 存在，缺失 → 放弃替换、保留原安装 |
| 文件占用（Predex.exe 还在跑） | 复制失败 | 等 PID 退出（≤90s）+ 复制前再查一次；失败则不重启、保留备份（`UpdateSupport.cs:731-737`） |
| 用户磁盘满 | 解压失败 | 预检 `size*2.2`；失败原因直接写"需要 X MB 可用空间" |
| 代理（系统/自定义） | 下载走错路 | 所有请求走同一个 `HttpClient`/工厂，**不要** `static readonly` 建一次用到死（`DownloadEngine.cs:110-149` 的坑） |
| 重定向 + Range 混用 | 分片拿不到 206 | 只对"探测到支持 Range"的源开分片，其余走整文件（`DownloadSupport.cs:107-113`） |
| 预发布号在 `Version`/Assembly 里丢信息 | 显示的版本和清单不一致 | 构建注入 `-p:Version=0.1.0-alpha.1`；比较逻辑**不用** `System.Version`（`UpdateCheckService.cs:54-57` 就是反面教材） |
| 更新失败把应用搞坏 | 无法启动 | 先备份整目录 + 失败保留备份 + 文档给"用 `dist-prev` 手动恢复"（Predex 已有 `src/Update-Dist.ps1:36-38` 的备份习惯） |
| 0.1.0-alpha.1 → 0.1.0 的语义跳变 | 用户以为降级 | 判定表 2.3.2 明写"release 压过自己的预发布"；UI 上显示通道名 |

## 4.3 发布流程前提（否则整套东西没意义）

更新机制依赖一个能发布的地方。落地顺序建议：

1. 建 `github.com/<OWNER>/Predex`（公开），把 `app/experiments/Predex.WinUI` 的 publish 产物打 zip。
2. 本地脚本产出 `manifest-<channel>.json`（sha256 + sizeBytes 由脚本算，人**不许手填**）。
3. 先传 Release → **下载回来复核**（`Get-FileHash` 对上才继续）→ 再推清单 → 再读回清单确认（`HANDOVER.md:161-162` 的流程）。
4. 首发用 `manifest-alpha.json` + `0.1.0-alpha.1`，稳定后再开 `manifest.json`。

---

# 5. 最该照抄的三点

1. **清单 sha256/size 是硬门禁，缺一项直接拒绝下载**（`UpdateSupport.cs:336-340,513-514` + `DshBundleDeployment.cs:218-225` 的"先比大小再比哈希"）：这比任何 UI 都重要，因为它把"换了源下到半个包"变成不可能。
2. **清单源「组内并行 / 组间串行 + 4s/组、8s 总预算」+ 下载「20s 停滞就换源、.part 续传」**（`UpdateMetadataReader.cs:15-18,53-98`、`DownloadSupport.cs:83,193-230`）：国内网络下"某个源这次不通"是常态，靠结构容错，不靠某个源稳定。
3. **替换一定交给独立脚本，先等 PID 退出再备份再覆盖，失败保留现场**（`UpdateSupport.cs:604-797`）：正在运行的程序改不了自己的 exe；脚本纯英文、UTF-8 无 BOM、全程写 `update.log`。

---

## 附：本次调研"未找到"的项（不做推测）

| 问题 | 结论 |
|---|---|
| GitCode 镜像 / 自建 CDN | **未找到**（全仓库 `gitcode` 零命中；唯一自有基础设施是后端服务器代理，不是 CDN） |
| 灰度发布 / 强制更新标识 | **未找到**（清单与设置里都没有强制/灰度字段） |
| 清单内文件大小（启动器） | **未找到**（启动器清单只有 sha256；`sizeBytes` 只存在于 DSH 完整包清单） |
| 架构选择（x64/arm64） | **未找到**（全项目假定 `win-x64`） |
| Authenticode 签名校验 | **未找到**（`SIGNING.md` 明说不签名，代码无 `WinVerifyTrust`） |
| 断点续传"跨进程/跨重启" | **未找到**（`.part` 只在同一次下载任务的多轮重试内保留，任务结束即删） |
| 更新失败自动回滚 | **未找到**（只有备份目录 + 手动恢复） |
| 更新进度显示"剩余时间 / ETA" | **未找到**（只有速度与已下载/总量） |
