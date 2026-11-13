#Requires -Version 7.0
<#
.SYNOPSIS
    Local update feed for verifying the update page without publishing a release.

.DESCRIPTION
    Serves three routes on 127.0.0.1 over plain HTTP (loopback only, and only because
    Performix was started with --update-feed, which is the one path that allows a loopback
    asset URL):

        /update-available.json   the "newer version, correct sha256" manifest
        /update-failed.json      the "newer version, wrong sha256" manifest
        /update-manual.json      the "newer version, minVersion above this build" manifest
                                 (optional; only served when -ManifestManualPath is given)
        /payload.zip             the package the download test pulls (throttled)
        /payload-bad.zip         a small package whose hash cannot match (throttled)

    The throttle is the point: the update page must show a moving progress bar with real
    bytes, speed and remaining time, so the transfer has to take seconds rather than
    milliseconds. Every request is appended to server.log next to the manifests.

    English only: this file is also readable on a machine without the zh-CN fonts, and it is
    launched with a hidden window by update-verify.ps1.
#>
[CmdletBinding()]
param(
    [int]$Port = 8099,
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$BadZipPath,
    [Parameter(Mandatory = $true)][string]$ManifestAvailablePath,
    [Parameter(Mandatory = $true)][string]$ManifestFailedPath,
    # Optional: the minVersion-gated manifest (newer release, minVersion above the running
    # build). Empty means the route answers 404.
    [string]$ManifestManualPath = '',
    [string]$LogPath = '',
    [int]$ChunkBytes = 65536,
    [int]$ChunkDelayMs = 30,
    [int]$IdleTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'update-server.log'
}
if (Test-Path -LiteralPath $LogPath) { Remove-Item -LiteralPath $LogPath -Force }

function Write-ServerLog([string]$Message) {
    $line = '[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $Message
    try { [System.IO.File]::AppendAllText($LogPath, $line + [Environment]::NewLine) } catch { }
}

function Send-Bytes([System.Net.HttpListenerContext]$Context, [byte[]]$Bytes, [string]$ContentType) {
    $response = $Context.Response
    $response.StatusCode = 200
    $response.ContentType = $ContentType
    $response.ContentLength64 = $Bytes.Length
    try {
        $response.OutputStream.Write($Bytes, 0, $Bytes.Length)
        $response.OutputStream.Flush()
    } catch {
        Write-ServerLog ('client went away while sending body: ' + $_.Exception.Message)
    } finally {
        $response.Close()
    }
}

# Streamed + throttled: the client sees a real multi-second transfer and reports progress.
function Send-FileSlowly([System.Net.HttpListenerContext]$Context, [string]$Path) {
    $response = $Context.Response
    try {
        $length = (Get-Item -LiteralPath $Path).Length
        $response.StatusCode = 200
        $response.ContentType = 'application/octet-stream'
        $response.ContentLength64 = $length

        $stream = [System.IO.File]::OpenRead($Path)
        try {
            $buffer = New-Object byte[] $ChunkBytes
            $sent = 0
            while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $response.OutputStream.Write($buffer, 0, $read)
                $response.OutputStream.Flush()
                $sent += $read
                Start-Sleep -Milliseconds $ChunkDelayMs
            }
            Write-ServerLog ("payload sent: $Path ($sent of $length bytes)")
        } finally {
            $stream.Dispose()
        }
    } catch {
        Write-ServerLog ('payload transfer failed: ' + $_.Exception.Message)
    } finally {
        $response.Close()
    }
}

$manifestAvailable = [System.IO.File]::ReadAllBytes($ManifestAvailablePath)
$manifestFailed = [System.IO.File]::ReadAllBytes($ManifestFailedPath)
$manifestManual = if ([string]::IsNullOrWhiteSpace($ManifestManualPath) -or
    -not (Test-Path -LiteralPath $ManifestManualPath)) { $null }
    else { [System.IO.File]::ReadAllBytes($ManifestManualPath) }

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
Write-ServerLog "listening on http://127.0.0.1:$Port/ (chunk $ChunkBytes bytes / $ChunkDelayMs ms)"

$deadline = (Get-Date).AddSeconds($IdleTimeoutSeconds)
try {
    while ((Get-Date) -lt $deadline) {
        if (-not $listener.IsListening) { break }

        $context = $null
        try { $context = $listener.GetContext() } catch { break }
        if ($null -eq $context) { continue }

        $requestPath = $context.Request.Url.AbsolutePath
        Write-ServerLog ("GET $requestPath from $($context.Request.RemoteEndPoint)")

        switch ($requestPath) {
            '/update-available.json' { Send-Bytes $context $manifestAvailable 'application/json' }
            '/update-failed.json'    { Send-Bytes $context $manifestFailed    'application/json' }
            '/update-manual.json'    {
                if ($null -ne $manifestManual) {
                    Send-Bytes $context $manifestManual 'application/json'
                } else {
                    $context.Response.StatusCode = 404
                    $context.Response.Close()
                }
            }
            '/payload.zip'           { Send-FileSlowly $context $ZipPath }
            '/payload-bad.zip'       { Send-FileSlowly $context $BadZipPath }
            default {
                $context.Response.StatusCode = 404
                $context.Response.Close()
            }
        }
    }
} finally {
    try { $listener.Stop(); $listener.Close() } catch { }
    Write-ServerLog 'stopped'
}
