#Requires -Version 7.0
<#
.SYNOPSIS
  PH317-55 硬件访问层：Acer WMI（风扇/模式）+ PSAgent 管道（温度/RPM）。零内核驱动。
.DESCRIPTION
  被 FanCurve.ps1 dot-source。所有函数都是幂等的短连接，不留悬挂句柄。

  Acer WMI（root\WMI AcerGamingFunction，已实测）：
    SetGamingFanBehavior  0x00410009 Auto / 0x00820009 Max / 0x00C30009 Custom
    SetGamingFanSpeed     (dutyPercent << 8) | fanId      fanId 1=CPU 4=GPU
    SetGamingMiscSetting  0x0B | (mode << 8)
    GetGamingMiscSetting  0x0B  -> mode 在 bits 8..15

  PSAgent 管道（\\.\pipe\predatorsense_service_namedpipe，服务 PSSvc 必须 Running）：
    cmd 15, arg = 1 | (id << 8)
    id 1=CPU温度 2=CPU风扇RPM 3=系统温度 6=GPU风扇RPM 10=GPU温度
    返回 u64: status = raw & 0xFF (0=OK), value = (raw >> 8) & 0xFFFF（无缩放）
#>

$script:CFG = @{
    PipeName          = 'predatorsense_service_namedpipe'
    CmdGetSysInfo     = 15
    CpuTemp           = 1
    CpuFanRpm         = 2
    SystemTemp        = 3
    GpuFanRpm         = 6
    GpuTemp           = 10
    FanCpu            = 1
    FanGpu            = 4
    BehaviorAuto      = [uint64]0x00410009
    BehaviorMax       = [uint64]0x00820009
    BehaviorCustom    = [uint64]0x00C30009
    ConnectTimeoutMs  = 2000
}

# ---------------- WMI ----------------
$script:Wmi = $null
function Get-AcerWmi {
    if (-not $script:Wmi) {
        $script:Wmi = Get-CimInstance -Namespace 'root\WMI' -ClassName 'AcerGamingFunction' -ErrorAction Stop
        if (-not $script:Wmi) { throw '找不到 root\WMI AcerGamingFunction' }
    }
    return $script:Wmi
}

function Invoke-AcerWmi([string]$method, [uint64]$value) {
    $w = Get-AcerWmi
    Invoke-CimMethod -InputObject $w -MethodName $method -Arguments @{ gmInput = $value }
}

function Set-FanMode([ValidateSet('Auto', 'Max', 'Custom')][string]$mode) {
    $payload = switch ($mode) {
        'Auto' { $script:CFG.BehaviorAuto }
        'Max' { $script:CFG.BehaviorMax }
        'Custom' { $script:CFG.BehaviorCustom }
    }
    (Invoke-AcerWmi 'SetGamingFanBehavior' $payload).ReturnValue -eq $true
}

function Set-FanDuty([int]$fanId, [int]$percent) {
    $pct = [Math]::Max(0, [Math]::Min(100, $percent))
    $payload = [uint64](($pct -shl 8) -bor $fanId)
    (Invoke-AcerWmi 'SetGamingFanSpeed' $payload).ReturnValue -eq $true
}

function Get-PowerMode {
    $r = Invoke-AcerWmi 'GetGamingMiscSetting' ([uint64]0x0B)
    if (-not $r.ReturnValue) { return $null }
    ([uint64]$r.gmOutput -shr 8) -band 0xFF
}

function Set-PowerMode([int]$mode) {
    $r = Invoke-AcerWmi 'SetGamingMiscSetting' ([uint64](0x0B -bor ($mode -shl 8)))
    $r.ReturnValue -eq $true
}

# ---------------- 管道 ----------------
function Read-ExactBytes([System.IO.Stream]$Stream, [int]$Count) {
    $buf = [byte[]]::new($Count); $got = 0
    while ($got -lt $Count) {
        $n = $Stream.Read($buf, $got, $Count - $got)
        if ($n -le 0) { throw "pipe closed after $got/$Count bytes" }
        $got += $n
    }
    return , $buf
}

function Invoke-PipeCmd([int]$cmd, [object[]]$arguments) {
    $ms = [System.IO.MemoryStream]::new(); $bw = [System.IO.BinaryWriter]::new($ms)
    try {
        $bw.Write([int16]$cmd)
        $bw.Write([byte]$arguments.Count)
        foreach ($a in $arguments) {
            # 必须显式 [byte[]] 强转：if 表达式会把 byte[] 退化成 object[]，BinaryWriter 只写出 1 字节
            [byte[]]$bytes = if ($a -is [byte[]]) { $a } else { [BitConverter]::GetBytes($a) }
            $bw.Write([int32]$bytes.Length)
            if ($bytes.Length) { $bw.Write($bytes) }
        }
        $bw.Flush(); $frame = $ms.ToArray()
    } finally { $bw.Dispose(); $ms.Dispose() }

    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $script:CFG.PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect($script:CFG.ConnectTimeoutMs)
        $pipe.Write($frame, 0, $frame.Length); $pipe.Flush()
        $hdr = Read-ExactBytes $pipe 5
        $numArgs = [int]$hdr[0]
        $len = [BitConverter]::ToInt32($hdr, 1)
        if ($len -lt 0 -or $len -gt 1MB) { throw "bogus response length $len" }
        $payload = if ($len -gt 0) { Read-ExactBytes $pipe $len } else { [byte[]]::new(0) }
        return , $payload
    } finally { try { $pipe.Dispose() } catch { } }
}

function Get-SensorValue([int]$sensorId) {
    <# 返回 int 或 $null（status != 0 表示该传感器本机无数据） #>
    try {
        $arg = [uint32](1 -bor ($sensorId -shl 8))
        $payload = Invoke-PipeCmd $script:CFG.CmdGetSysInfo @($arg)
        if ($payload.Length -lt 8) { return $null }
        $raw = [BitConverter]::ToUInt64($payload, 0)
        if (($raw -band 0xFF) -ne 0) { return $null }
        return [int](($raw -shr 8) -band 0xFFFF)
    } catch { return $null }
}

function Test-PipeAvailable {
    $null -ne (Get-SensorValue $script:CFG.CpuTemp)
}

function Get-CpuTempC { Get-SensorValue $script:CFG.CpuTemp }
function Get-GpuTempC { Get-SensorValue $script:CFG.GpuTemp }
function Get-CpuFanRpm { Get-SensorValue $script:CFG.CpuFanRpm }
function Get-GpuFanRpm { Get-SensorValue $script:CFG.GpuFanRpm }


