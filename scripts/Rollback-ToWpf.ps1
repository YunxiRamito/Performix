#Requires -Version 7.0
<#
.SYNOPSIS
  一条命令把默认启动入口切回 WPF 版（也支持切回 WinUI 版）
.DESCRIPTION
  只改 <repo>\edition.txt 这个标记文件里的默认值，不改 exe、不改计划任务、不改 C#：
    edition.txt = wpf   -> scripts\Run-GUI.ps1 默认启动 dist\Predex.exe（WPF 回滚路径）
    edition.txt = winui -> scripts\Run-GUI.ps1 默认启动 dist-winui\Performix.exe（新默认）

  为什么用标记文件而不是改脚本默认值：Run-GUI.ps1 是可审查的脚本，回滚要可逆、可 diff、
  不怕覆盖别人改动；edition.txt 只是一行文本，切错了再切回来就行（本脚本 -Edition winui）。

  开机自启（计划任务名沿用旧名，不能改）不在本脚本范围内：它记录的是**上次在哪个 exe 里
  关/开过开机启动**。切换入口后要到「设置 → 开机启动」关掉再打开一次。
.PARAMETER Edition
  目标默认入口：wpf（默认，= 回滚）或 winui（= 切回新版）。
.PARAMETER SyncConfig
  顺便把**另一个版本目录下**的 preysense.json 复制过来，先备份成 preysense.json.bak-<时间戳>。
  两版 exe 不在同一目录 => 各读自己目录的 preysense.json。
.PARAMETER Force
  -SyncConfig 时，即使目标版本正在运行（文件可能被锁）也尝试复制。
.EXAMPLE
  pwsh -File scripts\Rollback-ToWpf.ps1            # 默认入口切回 WPF
.EXAMPLE
  pwsh -File scripts\Rollback-ToWpf.ps1 -SyncConfig  # 切回 WPF 并把 WinUI 侧改过的配置带过来
.EXAMPLE
  pwsh -File scripts\Rollback-ToWpf.ps1 -Edition winui   # 反悔，切回 WinUI 默认
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('wpf', 'winui')]
    [string]$Edition = 'wpf',
    [switch]$SyncConfig,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$scriptsDir = $PSScriptRoot
$repo      = Split-Path -Parent $scriptsDir
$marker    = Join-Path $repo 'edition.txt'
$distDir   = Join-Path $repo 'dist'
$winuiDir  = Join-Path $repo 'dist-winui'
$distExe   = Join-Path $distDir 'Predex.exe'
$winuiExe  = Join-Path $winuiDir 'Performix.exe'

$targetDir = if ($Edition -eq 'wpf') { $distDir } else { $winuiDir }
$targetExe = if ($Edition -eq 'wpf') { $distExe } else { $winuiExe }
$otherDir  = if ($Edition -eq 'wpf') { $winuiDir } else { $distDir }
$label     = if ($Edition -eq 'wpf') { 'WPF（回滚路径）' } else { 'WinUI 3（默认新版）' }

Write-Host "[edition] 目标默认入口：$label" -ForegroundColor Cyan

# 1. 目标 exe 检查（缺了也照样写标记：让回滚动作本身永远能一条命令完成，Run-GUI 会自己回退/报错）
if (-not (Test-Path $targetExe)) {
    Write-Host "[edition] 警告：$targetExe 不存在 —— 标记照样写，但真启动会失败。" -ForegroundColor Yellow
    if ($Edition -eq 'wpf') {
        Write-Host '          先跑 scripts\build-fork.ps1 重建 dist' -ForegroundColor Yellow
    } else {
        Write-Host '          先跑 scripts\build-winui.ps1 生成 dist-winui' -ForegroundColor Yellow
    }
}

# 2. 写标记（唯一真相；Run-GUI 的解析顺序：-Exe > -Edition > edition.txt > 内置默认 winui）
if ($PSCmdlet.ShouldProcess($marker, "写入默认入口 '$Edition'")) {
    Set-Content -Path $marker -Value $Edition -Encoding utf8
    Write-Host "[edition] 已写入 $marker -> $Edition" -ForegroundColor Green
}

# 3. 可选：跨版本同步 preysense.json（两版目录不同 => 两份配置）
if ($SyncConfig) {
    $sourceCfg = Join-Path $otherDir 'preysense.json'
    $targetCfg = Join-Path $targetDir 'preysense.json'
    if (-not (Test-Path $sourceCfg)) {
        Write-Host "[config] 源配置不存在，跳过同步：$sourceCfg" -ForegroundColor Yellow
    } elseif (-not (Test-Path $targetDir)) {
        Write-Host "[config] 目标目录不存在，跳过同步：$targetDir" -ForegroundColor Yellow
    } else {
        $targetRunning = Get-Process -Name Performix, Predex -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($targetDir, [StringComparison]::OrdinalIgnoreCase) }
        if ($targetRunning -and -not $Force) {
            Write-Host ('[config] 目标版本正在运行（pid ' + (($targetRunning | ForEach-Object { $_.Id }) -join ', ') +
                        '），跳过同步；退出后用 -Force 或手动复制') -ForegroundColor Yellow
        } else {
            $same = $false
            if (Test-Path $targetCfg) {
                try {
                    $same = ([System.IO.File]::ReadAllText($sourceCfg) -eq [System.IO.File]::ReadAllText($targetCfg))
                } catch { $same = $false }
            }
            if ($same) {
                Write-Host '[config] 两边 preysense.json 内容一致，不用复制' -ForegroundColor Green
            } elseif ($PSCmdlet.ShouldProcess($targetCfg, "从 $sourceCfg 复制配置")) {
                if (Test-Path $targetCfg) {
                    $backup = "$targetCfg.bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
                    Copy-Item $targetCfg $backup -Force
                    Write-Host "[config] 已备份 -> $backup" -ForegroundColor DarkGray
                }
                Copy-Item $sourceCfg $targetCfg -Force
                Write-Host "[config] 已同步 $sourceCfg -> $targetCfg" -ForegroundColor Green
            }
        }
    }
}

# 4. 收尾提示
Write-Host ''
Write-Host '========== 默认入口已切 ==========' -ForegroundColor Green
Write-Host "edition.txt : $(if (Test-Path $marker) { (Get-Content $marker -Raw).Trim() } else { '<缺失>' })"
Write-Host "默认目标    : $targetExe（$(if (Test-Path $targetExe) { '在位' } else { '缺失' })）"
Write-Host ''
Write-Host '下一步：' -ForegroundColor Cyan
Write-Host '  1) 启动：pwsh -File scripts\Run-GUI.ps1'
Write-Host '  2) 开机自启仍指向「上次开关过的那版」：进设置把「开机启动」关掉再打开一次才会跟着切'
Write-Host '     （计划任务名沿用旧名保持不变）'
Write-Host '  3) 反悔：pwsh -File scripts\Rollback-ToWpf.ps1 -Edition winui'
