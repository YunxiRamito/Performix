# Performix 发布流程（更新源 + 清单 + 校验 + 回滚）

> 配套代码：`src\Performix.App\Services\UpdateClient.cs`（取源 / 下载 / 校验）、
> `src\Performix.App\Services\Update\UpdateManifest.cs`（清单硬门禁）、
> `scripts\invoke-update.ps1`（退出后替换）、`src\Performix.App\Pages\MiniMetaPage.xaml`（更新页）。
> 设计背景见 `docs\UPDATE-DESIGN.md`，本文只讲**怎么发一个版本**。

一句话：**先算出 sha256/size → 写 `update.json` → 传到 GitHub Release 和 npm 包 → 把同一份 `update.json`
提交到仓库默认分支（供三个 GitHub 代理源读取）→ 从线上回读复核 → 才算发布完成**。
顺序不能颠倒：先传包、再写清单、最后回读验证。

推一个 `v*` tag 会触发 CI 跑完整条链路，并且**自己把 `update.json` 提交回默认分支**（§5.1）；
本地 `scripts\publish-release.ps1` 走同一套门禁，上传后还会和线上资产对账（§5.2）。

---

## 1. 源与文件对应关系

一个版本要同时出现在 5 个位置，命中哪个由客户端决定（先官方组，失败/超时再加速组）：

| 组 | 源 id | 地址 | 读的是哪个文件 |
|---|---|---|---|
| 官方 | `github-releases` | `https://api.github.com/repos/YunxiRamito/Performix/releases` | Release 资产的 `update.json`（同名资产） |
| 官方 | `npm-official` | `https://registry.npmjs.org/performix` | npm 包内的 `package/update.json` |
| 加速 | `gh-proxy` | `https://gh-proxy.com/https://raw.githubusercontent.com/YunxiRamito/Performix/main/update.json` | **仓库默认分支**的 `update.json` |
| 加速 | `ghproxy-net` | `https://ghproxy.net/https://raw.githubusercontent.com/YunxiRamito/Performix/main/update.json` | 同上 |
| 加速 | `jsdelivr-gh` | `https://cdn.jsdelivr.net/gh/YunxiRamito/Performix@main/update.json` | 仓库文件（jsDelivr 只服务仓库文件，**不能**代理 Release 资产） |
| 加速 | `npmmirror` | `https://registry.npmmirror.com/performix` | npm 包内的 `package/update.json` |

> **Release 里的资产现在有三个**：`Performix-win-x64.zip`（应用包）、`PerformixSetup.exe`（amd64
> 安装器）、`update.json`（清单）。**只有 zip 进清单的 `assets[]`，安装器不进清单**——它不是更新
> 资产，客户端永远不会主动下载它来自我替换；它只由用户从 Release 页手动下载运行（用途见 §2 的
> 「安装器」小节）。

### 源与资产地址白名单（客户端硬门禁）

客户端只接受下面这些 host 的地址（`UpdateManifest.IsKnownReleaseHost`，校验的是清单里的
`assets[].url`；源地址本身是代码里的常量）：

| 用途 | host |
|---|---|
| 发布 / API | `github.com`、`api.github.com`、`codeload.github.com` |
| GitHub 内容 CDN | `raw.githubusercontent.com`、`objects.githubusercontent.com`，以及任意 `*.githubusercontent.com` |
| npm | `registry.npmjs.org`、`registry.npmmirror.com` |
| 加速 | `cdn.jsdelivr.net`、`gh-proxy.com`、`ghproxy.net` |

- `api.github.com`（`github-releases` 源）和 `codeload.github.com`（Release 资产的真实落点）
  **在代码里是允许的**，写清单/查日志时不要把这两个当成“被拒绝的域”。
- 非 https 一律拒绝；唯一例外是下面的本地 override 开关。

### 本地 override（`--update-feed`，仅测试）

带 `--update-feed <本地或回环地址>` 启动时，客户端进入“本地测试”模式（`allowLocalUrls`）：

- 允许 `file:` 地址，以及 `http:` —— 但只限**环回地址**（`127.0.0.1` / `localhost` / `::1`，
  判据是 `Uri.IsLoopback`）。指向公网的 `http://` 依旧被拒绝。
- 设置这个开关时安装会被自动抑制（`--update-feed` 会话停在 ReadyToInstall，不替换真实安装），
  所以它不可能把一台正常用户的机器升级到本地包。

**正式分发禁用这三个开关**（`--update-feed` / `--update-auto` / `--update-no-install`），
它们只用于发布验收和离线复现 4 张更新页截图。

由此推出两条硬规则：

1. **`update.json` 必须提交到仓库默认分支**（`main`），否则三个 GitHub 加速源全部 404。
2. **资产下载地址写 GitHub Release 的 https URL**：客户端会自动再生成
   `https://gh-proxy.com/<原地址>` 与 `https://ghproxy.net/<原地址>` 两个候选（`BuildDownloadCandidates`），
   所以“换源重试”不需要清单里再维护一份 mirrors 数组。

### 通道与文件名

| 通道 | 清单文件名 | 版本形态 | 现在是否发布 |
|---|---|---|---|
| `alpha` | `update.json` | `0.1.0-alpha.N` | ✅ 当前唯一通道 |
| `beta` | `update-beta.json` | `0.2.0-beta.N` | 未启用（客户端已支持；要并行发布时按同样结构加文件） |
| `stable` | `update-stable.json`（见下方“已知冲突”） | `0.2.0` | 未启用 |

清单里的 `channel` 字段必须与文件名/通道一致，客户端会严格比对（`alpha` 客户端拿到 `channel=beta`
的清单会直接拒绝，而不是装上）。

> **已知冲突：stable 通道的清单文件名，文档与代码不一致（待决策，暂未改代码）**
>
> - **文档原本的设计意图**：稳定期把仓库里的 `update.json` 换成 stable 内容，即 stable 也读
>   `update.json`（上一版本文档表格写的就是这个）。
> - **当前代码的实际行为**：`UpdateSourceCatalog.ManifestFileName(channel)` 只对 `alpha` 返回
>   `update.json`，**其余通道一律返回 `update-<channel>.json`**；因此 stable 客户端会去取
>   `https://…/main/update-stable.json`（以及对应代理/jsDelivr 地址），**不是** `update.json`。
>   `github-releases`（Release 资产）和 npm 两个源带兜底：先找 `update-<channel>.json`，找不到再
>   退回 `update.json`（`UpdateClient` 的 release-asset / `package/` 取名处）；而**仓库文件类的四个
>   源没有兜底**（`github-raw` / `gh-proxy` / `ghproxy.net` / `jsdelivr-gh`），它们只会 404。
> - **影响面**：目前只发布 `alpha`，两条路都走 `update.json`，线上没有实际故障；但一旦按旧文档
>   发 stable（只放 `update.json`），四个仓库文件源会全部 404，只剩官方 Release / npm 两条路。
> - **需要决策（二选一，改代码前不要按旧表格操作）**：
>   1. **跟代码**（推荐）：stable 就发 `update-stable.json`，与 beta 结构一致。好处是 alpha/stable
>      可并行发布、互不覆盖仓库里的同一个 `update.json`；代价是 stable 要多维护一个文件名，
>      且 `update.json` 永远保持 alpha（预发布）语义。
>   2. **跟文档**：在 `ManifestFileName` 里把 `stable` 也映射到 `update.json`（alpha 与 stable
>      互斥、同时间只能发一个通道）。好处是发版命令不变；代价是两个通道不能并存，切换通道时要
>      覆盖同一个文件，`--check-update` 的通道断言容易踩错。
>
> 决策前，**stable 的实际文件名以代码为准**（`update-stable.json`）。

---

## 2. 打包

```powershell
$sdk = 'G:\DeepSeek DSH\DSH Works\Project\PredatorSense\tools\dotnet-sdk\dotnet.exe'
$repo = 'G:\DeepSeek DSH\DSH Works\Project\Performix'
$out  = Join-Path $repo 'artifacts\publish\alpha'
& $sdk publish (Join-Path $repo 'src\Performix.App\Performix.App.csproj') -c Release -p:Platform=x64 `
    -p:Version=0.1.0-alpha.2 -r win-x64 --self-contained false -o $out

$zip = Join-Path $repo 'artifacts\Performix-win-x64.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
```

- 版本号一处改：`-p:Version=` 会同时写进 `AssemblyInformationalVersion`，更新页显示的就是它；
  `Directory.Build.props` 里的默认值只在本地调试用。
- zip 里必须能直接看到 `Performix.exe`（`invoke-update.ps1` 会在根目录找，找不到才递归找一层）。
- 发布产物应包含 `scripts\invoke-update.ps1`（csproj 已把它复制到输出目录），否则更新完成后无法自替换。

本节上面的命令是**事实依据**；脚本只是把它们串起来，顺序和门禁都没变：

| 脚本 | 干什么 |
|---|---|
| `scripts\build-app-zip.ps1 -Version <ver>` | §2 的 publish + 打包（确定性容器，见 §2.1），产出 `artifacts\Performix-win-x64.zip`，跑完打印 sha256 / sizeBytes（**仅本次构建有效**） |
| `scripts\build-installer.ps1 -Version <ver>` | 产出 `artifacts\PerformixSetup.exe`（amd64 安装器，见下） |
| `scripts\publish-release.ps1 -Version <ver> -NotesFile <file>` | 一条命令走完 §2 → §8（除 npm），`-DryRun` 只跑本地步骤 |

### 安装器：每次发布都要带一个 `PerformixSetup.exe`（amd64）

zip 解决的是「已经装了 Performix 的机器怎么换版本」，安装器解决的是「这台机器还没装」。所以
**每次发布都必须带一个 amd64 的 `artifacts\PerformixSetup.exe`**，跟 zip 同一次上传；只发 zip
不发 exe，等于把新用户和 `minVersion` 不满足、只能整装重来的用户一起挡在门外。产物路径是约定值
（`artifacts\PerformixSetup.exe`），`scripts\publish-release.ps1` 和
`.github\workflows\release.yml` 都按这个路径取文件，改路径两边都要改。

安装器做什么（只列发布方要知道的；实现看源码目录 `src\Performix.Installer\`，不往这里抄）：

1. **先看运行时**：探测本机有没有 .NET 10 桌面运行时（`Microsoft.WindowsDesktop.App` 和配套的
   `Microsoft.NETCore.App` 两半都得在）。缺了就当场装——同样是**多线程分段下载**
   （`SegmentedDownloader`，并发分段 + 断点续传），装完再继续装本体。用户不需要预先准备 .NET。
2. **再装本体**：解到 `%LOCALAPPDATA%\Performix\app\<version>\`，一个版本一个目录。故意不装进
   `Program Files`：写文件这一步就不需要提权（唯一要提权的是机器级运行时安装）。
3. **建开始菜单快捷方式**：`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Performix.lnk`。
4. **`--uninstall` 卸载**：删快捷方式、删 `app\<version>\`；`preysense.json` 会被搬到
   `%LOCALAPPDATA%\Performix\` 并把路径打出来，不跟着一起没。

**安装器也校验哈希**，规矩和更新器一样（§3：「大小或 sha256 不符就不替换」）：优先用 GitHub 给
Release 资产算的 digest（sha256），没有就找同目录的 `Performix-win-x64.zip.sha256` 资产，两者都
没有时它会明说「跳过校验」而不是假装校验过。所以**重新打包 zip 就等于换了一份哈希**：清单、
Release 资产、安装器读到的值必须来自同一次构建，不要只重传其中一个。

### 2.1 可复现性：容器钉住了，内容没有（别拿本地哈希当准）

同一个 commit，本机打出来的包和 CI 打出来的包**不是同一个文件**。0.1.0-alpha.2 的实测：

| 构建位置 | 字节数 | sha256（前 8 位） |
|---|---|---|
| 本机 | 33490261 | `a8d5e3e7…` |
| CI（GitHub runner） | 33489786 | `3d5048…` |

`scripts\build-app-zip.ps1` 现在把 **zip 容器**钉死了：

- 条目按相对路径做**序数排序**（`[StringComparer]::Ordinal`，不是 `Sort-Object` 的区域性排序——换台
  机器/换个 locale，顺序就会变）；
- 所有条目的时间戳固定成同一个值（1980-01-01 00:00，DOS 时间字段能表示的最小值）。之前每个条目带的是
  文件自己的 `LastWriteTime`，光是这一项就足以让两次构建的字节不同；
- 不写目录条目、外部属性固定为 0、压缩级别固定 `Optimal`。

**内容**钉不住，所以哈希不跨机器：

- **.NET SDK 补丁版本**：10.0.1xx 和 10.0.2xx 带的编译器和运行时包不同。CI 用 `actions/setup-dotnet`
  的 `10.0.x`（当天解析到哪个算哪个），本机用装的那个或 `tools\dotnet-sdk\`。
- **Windows App SDK 与整个 NuGet 还原图**：浮动版本、缓存状态、fallback 目录。
- **pdb 里烧进去的源码绝对路径**：CI 的 `D:\a\Performix\Performix` 和本机路径长度不同，字节就不同。
  上面那 475 字节的差就是这类因素造成的（没有逐字节归因，别把这句话当成精确结论）。
- **`PublishReadyToRun` / PGO / ILLink**：任何一项都会换掉编译出来的代码。脚本目前不传，也别加。
- MSBuild 节点复用、增量构建状态等「取决于这台机器之前跑过什么」的东西。

由此定一条规则，别再自己骗自己：

> **以 CI 构建的资产为准，本地构建仅用于验证。**
> 本地跑出来的 sha256 **仅在本次构建有效**，不要拿它去填 `update.json`，也不要拿它和线上资产比。
> `build-app-zip.ps1` 跑完会把这句话打印在 `REPRODUCIBILITY` 段里。

要「本地验完再发」，验的是**行为**（能装、能起、更新页正常、换源正常），不是字节。

---

## 3. 算 sha256 / size（人手不许填）

```powershell
$zip  = Join-Path $repo 'artifacts\Performix-win-x64.zip'
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $zip).Length
"sha256 = $hash"
"size   = $size"
```

两个都是硬门禁：清单里缺任一项，客户端**拒绝该资产**（记录原因后继续找别的资产/别的源），永远不会
“先下载再祈祷”。

---

## 4. 生成 update.json

`notes` 字段和 Release 正文是**同一份文本**，来源是 `CHANGELOG.md` 里对应版本那一节——由
`scripts\get-release-notes.ps1` 抽出来（规则见 §5.1），找不到就退回一个短模板。**不要**再拿
`git log --oneline` 堆 commit subject。

```powershell
$tag     = 'v0.1.0-alpha.2'
$version = '0.1.0-alpha.2'
$notes   = @'
- 新增：更新页（状态机 / 进度 / 换源重试）
- 修复：托盘图标在高 DPI 下的模糊
'@
$assetUrl = "https://github.com/YunxiRamito/Performix/releases/download/$tag/Performix-win-x64.zip"

$manifest = [ordered]@{
    schema      = 1
    channel     = 'alpha'
    version     = $version
    publishedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    notes       = $notes
    minVersion  = '0.1.0-alpha.1'
    assets      = @(
        [ordered]@{
            name      = 'Performix-win-x64.zip'
            url       = $assetUrl
            sizeBytes = $size
            sha256    = $hash
        }
    )
}
$json = $manifest | ConvertTo-Json -Depth 6
$manifestPath = Join-Path $repo 'artifacts\update.json'
# UTF-8 无 BOM：BOM 会让某些镜像/JSON 解析器读到多余字节
[System.IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))
```

字段规则（`UpdateManifestParser` 的实现口径）：

| 字段 | 必填 | 规则 |
|---|---|---|
| `schema` | ✅ | 必须 `1` |
| `channel` | ✅ | 必须等于客户端请求的通道 |
| `version` | ✅ | 语义化版本，允许 `0.1.0-alpha.2`；不能只有两段之外的花样 |
| `publishedAt` | ✅ | ISO-8601，客户端按 UTC 显示 |
| `notes` | ⬜ | 纯文本，更新页最多显示 240 DIP 高，超出滚动 |
| `minVersion` | ⬜ | 该版本可以就地升级的最低旧版本。当前版本**低于**它时，客户端**拒绝**下载/就地安装（`CanDownload` 为 false，状态机给出 `RequiresManualInstall` 与原因），用户必须手动安装完整包；当前版本 ≥ 它时按正常流程更新 |
| `assets[].name` | ✅ | 纯文件名，不许带路径，**长度上限 120 字符**，建议 `.zip` 且含 `win-x64`（多资产时优先取它） |
| `assets[].url` | ✅ | https，host 必须在 §1 的白名单（`github.com` / `api.github.com` / `codeload.github.com` / `*.githubusercontent.com` / `registry.npmjs.org` / `registry.npmmirror.com` / `cdn.jsdelivr.net` / `gh-proxy.com` / `ghproxy.net`）；`http`/`file` 只在 `--update-feed` 本地 override 且为环回地址时放行 |
| `assets[].sizeBytes` | ✅ | 1 … 1 GiB |
| `assets[].sha256` | ✅ | 64 hex |

---

## 5. 发布到 GitHub Release

```powershell
cd $repo
git tag $tag                      # 只打 tag，别忘了推送 tag
git push origin $tag

gh release create $tag `
  (Join-Path $repo 'artifacts\Performix-win-x64.zip') `
  (Join-Path $repo 'artifacts\PerformixSetup.exe') `
  (Join-Path $repo 'artifacts\update.json') `
  --title "Performix $version" `
  --notes-file (Join-Path $repo 'artifacts\release-notes.md') `
  --prerelease
```

要点：

- **三个资产一起传**：zip（给已装机器更新）、`PerformixSetup.exe`（给没装的机器首次安装，
  见 §2 的安装器小节）、`update.json`（`github-releases` 源读的就是它）。少传清单 = 官方源直接
  失败；少传 exe = 新用户装不了。
- 版本说明用它自己那份文本（`artifacts\release-notes.md`），别拿 `update.json` 当 notes 传——
  那会把一整段 JSON 摊在 Release 页上。
- **tag 名带 `v`**（`v0.1.0-alpha.2`），客户端会剥掉前缀再比较。
- Release 里**必须有 `update.json` 资产**：`github-releases` 源就是靠它拿清单的；只有 tag 没有清单
  → 客户端记 “release 不带 update.json”，继续找别的源。
- `--prerelease` 与版本里的 `alpha/beta/rc` 保持一致；`stable` 版本的 tag 不带后缀。

### 5.1 CI 发布（`v*` tag 触发 `.github\workflows\release.yml`）

推一个 `v*` tag（或手动 `workflow_dispatch`）就跑完整条流水线：构建 → 测试 → 打包 → 安装器 →
notes → `update.json` → 上传三个资产 → **把 CI 自己那份 `update.json` 提交回默认分支**。
本地 `publish-release.ps1` 和 CI 用同一套门禁，区别只在「谁上传」和「谁提交清单」。

#### notes 从 CHANGELOG.md 来

- 来源：`scripts\get-release-notes.ps1`，取 `CHANGELOG.md` 里对应版本那一节（`## 0.1.0-alpha.2 — …`
  这种标题；`## v0.1.0-alpha.2`、`## [0.1.0-alpha.2]` 也认）。这一节同时是 Release 正文和
  `update.json` 的 `notes` 字段。
- 找不到这一节时退回模板：版本 + 一句概要 + 指向 `CHANGELOG.md` 的链接。模板是英文的——那几个脚本
  按约定是 UTF-8 无 BOM + 纯 ASCII，写中文在 Windows PowerShell 5.1 下会被当 ANSI 读坏。
- **不再用 `git log --oneline`**：一串 commit subject 在 Release 页面上没法读，而且会把上一版就已经
  发出去的东西再列一遍（0.1.0-alpha.2 的线上 notes 就是这么来的，还带着 bootstrap 那条）。
- 本地 `scripts\publish-release.ps1` 在既没给 `-Notes` 也没给 `-NotesFile` 时走同一条规则，
  两条路不会产出两种 notes。

#### 发布后自动同步 `update.json`（0.1.0-alpha.2 踩过的坑）

`gh-proxy` / `ghproxy.net` / jsDelivr 读的是**仓库默认分支**里的 `update.json`，不是 Release 资产。
tag 触发 CI 后，CI 重新构建并 `--clobber` 上传了 zip，它生成的清单描述的是 CI 那份字节；仓库里那份
还指着本地构建的包（`a8d5e3…` vs 线上 `3d5048…`）→ 走加速源的用户下载后校验失败。那次是手工提交修好的。

现在流水线最后一个 step 自己收尾：

- 用 `github-actions[bot]` 身份提交（`41898282+github-actions[bot]@users.noreply.github.com`）；
- 只提交 `update.json`（非 alpha 通道是 `update-<channel>.json`），不碰别的文件；
- 内容没变就不提交——幂等，重跑不会堆空提交；
- commit message 带 `[skip ci]`（这个 workflow 只监听 tag 和手动触发，本来也回不到自己；标记是给
  别的监听分支的 workflow 看的）；
- `main` 在构建期间被推过的话，`fetch` + `rebase` 重试 3 次，不会因为一次非快进让整场发布失败；
- 在 Release 建好之后才执行，上传失败不会留下「分支指向一个还不存在的资产」；
- 权限就是现有的 `contents: write`。

> 提交完成后 `gh-proxy` / jsDelivr 有缓存，要等一会儿才生效——§8 的源检查就是等这个。

### 5.2 清单与线上资产对账（本地发布时）

`scripts\publish-release.ps1` 上传完之后、提交清单之前，会先下载线上资产算一遍 sha256：

- **和本次构建一致** → 继续。
- **不一致**（同一个 tag 又跑过 CI，线上包被 `--clobber` 换掉了）→ **以线上为准**：用线上的
  sha256/size 重写清单、`gh release upload --clobber` 把清单重传一遍，再把这份清单提交到默认分支。
  本地那个包留在 `artifacts\` 里，但它已经不是清单指向的文件了，脚本会说明。
- **下载不到** → 只警告，不重写；后面的回读步骤会把它变成硬失败。

理由和 §5.1 一样：清单要描述用户**实际下载到的字节**，不是我们以为的字节。

## 6. 发布到 npm

npm 包的作用是给 `npm-official` / `npmmirror` 两个源提供同一份清单。包本身不需要可执行，只需要
`update.json`：

```powershell
$npmDir = Join-Path $repo 'artifacts\npm'
New-Item -ItemType Directory -Path $npmDir -Force | Out-Null
Copy-Item (Join-Path $repo 'artifacts\update.json') (Join-Path $npmDir 'update.json') -Force
@'
{
  "name": "performix",
  "version": "0.1.0-alpha.2",
  "description": "Performix update manifest (the app itself ships as a GitHub Release zip)",
  "files": ["update.json"],
  "license": "GPL-3.0-only"
}
'@ | Set-Content -LiteralPath (Join-Path $npmDir 'package.json') -Encoding utf8NoBOM

Push-Location $npmDir
npm publish --access public --tag alpha    # dist-tag 用 alpha，npmmirror 会自动同步
Pop-Location
```

发布后核对：

```powershell
(Invoke-WebRequest -UseBasicParsing 'https://registry.npmjs.org/performix').StatusCode     # 200
(Invoke-WebRequest -UseBasicParsing 'https://registry.npmmirror.com/performix').StatusCode # 200（同步有延迟）
```

> npm 包名占位说明：如果 `performix` 在 npm 上不可用，改 `UpdateSourceCatalog.NpmPackage` 常量，
> 并把两个 registry URL 一起改，清单格式不变。

## 7. 提交清单到仓库（三个 GitHub 代理源的唯一入口）

```powershell
Copy-Item (Join-Path $repo 'artifacts\update.json') (Join-Path $repo 'update.json') -Force
git add update.json
git commit -m "release: update.json for $version"
git push origin main
```

> **CI 也会做这一步**：tag 触发时流水线最后一个 step 会把 CI 自己的清单提交到默认分支（§5.1）；
> 本地脚本在提交前会先和线上资产对账（§5.2）。两条路都不要手工 `git commit` 一份来路不明的
> `update.json`——那是 0.1.0-alpha.2 出问题的直接原因。

验证三个代理源都能读到（应当逐条 200，且内容与本地一致）：

```powershell
foreach ($u in @(
  'https://gh-proxy.com/https://raw.githubusercontent.com/YunxiRamito/Performix/main/update.json',
  'https://ghproxy.net/https://raw.githubusercontent.com/YunxiRamito/Performix/main/update.json',
  'https://cdn.jsdelivr.net/gh/YunxiRamito/Performix@main/update.json')) {
  try { $r = Invoke-WebRequest -UseBasicParsing $u -TimeoutSec 20; "$($r.StatusCode) $u" }
  catch { "FAIL $u : $($_.Exception.Message)" }
}
```

---

## 8. 发布验收（必做，不许跳）

1. **从线上回读资产并核哈希**（防止上传被截断 / CDN 缓存旧文件）：

   ```powershell
   $tmp = Join-Path $env:TEMP 'Performix-release-check.zip'
   Invoke-WebRequest -UseBasicParsing $assetUrl -OutFile $tmp
   $line  = (Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
   $size2 = (Get-Item $tmp).Length
   if ($line -ne $hash -or $size2 -ne $size) { throw "线上包与清单不一致，停止发布" }
   Remove-Item $tmp
   ```

2. **跑一次命令行检查**（不下载、不安装）：

   ```powershell
   $exe = 'src\Performix.App\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\Performix.exe'
   & $exe --check-update
   ```

   期望输出里 `result : 有新版 / update available`，`source` 是实际命中的源（官方或是某个加速源），
   `version` 等于刚发的版本。同一行命令加 `--update-feed <本地清单>` 可在离线环境复现。

3. **更新页点一遍**：「检查更新 → 下载并安装」。**真机验证只做一次**，确认会退出、替换、重启。

4. 若 `source` 一直是官方源，说明加速源也通；若只有加速源通，属于预期（客户端已降级）。

---

## 9. 失败与回滚

### 客户端侧（用户机器）

- 更新页失败态会显示原因 + 「重试」+「换源重试」。下载失败的 `.part` 留在
  `%TEMP%\Performix\update\`，重试会走 HTTP Range 续传。
- 校验失败（大小或 sha256 不符）会**删除临时文件**，并在页面上写「校验失败」，不会替换任何东西。

### 替换脚本侧（`scripts\invoke-update.ps1`）

启动参数：`-Zip` / `-Target` / `-Pid` / `-Sha256` 由 `UpdateInstaller.Start` 传入，
`-RestartArguments "--updated-from <旧版本>"` 也由它传入（另两个参数 `-WaitSeconds` / `-NoRestart`
只给验证脚本用）。新进程启动时据此识别「刚更新过」，写一条 `update.log` 并弹一次轻提示
（`UpdateLaunchNotice` + `LocalizationService`），所以用户能看到「已更新到 x.y.z」。

脚本全程写 `%LOCALAPPDATA%\Performix\logs\update.log`，退出码：

| 退出码 | 含义 | 现场 |
|---|---|---|
| 0 | 替换成功并已重启 | staging 已清理 |
| 1 | 前置检查失败（包不存在 / sha256 不符 / 90s 内旧进程没退出） | 什么都没改 |
| 2 | 有文件复制失败，**没有重启** | staging + `backup\` 都保留 |
| 3 | 新版本启动失败 | `backup\` 保留 |

手动回滚（脚本会把它打印在日志里）：

```powershell
$dir = 'C:\path\to\Performix'
$backup = Get-ChildItem (Join-Path $dir 'backup') -Filter 'Performix.exe.*' |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Process Performix -ErrorAction SilentlyContinue | Stop-Process -Force
Copy-Item $backup.FullName (Join-Path $dir 'Performix.exe') -Force
Start-Process (Join-Path $dir 'Performix.exe')
```

### 发布侧（发错了怎么办）

- **清单写错但包没问题**：直接改仓库 `update.json` + 重新 `gh release upload --clobber`，客户端下次检查即可。
  已经装上旧包的机器不会再“降级”（客户端永不降级）。
- **清单和线上包对不上**（加速源报 sha256 不符）：**以线上包为准**重写清单——`publish-release.ps1`
  现在会自动这么做（§5.2）；手工做就是 `gh release download` 线上包 → 算 sha256/size → 改清单 →
  `gh release upload --clobber update.json` → 同一份提交到 `main`。**不要**反过来「重传一个本地包」，
  那正是 0.1.0-alpha.2 的坑。
- **包本身有问题**：把 Release 标记成 draft / 删除资产，然后把仓库 `update.json` 改成指向上一版
  （或删掉 `assets`，让所有源都变成“清单不完整”），客户端会停在失败态而不是装坏包。
- **npm 发错**：`npm unpublish performix@<version> --force`（72 小时内）或用 `npm deprecate` 标记。

---

## 10. 明确不做

| 不做 | 理由 |
|---|---|
| 代码签名 / Authenticode 校验 | 没有证书。**sha256 不等于签名**：清单和包都在同一个仓库，仓库被投毒时两者会一起被换掉。文档和 UI 都不许说成“签名校验”。 |
| 静默自动更新 | Performix 是提权 + 调硬件的托盘程序，后台替换重启会打断用户正在调的东西。更新页是唯一入口。 |
| 强制更新 / 灰度百分比 | 清单格式里没有这类字段，也不打算加（没有能远程“搞死”用户的信任链）。 |
| 增量补丁 | 包才几十 MB，双清单 + 补丁链的复杂度与失败率不合算。 |
| 跨机器 bit-for-bit 可复现构建 | 做不到（§2.1）：SDK 版本、NuGet 还原图、pdb 里的绝对路径都会改字节。zip 容器已经确定化，内容没有。规则是「以 CI 构建的资产为准，本地构建仅用于验证」，不假装做到了。 |
| --update-feed / --update-auto / --update-no-install 用于正式分发 | 那是验证开关：带 `--update-feed` 启动时安装会被自动抑制，永远不会替换真实安装。 |
