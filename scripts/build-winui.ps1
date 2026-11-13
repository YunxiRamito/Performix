#Requires -Version 7.0
<#
.SYNOPSIS
  把 WinUI 3 版 Performix 发布到 dist-winui（不碰 dist\ —— 那是 WPF 回滚路径）
.DESCRIPTION
  1. 检查 dist-winui 里的 Performix.exe 是否在跑（文件锁会让镜像失败）
  2. 用仓库外/仓库内的便携 SDK 跑验证过的命令：
       dotnet publish src\Performix.App\Performix.App.csproj -p:Platform=x64 -c Release -nodeReuse:false
     故意不传 -o：resources.pri 只在默认输出目录（...\win-x64\publish）验证过。
  3. 把默认输出目录镜像到 dist-winui（robocopy /MIR，顺带清掉上一版多余文件）
  4. 校验 Performix.exe / resources.pri 存在、且没有残留 Performix.pri
     （MRM 会优先加载 <exe名>.pri，留着 = 启动约 50% 概率堆崩）
  5. dist-winui 原本没有 preysense.json 时，从 dist\preysense.json 播一次种
     （两版 exe 目录不同 => 各读自己目录下的 preysense.json）
  6. 打印产物路径 / 文件数 / 校验结果 / 切换入口的命令

  SDK 解析顺序：$env:PERFORMIX_DOTNET，否则 <repo>\tools\dotnet-sdk\dotnet.exe
  （便携 SDK 不提交，tools/ 已忽略）。

  只写两个目录：src\Performix.App\bin\**（构建产物，SDK 自己写）、dist-winui。
.PARAMETER SkipBuild
  跳过 dotnet publish，直接把现有默认输出目录镜像到 dist-winui（调试脚本用）。
.PARAMETER SkipConfigSeed
  dist-winui 缺 preysense.json 时不从 dist\ 复制种子配置。
.EXAMPLE
  pwsh -File scripts\build-winui.ps1
.EXAMPLE
  pwsh -File scripts\build-winui.ps1 -SkipConfigSeed
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipConfigSeed
)

$ErrorActionPreference = 'Stop'

$scriptsDir = $PSScriptRoot                                  # <repo>\scripts
$repoDir = Split-Path -Parent $scriptsDir                    # <repo>
$projDir = Join-Path $repoDir 'src\Performix.App'
$proj = Join-Path $projDir 'Performix.App.csproj'
$dest = Join-Path $repoDir 'dist-winui'
$wpfExe = Join-Path $repoDir 'dist\Predex.exe'
$wpfCfg = Join-Path $repoDir 'dist\preysense.json'

function Resolve-DotNet {
    if ($env:PERFORMIX_DOTNET) {
        $explicit = $env:PERFORMIX_DOTNET
        if (-not (Test-Path $explicit)) {
            throw "PERFORMIX_DOTNET 指向的文件不存在：$explicit"
        }
        return (Resolve-Path $explicit).Path
    }
    $default = Join-Path $repoDir 'tools\dotnet-sdk\dotnet.exe'
    if (-not (Test-Path $default)) {
        throw @"
找不到 .NET 10 SDK。二选一：
  1) 设置环境变量 PERFORMIX_DOTNET 指向 dotnet.exe（推荐）
  2) 把便携 SDK 放到 $default（tools\ 已被 .gitignore 忽略，不会提交）
"@
    }
    return $default
}

if (-not (Test-Path $proj)) { throw "找不到 WinUI 项目：$proj" }

# 0. 占用检查：dist-winui 里的 exe 在跑时镜像必失败
$running = Get-Process -Name Performix -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($dest, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    throw ("dist-winui 里的 Performix.exe 正在运行（pid " +
           (($running | ForEach-Object { $_.Id }) -join ', ') + "），先在托盘 Quit 再发布")
}

# 1. 发布
if (-not $SkipBuild) {
    $dotnet = Resolve-DotNet
    $sdkDir = Split-Path -Parent $dotnet
    $env:DOTNET_ROOT = $sdkDir
    $env:PATH = "$sdkDir;$env:PATH"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

    Write-Host "[winui] SDK $(& $dotnet --version)" -ForegroundColor Cyan
    Write-Host '[winui] publish src\Performix.App\Performix.App.csproj -p:Platform=x64 -c Release' -ForegroundColor Cyan
    & $dotnet publish $proj -p:Platform=x64 -c Release -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit $LASTEXITCODE）" }
} else {
    Write-Host '[winui] -SkipBuild：跳过发布，直接镜像现有输出目录' -ForegroundColor Yellow
}

# 2. 找默认输出目录（TFM 变了也不用改脚本）
$releaseRoot = Join-Path $projDir 'bin\x64\Release'
$publishDir = $null
if (Test-Path $releaseRoot) {
    $publishDir = Get-ChildItem $releaseRoot -Directory -Filter 'net*-windows*' |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName 'win-x64\publish' } |
        Where-Object { Test-Path (Join-Path $_ 'Performix.exe') } |
        Select-Object -First 1
}
if (-not $publishDir) {
    throw "没找到发布输出（$releaseRoot\net*-windows*\win-x64\publish\Performix.exe）—— 构建真的成功了吗？"
}
$publishDir = (Resolve-Path $publishDir).Path
Write-Host "[winui] publish 输出：$publishDir" -ForegroundColor DarkGray

# 3. 镜像到 dist-winui（/MIR 会清掉上一版多余文件；先保住已有的 preysense.json）
$destCfg = Join-Path $dest 'preysense.json'
$existingCfg = $null
if (Test-Path $destCfg) {
    $existingCfg = [System.IO.File]::ReadAllText($destCfg)
}

$stale = Join-Path $publishDir 'Performix.pri'
if (Test-Path $stale) {
    Write-Host '[winui] 输出目录残留 Performix.pri（MRM 加载错 PRI 的根因），先删掉' -ForegroundColor Yellow
    Remove-Item $stale -Force
}

New-Item -ItemType Directory -Path $dest -Force | Out-Null
& robocopy $publishDir $dest /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy 镜像失败（exit $LASTEXITCODE）：$publishDir -> $dest" }
Write-Host "[winui] 已镜像 -> $dest" -ForegroundColor Green

# 4. 校验
$exe = Join-Path $dest 'Performix.exe'
$pri = Join-Path $dest 'resources.pri'
$bad = Join-Path $dest 'Performix.pri'
if (Test-Path $bad) {
    Remove-Item $bad -Force
    Write-Host '[winui] dist-winui 里残留 Performix.pri，已删除（否则 MRM 加载错 PRI）' -ForegroundColor Yellow
}
if (-not (Test-Path $exe)) { throw "发布后找不到 $exe" }
if (-not (Test-Path $pri)) {
    throw ("$dest 里没有 resources.pri —— 非打包 WinUI 缺它会在 Application.Start 解析资源时崩。" +
           '检查 src\Performix.App\Performix.App.csproj 的 EnableMsixTooling=true 和构建日志')
}

# 5. 配置：dist-winui 原有的保留；没有才从 dist 播种
if ($null -ne $existingCfg) {
    [System.IO.File]::WriteAllText($destCfg, $existingCfg)
    Write-Host '[winui] 保留 dist-winui 原有的 preysense.json（未用 dist 覆盖）' -ForegroundColor DarkGray
} elseif (-not $SkipConfigSeed -and (Test-Path $wpfCfg)) {
    Copy-Item $wpfCfg $destCfg
    Write-Host '[winui] 首次播种 preysense.json：dist -> dist-winui（之后两份独立）' -ForegroundColor Yellow
} else {
    Write-Host '[winui] dist-winui 没有 preysense.json，首次启动会生成默认配置' -ForegroundColor Yellow
}

# 6. 报告
$files = Get-ChildItem $dest -Recurse -File
$mb = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 1)
$topCount = (Get-ChildItem $dest -File).Count
$priMb = [math]::Round((Get-Item $pri).Length / 1MB, 2)
Write-Host ''
Write-Host '========== WinUI 发布完成 ==========' -ForegroundColor Green
Write-Host "产物目录 ：$dest"
Write-Host "可执行   ：$exe"
Write-Host "resources.pri：存在（$priMb MB） / Performix.pri：不存在"
Write-Host "文件数   ：$($files.Count)（顶层 $topCount） / $mb MB"
Write-Host ("WPF 回滚 ：$wpfExe（" + $(if (Test-Path $wpfExe) { '在位' } else { '缺失，先跑 scripts\build-fork.ps1' }) + '）') -ForegroundColor DarkGray
Write-Host ''
Write-Host '启动 ：pwsh -File scripts\Run-GUI.ps1               # 默认入口见 edition.txt' -ForegroundColor Cyan
Write-Host '本次强制 WinUI：pwsh -File scripts\Run-GUI.ps1 -Edition winui' -ForegroundColor Cyan
Write-Host '回滚 ：pwsh -File scripts\Rollback-ToWpf.ps1        # 默认入口切回 WPF' -ForegroundColor Cyan
