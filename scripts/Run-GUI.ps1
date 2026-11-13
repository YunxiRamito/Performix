#Requires -Version 7.0
<#
.SYNOPSIS
  启动 Performix GUI（默认 WinUI 3 版；WPF 版保留为回滚路径）
.DESCRIPTION
  入口优先级：-Exe > -Edition > <repo>\edition.txt > 内置默认 winui
    winui -> dist-winui\Performix.exe（scripts\build-winui.ps1 发布）
    wpf   -> dist\Predex.exe        （scripts\build-fork.ps1 发布，WPF 回滚路径）
  WinUI 版缺失时：明确提示 + 自动回退 WPF 版（不会静默还是启动 WPF）。
  其余行为不变：
  1. 检查管理员令牌
  2. 确保 PSSvc 服务 Running（管道服务端；StartMode=Manual）
  3. 关掉官方 Predator Sense 的 UI / agent（PredatorSense.exe、PSAgent.exe），**保留 PSSvc**
  4. 启动目标 exe

  回滚 / 切回：scripts\Rollback-ToWpf.ps1（只改 edition.txt 默认值，不动 exe、不动计划任务）。
.EXAMPLE
  .\Run-GUI.ps1                 # 按默认入口启动（edition.txt，缺省 winui）
  .\Run-GUI.ps1 -Edition wpf    # 本次强制 WPF 版
  .\Run-GUI.ps1 -Force          # 不询问直接关官方进程
  .\Run-GUI.ps1 -KeepOem        # 不关官方进程（会互相抢风扇，仅调试用）
  .\Run-GUI.ps1 -Exe ..\dist-winui\Performix.exe
#>
[CmdletBinding()]
param(
    [ValidateSet('winui', 'wpf')]
    [string]$Edition = $null,
    [switch]$Force,
    [switch]$KeepOem,
    [string]$Exe = $null
)

$ErrorActionPreference = 'Stop'

$scriptsDir     = $PSScriptRoot
$repo           = Split-Path -Parent $scriptsDir
$editionMarker  = Join-Path $repo 'edition.txt'
$builtinDefault = 'winui'

function Resolve-Edition {
    param([string]$Requested)
    if ($Requested) { return [pscustomobject]@{ Edition = $Requested.ToLowerInvariant(); Source = '-Edition' } }
    if (Test-Path $editionMarker) {
        $raw = (Get-Content $editionMarker -Raw).Trim()
        if ($raw) {
            $value = $raw.ToLowerInvariant()
            if ($value -in @('winui', 'wpf')) {
                return [pscustomobject]@{ Edition = $value; Source = 'edition.txt' }
            }
            Write-Host "[edition] edition.txt 内容无效（'$raw'），忽略并按内置默认 $builtinDefault" -ForegroundColor Yellow
        }
    }
    return [pscustomobject]@{ Edition = $builtinDefault; Source = '内置默认' }
}

function Find-EditionExe {
    param([string]$Name)
    $candidates = switch ($Name) {
        'winui' { @('..\dist-winui\Performix.exe') }
        'wpf'   { @('..\dist\Predex.exe') }
        default { @() }
    }
    foreach ($candidate in $candidates) {
        $path = Join-Path $scriptsDir $candidate
        if (Test-Path $path) { return $path }
    }
    return $null
}

# 1. 管理员
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw '需要管理员权限：用管理员 PowerShell 跑这个脚本' }

# 2. PSSvc / 管道
$svc = Get-Service PSSvc -ErrorAction SilentlyContinue
if (-not $svc) { throw '找不到 PSSvc 服务 —— 官方 Predator Sense 服务组件没装？' }
if ($svc.Status -ne 'Running') {
    Write-Host '[svc] 启动 PSSvc ...' -ForegroundColor Yellow
    Start-Service PSSvc
    Start-Sleep -Milliseconds 800
}
Write-Host "[svc] PSSvc = $((Get-Service PSSvc).Status)" -ForegroundColor Green

# 3. 关掉官方 UI/agent（保留 PSSvc，否则管道没了）
if (-not $KeepOem) {
    $targets = Get-Process -Name 'PredatorSense', 'PSAgent' -ErrorAction SilentlyContinue
    if ($targets) {
        if (-not $Force) {
            $ans = Read-Host "要关掉官方 Predator Sense 进程（$($targets.ProcessName -join ', ')）吗？它们会和本程序抢风扇 (y/N)"
            if ($ans -notmatch '^[Yy]') { Write-Host '[oem] 保留官方进程，风扇控制可能互相覆盖' -ForegroundColor Yellow; $targets = @() }
        }
        foreach ($p in $targets) {
            try { $p | Stop-Process -Force -ErrorAction Stop; Write-Host "[oem] 已结束 $($p.ProcessName) ($($p.Id))" -ForegroundColor Yellow } catch { }
        }
        Start-Sleep -Milliseconds 500
    } else {
        Write-Host '[oem] 官方 Predator Sense 没在跑' -ForegroundColor Green
    }
}

# 4. 解析入口（-Exe > -Edition > edition.txt > 内置默认 winui）
if ($Exe) {
    $label = '自定义 -Exe'
    # 相对路径按仓库根解析，其次按本脚本目录
    $exeCandidates = @($Exe)
    if (-not [System.IO.Path]::IsPathRooted($Exe)) {
        $exeCandidates += (Join-Path $repo $Exe)
        $exeCandidates += (Join-Path $scriptsDir $Exe)
    }
    $hit = $exeCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $hit) { throw "找不到 -Exe 指定的 $Exe" }
    $Exe = $hit
} else {
    $resolved = Resolve-Edition -Requested $Edition
    $edition = $resolved.Edition
    $found = Find-EditionExe -Name $edition

    if ($found) {
        $Exe = $found
        $label = "$edition（$($resolved.Source)）"
    } elseif ($edition -eq 'winui') {
        Write-Host '[edition] 找不到 WinUI 版：dist-winui\Performix.exe' -ForegroundColor Yellow
        Write-Host '          先跑 scripts\build-winui.ps1 生成；本次自动回退 WPF 版' -ForegroundColor Yellow
        $fallback = Find-EditionExe -Name 'wpf'
        if (-not $fallback) { throw 'WinUI 与 WPF 版都找不到：先跑 scripts\build-winui.ps1（或 scripts\build-fork.ps1）' }
        $Exe = $fallback
        $label = 'wpf（WinUI 缺失，自动回退）'
    } else {
        throw '找不到 WPF 版 dist\Predex.exe —— 先跑 scripts\build-fork.ps1'
    }
}
$Exe = (Resolve-Path $Exe).Path
$exeDir = Split-Path $Exe

# 4b. 两版单实例 mutex 名不同（WPF: PreySenseApp_Unique_System_Mutex_999 / WinUI: PredexWinUI_Unique_Mutex_1234），
#     所以另一版不会挡住本版 —— 会双开。这里只警告，不改 mutex（兼容面，红线）。
$others = Get-Process -Name Performix, Predex -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and -not $_.Path.StartsWith($exeDir, [StringComparison]::OrdinalIgnoreCase) }
if ($others) {
    Write-Host ('[edition] 警告：另一个版本的进程正在跑（' +
        (($others | ForEach-Object { "$($_.ProcessName) $($_.Id) $($_.Path)" }) -join '; ') +
        '）。两版 mutex 名不同，会双开抢风扇，建议先退出那个。') -ForegroundColor Yellow
}

# 5. 启动
Write-Host "[edition] 本次启动：$label" -ForegroundColor Cyan
Write-Host "[run] $Exe" -ForegroundColor Cyan
Start-Process -FilePath $Exe -WorkingDirectory $exeDir
Write-Host '[run] 已启动。出问题就跑 scripts\Restore-AutoFan.ps1 归还风扇控制权。' -ForegroundColor Green
