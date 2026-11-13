#Requires -Version 7.0
<#
.SYNOPSIS
  把 dist-next 覆盖到 dist（程序在跑的时候 dist 会被文件锁占用）
.DESCRIPTION
  1. 检查是否有 Performix / Predex / PerformixProbe 进程在跑；有就提示先 Quit 并退出
  2. 备份当前 dist 到 dist-prev
  3. 用 dist-next 覆盖 dist
  4. 打印版本时间戳
  目录布局：dist / dist-next / dist-prev 都在仓库根（均被 .gitignore 忽略）。
.EXAMPLE
  .\Update-Dist.ps1
  .\Update-Dist.ps1 -Force   # 自动结束正在运行的进程
#>
[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $repo 'dist'
$next = Join-Path $repo 'dist-next'
$prev = Join-Path $repo 'dist-prev'

if (-not (Test-Path $next)) { throw "找不到 $next —— 没有待发布的新版本" }
if (-not (Test-Path $dist)) { throw "找不到 $dist —— 先跑 scripts\build-fork.ps1" }

$running = Get-Process Performix, Predex, PerformixProbe -ErrorAction SilentlyContinue
if ($running) {
    if ($Force) {
        $running | ForEach-Object { $_.Kill(); "已结束 $($_.ProcessName) ($($_.Id))" }
        Start-Sleep -Seconds 2
    } else {
        throw ("程序正在运行（pid " + (($running | ForEach-Object { $_.Id }) -join ', ') +
               "），请先从托盘 Quit，或加 -Force 自动结束")
    }
}

if (Test-Path $prev) { Remove-Item $prev -Recurse -Force }
Copy-Item $dist $prev -Recurse
Copy-Item (Join-Path $next '*') $dist -Recurse -Force

Write-Host '[deploy] 已完成 dist-next -> dist' -ForegroundColor Green
Get-ChildItem $dist -Include *.exe -Recurse | Select-Object Name, LastWriteTime | Format-Table -AutoSize
