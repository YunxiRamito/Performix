#Requires -Version 5.1
<#
.SYNOPSIS
    Applies a verified Performix update package after the running build has exited.

.DESCRIPTION
    Performix cannot overwrite its own executable, so the update path finishes here, in a
    separate process started by the app (src\Performix.App\Services\Update\UpdateInstaller.cs).

    Order of operations - every step is a hard gate:

        1. Verify the SHA-256 of the zip against the value from the manifest. A mismatch
           aborts BEFORE anything is touched.
        2. Wait for the old process to exit (at most 90 seconds). On timeout: abort, change
           nothing, keep the log. The exe is still locked, so half-copying would corrupt the
           installation.
        3. Back up the current Performix.exe into <Target>\backup\Performix.exe.<timestamp>.
        4. Unpack the zip into a staging folder and require Performix.exe inside it.
        5. Copy over the target directory file by file. Any failure keeps the staging folder,
           the backup and this log, and does NOT restart the app (an installation that might
           be half-replaced must never start silently).
        6. Start the new build and remove the staging folder.

    The script writes %LOCALAPPDATA%\Performix\logs\update.log the whole way. On failure it
    prints the backup path, which is the documented manual rollback (docs\RELEASE.md).

    ENGLISH ONLY, UTF-8 WITHOUT BOM. PowerShell 5.1 reads a BOM-less script as ANSI, so a
    single non-ASCII literal would be corrupted before it ever runs.

.PARAMETER Pid
    PID of the Performix process that started this script. Accepted through the alias because
    $PID is a read-only automatic variable in PowerShell.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File invoke-update.ps1 `
        -Zip "$env:TEMP\Performix\update\Performix-win-x64.zip" `
        -Target "C:\Program Files\Performix" -Pid 4242 -Sha256 9f2c...
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Zip,
    [Parameter(Mandatory = $true)][string]$Target,
    [Parameter(Mandatory = $true)][Alias('Pid')][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$Sha256,
    # Timeout for "wait for the running build to exit", the brief asks for 90 seconds.
    [int]$WaitSeconds = 90,
    # Verification runs must never restart the app; normal updates must.
    [switch]$NoRestart,
    # Appended to the restarted process' command line (reserved for an "updated" notification).
    [string]$RestartArguments = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:LogDirectory = Join-Path $env:LOCALAPPDATA 'Performix\logs'
$script:LogPath = Join-Path $script:LogDirectory 'update.log'
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:Timestamp = Get-Date -Format 'yyyyMMddHHmmss'

function Write-Log {
    param([string]$Message)
    $line = '[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Write-Host $line
    try {
        if (-not (Test-Path -LiteralPath $script:LogDirectory)) {
            New-Item -ItemType Directory -Path $script:LogDirectory -Force | Out-Null
        }
        [System.IO.File]::AppendAllText($script:LogPath, $line + [Environment]::NewLine, $script:Utf8NoBom)
    } catch {
        # The log must never be the reason an update fails.
    }
}

function Stop-WithError {
    param([string]$Message, [int]$Code = 1)
    Write-Log ('FAILED: ' + $Message)
    Write-Log ('Evidence kept; log: ' + $script:LogPath)
    exit $Code
}

Write-Log '====================================================================='
Write-Log ('invoke-update.ps1 start: pid=' + $PID + ' targetPid=' + $ProcessId)
Write-Log ('  zip    : ' + $Zip)
Write-Log ('  target : ' + $Target)
Write-Log ('  sha256 : ' + $Sha256)

# ---------------------------------------------------------------- 1. arguments + hash gate
if (-not (Test-Path -LiteralPath $Zip -PathType Leaf)) {
    Stop-WithError ('update package not found: ' + $Zip)
}
if (-not (Test-Path -LiteralPath $Target -PathType Container)) {
    Stop-WithError ('target directory not found: ' + $Target)
}
if ([string]::IsNullOrWhiteSpace($Sha256) -or $Sha256.Length -ne 64) {
    Stop-WithError 'the expected SHA-256 is missing or not 64 hex characters'
}

$packagePath = (Resolve-Path -LiteralPath $Zip).ProviderPath
$targetPath = (Resolve-Path -LiteralPath $Target).ProviderPath
$packageLength = (Get-Item -LiteralPath $packagePath).Length
Write-Log ('package size: ' + $packageLength + ' bytes')

Write-Log 'verifying SHA-256 ...'
$actualHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedHash = $Sha256.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    Stop-WithError ('SHA-256 mismatch: expected ' + $expectedHash + ', got ' + $actualHash)
}
Write-Log 'SHA-256 ok'

# ---------------------------------------------------------------- 2. wait for the old process
$targetExe = Join-Path $targetPath 'Performix.exe'
$deadline = (Get-Date).AddSeconds($WaitSeconds)
$waited = 0
while ($null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
    if ((Get-Date) -gt $deadline) {
        Stop-WithError ('timed out after ' + $WaitSeconds + ' s waiting for pid ' + $ProcessId + ' to exit; nothing was changed')
    }
    Start-Sleep -Milliseconds 400
    $waited += 400
}
Write-Log ('old process exited (waited ' + $waited + ' ms), the exe is no longer locked')

# ---------------------------------------------------------------- 3. backup
$backupDirectory = Join-Path $targetPath 'backup'
if (-not (Test-Path -LiteralPath $backupDirectory)) {
    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
}
$backupPath = Join-Path $backupDirectory ('Performix.exe.' + $script:Timestamp)
if (Test-Path -LiteralPath $targetExe) {
    Copy-Item -LiteralPath $targetExe -Destination $backupPath -Force
    Write-Log ('backup: ' + $backupPath)
} else {
    Write-Log 'no Performix.exe to back up (first install?); continuing'
}

# ---------------------------------------------------------------- 4. staging + package gate
$stagingPath = Join-Path $env:TEMP ('PerformixUpdate-' + $script:Timestamp)
try {
    if (Test-Path -LiteralPath $stagingPath) { Remove-Item -LiteralPath $stagingPath -Recurse -Force }
    New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
    Write-Log ('extracting to ' + $stagingPath)
    Expand-Archive -LiteralPath $packagePath -DestinationPath $stagingPath -Force
} catch {
    Stop-WithError ('could not extract the package: ' + $_.Exception.Message)
}

$stagedExe = Join-Path $stagingPath 'Performix.exe'
if (-not (Test-Path -LiteralPath $stagedExe)) {
    $found = Get-ChildItem -LiteralPath $stagingPath -Filter 'Performix.exe' -Recurse -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $found) {
        Stop-WithError 'the package does not contain Performix.exe - refusing to replace anything'
    }
    $stagingPath = $found.Directory.FullName
    $stagedExe = $found.FullName
    Write-Log ('Performix.exe found in a subfolder: ' + $stagingPath)
}

# ---------------------------------------------------------------- 5. copy over the target
$failureCount = 0
$copiedCount = 0
$runningScript = $PSCommandPath
Get-ChildItem -LiteralPath $stagingPath -Force | ForEach-Object {
    $entry = $_
    if ($entry.Name -ieq 'backup') {
        Write-Log 'skip staging\backup'
        return
    }
    if ($entry.PSIsContainer) {
        # Copy-Item -Recurse merges existing directories by default, which is what we want:
        # the package carries the new build, leftovers from the old one are harmless.
        try {
            Copy-Item -LiteralPath $entry.FullName -Destination $targetPath -Recurse -Force -ErrorAction Stop
            $copiedCount++
        } catch {
            $failureCount++
            Write-Log ('  copy FAILED: ' + $entry.Name + ' - ' + $_.Exception.Message)
        }
        return
    }

    $destination = Join-Path $targetPath $entry.Name
    try {
        Copy-Item -LiteralPath $entry.FullName -Destination $destination -Force -ErrorAction Stop
        $copiedCount++
    } catch {
        if ($runningScript -and ($destination -ieq $runningScript)) {
            # The script currently executing cannot be overwritten; the next update brings it.
            Write-Log ('  copy skipped (this script is running): ' + $destination)
        } else {
            $failureCount++
            Write-Log ('  copy FAILED: ' + $entry.Name + ' - ' + $_.Exception.Message)
        }
    }
}
Write-Log ('copied ' + $copiedCount + ' top-level entries, ' + $failureCount + ' failure(s)')

if ($failureCount -gt 0) {
    Write-Log ('staging kept for diagnosis: ' + $stagingPath)
    Write-Log ('backup kept: ' + $backupPath)
    Stop-WithError ($failureCount.ToString() + ' file(s) could not be replaced; the app was NOT restarted') 2
}

# ---------------------------------------------------------------- 6. restart
if (-not (Test-Path -LiteralPath $targetExe)) {
    Stop-WithError 'Performix.exe is missing from the target after the copy; the app was NOT restarted'
}

if ($NoRestart) {
    Write-Log 'restart suppressed (-NoRestart): verification run'
} else {
    try {
        $startParameters = @{ FilePath = $targetExe; WorkingDirectory = $targetPath; PassThru = $true }
        if (-not [string]::IsNullOrWhiteSpace($RestartArguments)) {
            $startParameters['ArgumentList'] = $RestartArguments
        }
        $started = Start-Process @startParameters
        Write-Log ('restarted: ' + $targetExe + ' pid=' + $started.Id)
    } catch {
        Write-Log ('backup for manual rollback: ' + $backupPath)
        Stop-WithError ('the new build could not be started: ' + $_.Exception.Message) 3
    }
}

try {
    Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction Stop
    Write-Log 'staging removed'
} catch {
    Write-Log ('staging could not be removed: ' + $stagingPath + ' - ' + $_.Exception.Message)
}

Write-Log 'update finished'
exit 0
