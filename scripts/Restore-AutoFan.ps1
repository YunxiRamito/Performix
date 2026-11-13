#Requires -Version 7.0
<#
.SYNOPSIS
  紧急恢复：把风扇控制权还给固件 Auto，并（可选）关掉自制程序
.DESCRIPTION
  任何时候觉得风扇不对、程序崩了、温度异常，就跑这个。
  依赖同目录的 PSHardware.ps1（Acer WMI + PSSvc 管道，只读/短连接）。
.EXAMPLE
  .\Restore-AutoFan.ps1
  .\Restore-AutoFan.ps1 -KillApps
#>
[CmdletBinding()]
param([switch]$KillApps)

$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'PSHardware.ps1')

if ($KillApps) {
    foreach ($n in 'Performix', 'Predex', 'PerformixProbe', 'PreySense', 'PreySenseProbe') {
        Get-Process -Name $n -ErrorAction SilentlyContinue | ForEach-Object {
            try { $_.Kill(); "已结束 $n ($($_.Id))" } catch { }
        }
    }
}

try {
    $ok = Set-FanMode 'Auto'
    "SetGamingFanBehavior 0x00410009 (Auto) -> $ok"
} catch {
    "恢复失败: $($_.Exception.Message)"
}

Start-Sleep -Milliseconds 500
"当前: temp CPU=$(Get-CpuTempC)C GPU=$(Get-GpuTempC)C  fan=$(Get-CpuFanRpm)/$(Get-GpuFanRpm) RPM  mode=$(Get-PowerMode)"
