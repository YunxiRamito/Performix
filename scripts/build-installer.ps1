#Requires -Version 5.1
<#
.SYNOPSIS
  Builds the Windows x64 installer (PerformixSetup.exe) into artifacts\.

.DESCRIPTION
  One command, one artifact: publishes src\Performix.Installer self-contained, single-file and
  trimmed for win-x64, copies the result to artifacts\PerformixSetup.exe, then prints its size
  and SHA-256 (the same digest the release notes should quote).

  Why a self-contained publish instead of a plain build: the installer's whole job is to run on a
  machine that has no .NET runtime yet, so the exe must carry the runtime itself. PublishTrimmed
  keeps it near 15 MB instead of the ~70 MB an untrimmed self-contained console app produces;
  EnableCompressionInSingleFile (set in the csproj) takes another ~40% off.

  Written for Windows PowerShell 5.1 as well as PowerShell 7: `powershell -File` and
  `pwsh -File` both have to work on a build machine that may only have the inbox shell.

  SDK resolution order:
    1. $env:PERFORMIX_DOTNET
    2. <repo>\tools\dotnet-sdk\dotnet.exe
    3. the PredatorSense portable SDK at
       G:\DeepSeek DSH\DSH Works\Project\PredatorSense\tools\dotnet-sdk\dotnet.exe
       (this repository's usual build machine)
    4. whatever `dotnet` is on PATH

  Writes only src\Performix.Installer\bin\**, src\Performix.Installer\obj\** and artifacts\.

.PARAMETER Configuration
  Build configuration. Release by default - the size numbers quoted for releases come from here.

.PARAMETER OutputDirectory
  Where to put PerformixSetup.exe. Defaults to <repo>\artifacts.

.PARAMETER SkipSizeGate
  Do not fail the run when the exe exceeds the 25 MB budget (it is still printed as a warning).

.EXAMPLE
  powershell -File scripts\build-installer.ps1
.EXAMPLE
  pwsh -File scripts\build-installer.ps1 -Configuration Debug -OutputDirectory C:\tmp\setup
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputDirectory,

    [switch]$SkipSizeGate
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Chinese messages in a console that is not on code page 936 would otherwise be mojibake.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# The single-file, trimmed, self-contained budget for the installer. Past this the console-only
# design stops paying off (somebody added a framework reference it should not have).
$SizeBudgetMb = 25

$scriptsDir = $PSScriptRoot
$repoDir = Split-Path -Parent $scriptsDir
$proj = Join-Path $repoDir 'src\Performix.Installer\Performix.Installer.csproj'
$artifactDir = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $repoDir 'artifacts' }
$artifact = Join-Path $artifactDir 'PerformixSetup.exe'
$publishRoot = Join-Path $repoDir 'src\Performix.Installer\bin'

function Resolve-DotNet {
    $candidates = @()
    if ($env:PERFORMIX_DOTNET) { $candidates += $env:PERFORMIX_DOTNET }
    $candidates += (Join-Path $repoDir 'tools\dotnet-sdk\dotnet.exe')
    $candidates += 'G:\DeepSeek DSH\DSH Works\Project\PredatorSense\tools\dotnet-sdk\dotnet.exe'

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) {
        return $onPath.Source
    }

    throw @"
找不到 .NET 10 SDK。按顺序试过：
  1) 环境变量 PERFORMIX_DOTNET
  2) $(Join-Path $repoDir 'tools\dotnet-sdk\dotnet.exe')
  3) G:\DeepSeek DSH\DSH Works\Project\PredatorSense\tools\dotnet-sdk\dotnet.exe
  4) PATH 上的 dotnet
设置 PERFORMIX_DOTNET 指向 dotnet.exe 即可。
"@
}

if (-not (Test-Path -LiteralPath $proj)) {
    throw "安装器工程不存在：$proj"
}

$dotnet = Resolve-DotNet
Write-Host ""
Write-Host "PerformixSetup 构建" -ForegroundColor Cyan
Write-Host "  SDK    : $dotnet"
Write-Host "  工程   : $proj"
Write-Host "  配置   : $Configuration"
Write-Host "  输出   : $artifact"
Write-Host ""

# -m:1 / -nodeReuse:false: the same flags the repository uses everywhere else, so a build under a
# sandbox or a locked-down CI agent does not spawn persistent MSBuild nodes.
$arguments = @(
    'publish', $proj,
    '-c', $Configuration,
    '-m:1',
    '-nodeReuse:false',
    '-p:PublishSingleFile=true',
    '-p:PublishTrimmed=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:SelfContained=true',
    '-p:DebugType=none'
)

Write-Host "dotnet $($arguments -join ' ')" -ForegroundColor DarkGray
& $dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败（退出码 $LASTEXITCODE）"
}

$published = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -Filter 'PerformixSetup.exe' -File |
    Where-Object { $_.FullName -match 'publish' } |
    Sort-Object LastWriteTimeUtc -Descending)
if ($published.Count -eq 0) {
    throw "publish 报告成功，但 $publishRoot 下找不到 PerformixSetup.exe"
}

$exe = $published[0]

# A single-file publish must produce exactly one file. If it ever stops doing that, the release
# has to notice here rather than six extra dll files appearing next to the installer.
$siblings = @(Get-ChildItem -LiteralPath $exe.Directory.FullName -File |
    Where-Object { $_.Name -ne 'PerformixSetup.exe' -and $_.Extension -in '.dll', '.exe', '.json', '.pdb' })
if ($siblings.Count -gt 0) {
    Write-Warning "输出目录里除 PerformixSetup.exe 之外还有 $($siblings.Count) 个文件（单文件发布应该只有一个）：$($siblings.Name -join ', ')"
}

New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
Copy-Item -LiteralPath $exe.FullName -Destination $artifact -Force

$info = Get-Item -LiteralPath $artifact
$sizeMb = [math]::Round($info.Length / 1MB, 2)
$hash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
$sidecar = "$artifact.sha256"
[System.IO.File]::WriteAllText($sidecar, "$hash  PerformixSetup.exe`r`n", (New-Object System.Text.UTF8Encoding($false)))

Write-Host ""
Write-Host "构建完成" -ForegroundColor Green
Write-Host "  产物     : $artifact"
Write-Host "  体积     : $sizeMb MB ($($info.Length) 字节)"
Write-Host "  SHA-256  : $hash"
Write-Host "  校验文件 : $sidecar" -ForegroundColor DarkGray
Write-Host ""

if ($sizeMb -gt $SizeBudgetMb) {
    $message = "体积 $sizeMb MB 超过 $SizeBudgetMb MB 预算：检查有没有引入新的框架引用（UseWindowsForms/UseWPF 必须保持 false）。"
    if ($SkipSizeGate) {
        Write-Warning $message
    }
    else {
        throw $message
    }
}

Write-Host "  提示     : 双击运行，或先执行 $artifact --help" -ForegroundColor DarkGray
