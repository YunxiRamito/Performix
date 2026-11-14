#Requires -Version 7.0
<#
.SYNOPSIS
    Update-page verification: four screenshots from a local fake feed, plus the log evidence.

.DESCRIPTION
    pwsh -NoProfile -File src\Performix.App\docs\update-verify.ps1 -Exe "<...>\Performix.exe"

    Produces (repo docs\):
        update-idle.png         the page before any check ("Not checked" / 未检查)
        update-available.png    after --update-auto=check: 有新版 + target version/size + notes
        update-downloading.png  mid-transfer against a throttled local server:
                                percent + determinate bar, bytes, speed, remaining time
        update-failed.png       the same flow against a manifest with a wrong sha256:
                                更新失败 + one-sentence reason + Retry / Switch source and retry
        update-manual-install.png  a manifest whose minVersion is above this build: the status
                                line says 需手动安装, the body carries the block reason and the
                                download button stays visible but DISABLED (no accent fill on the
                                button; the page's "Automatic updates" switch is the only accent
                                source left, which is why that shot is asserted with a ceiling,
                                see the assertion below).

    THE SCREENSHOT IS ASSERTED, NOT TRUSTED. PrintWindow cannot capture the Mica / acrylic
    composition surface, which is why the same build used to produce three different page
    backgrounds. The script therefore:
      1. pins the appearance to Light + Solid + the fixed accent (backing up and restoring
         %LOCALAPPDATA%\Predex\appearance.json), so all four states share ONE theme and the
         window paints an opaque, deterministic surface instead of a backdrop;
      2. records the page-background pixel and the accent-pixel count of every shot, then
         fails the run if the four backgrounds differ or a shot contains no accent pixels;
      3. fails the run unless shell.log confirms theme=Light / material=Solid.

    TWO CAPTURE PATHS. Save-Shot tries PrintWindow(PW_RENDERFULLCONTENT) first and falls back to
    BitBlt from the screen DC, which is what the compositor actually put on the display. That
    fallback is the documented answer to a window whose content lives in a Composition content
    island (PrintWindow returns the bare window surface while the real pixels are on screen).
    Windows.Graphics.Capture would read the composition layer directly, but it needs a Direct3D
    interop host in PowerShell; the two GDI paths plus the theme pin cover both cases this script
    has actually met. When BOTH paths are blank the run says so explicitly: that is the app not
    presenting anything, and no capture technique can recover it.

    FAILING RUNS MUST NOT DESTROY EVIDENCE. Every capture is written to a staging directory under
    %TEMP% first; docs\*.png is only written after all assertions passed, and a capture whose ink
    is below the blank floor is refused at that gate. A red run therefore leaves the committed
    screenshots alone and prints the staging path for diagnosis - it used to overwrite docs\*.png
    with blank white images, which is worse than no run at all.

    The feed is served by update-test-server.ps1 on 127.0.0.1, and the app is launched with
    --update-feed=... — that switch also suppresses installing (App.OnLaunched), so nothing
    here can replace the real Performix.exe. No release is published for this: the manifests,
    the package and the throttling all live in %TEMP%.

    Read-only with respect to hardware: only navigation rows and the two update buttons are
    touched. Still run scripts\Restore-AutoFan.ps1 afterwards (real-machine discipline).

    Window enumeration / PrintWindow / UIA primitives are the same ones docs\s1-verify.ps1 …
    docs\ia-verify.ps1 used (EnumWindows by PID, PrintWindow PW_RENDERFULLCONTENT + a non-blank
    ink check, UIA AutomationId clicks).
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$OutDir = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path 'docs'),
    [int]$Port = 8099,
    [int]$PayloadMegabytes = 32,
    [int]$ChunkDelayMs = 40,
    # The theme the run pins. Light is the shipping screenshot set; Dark exists so the same
    # assertions can prove the dark palette (and it must be passed explicitly, never inherited).
    [ValidateSet('Light', 'Dark')][string]$Theme = 'Light'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class UpdV
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);

    public static void MakeDpiAware() { try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } }

    public sealed class WinInfo
    {
        public IntPtr Handle;
        public string ClassName = "";
        public string Title = "";
        public bool Visible;
    }

    public static List<WinInfo> WindowsOf(uint processId)
    {
        var found = new List<WinInfo>();
        EnumWindows((h, l) =>
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != processId) return true;
            var cls = new StringBuilder(256); GetClassNameW(h, cls, cls.Capacity);
            var txt = new StringBuilder(512); GetWindowTextW(h, txt, txt.Capacity);
            found.Add(new WinInfo { Handle = h, ClassName = cls.ToString(), Title = txt.ToString(), Visible = IsWindowVisible(h) });
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool PrintTo(IntPtr hwnd, IntPtr hdc) => PrintWindow(hwnd, hdc, 2);

    /// <summary>Screen pixels of the window rect, the fallback capture path.</summary>
    public static bool BitBltFromScreen(IntPtr hdc, int x, int y, int w, int h)
    {
        IntPtr src = GetDC(IntPtr.Zero);
        if (src == IntPtr.Zero) return false;
        try { return BitBlt(hdc, 0, 0, w, h, src, x, y, 0x00CC0020); }
        finally { ReleaseDC(IntPtr.Zero, src); }
    }

    public static int[] RectOf(IntPtr h)
    {
        RECT r;
        if (!GetWindowRect(h, out r)) return new int[0];
        return new[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }

    public static int DpiOf(IntPtr h) { return (int)GetDpiForWindow(h); }
}
'@

[UpdV]::MakeDpiAware()

$serverScript = Join-Path $PSScriptRoot 'update-test-server.ps1'
$workRoot = Join-Path $env:TEMP ('PerformixUpdateVerify-' + (Get-Date -Format 'yyyyMMddHHmmss'))
# Screenshots are written HERE first and only copied into $OutDir after every assertion passed.
# A failing run used to overwrite the committed docs\*.png with blank captures, i.e. it destroyed
# the very evidence it was supposed to protect. Now a failure leaves docs\ untouched and points
# at this directory instead.
$shotStaging = Join-Path $workRoot 'shots'
$updateTemp = Join-Path $env:TEMP 'Performix\update'
$serverLog = Join-Path $workRoot 'update-server.log'
$shellLog = Join-Path (Split-Path -Parent (Resolve-Path $Exe).Path) 'shell.log'
$serverProcess = $null

# Pinned appearance: one theme for all four states, and an opaque surface PrintWindow can
# actually capture. Light + Solid + the fixed accent is written to the same file the app reads
# (AppearanceService.LoadPersisted); the original is restored in the finally block.
$appearancePath = Join-Path $env:LOCALAPPDATA 'Predex\appearance.json'
$appearanceBackup = $null
# AppearanceService.SetSurface: the Light / Dark solid fallback.
$pinnedBackground = if ($Theme -eq 'Dark') { '#202020' } else { '#F3F3F3' }
$accentRgb = @(0x3B, 0x82, 0xF6) # the fixed accent every button / progress fill uses

function Set-PinnedAppearance {
    if (Test-Path -LiteralPath $appearancePath) {
        $script:appearanceBackup = Get-Content -LiteralPath $appearancePath -Raw
    } else {
        $script:appearanceBackup = $null
    }
    $dir = Split-Path -Parent $appearancePath
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $pinned = [ordered]@{
        Theme       = $Theme
        Material    = 'Solid'
        WindowStyle = 'Windows11'
        AccentMode  = 'Fixed'
        AccentColor = '#3B82F6'
    }
    [System.IO.File]::WriteAllText($appearancePath, ($pinned | ConvertTo-Json),
        (New-Object System.Text.UTF8Encoding($false)))
    Zz "appearance pinned: $Theme / Solid / #3B82F6 ($appearancePath)"
}

function Restore-Appearance {
    try {
        if ($null -ne $script:appearanceBackup) {
            [System.IO.File]::WriteAllText($appearancePath, $script:appearanceBackup,
                (New-Object System.Text.UTF8Encoding($false)))
            Zz 'appearance restored'
        } elseif (Test-Path -LiteralPath $appearancePath) {
            Remove-Item -LiteralPath $appearancePath -Force
            Zz 'appearance.json removed (it did not exist before the run)'
        }
    } catch {
        Write-Warning "could not restore appearance.json: $($_.Exception.Message)"
    }
}

# Per-shot evidence collected by Save-Shot and asserted at the end of the run.
$script:shots = [System.Collections.Generic.List[object]]::new()
# The blank-capture floor: a real page is well above 0.5% ink; a PrintWindow that returned
# nothing is 0.0%. Same number for the assertion and for the publish gate.
$script:minInk = 0.005
# Set by Save-Shot when the screen path DID show pixels even though neither path produced an
# acceptable (app-surface) image. It separates "the capture path is blind" from "the window is
# painting its own surface and nothing else", which are different bugs.
$script:screenShowedSomething = $false

function Write-Step([string]$text) { Write-Output "== $text" }
function Zz([string]$text) { Write-Output "   $text" }

if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
if (-not (Test-Path -LiteralPath $workRoot)) { New-Item -ItemType Directory -Path $workRoot -Force | Out-Null }
if (-not (Test-Path -LiteralPath $shotStaging)) { New-Item -ItemType Directory -Path $shotStaging -Force | Out-Null }

# ── Helpers (same primitives as ia-verify.ps1) ────────────────────────────────────────────
function Get-ShellWindow([int]$processId, [int]$tries = 80) {
    for ($i = 0; $i -lt $tries; $i++) {
        foreach ($w in [UpdV]::WindowsOf([uint32]$processId)) {
            if ($w.ClassName -eq 'WinUIDesktopWin32WindowClass' -and $w.Title -eq 'Performix') { return $w }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Measure-Ink([System.Drawing.Bitmap]$bmp) {
    # Reference colour = the MODAL colour, not pixel (1,1). A DWM-rounded window has a
    # transparent/black corner, so (1,1) is often not the page colour at all: with that pixel as
    # the reference a perfectly blank window measured 100% "ink" and a real page could measure
    # low. The most common colour is the page surface in every state this script captures.
    $counts = @{}
    for ($y = 0; $y -lt $bmp.Height; $y += 6) {
        for ($x = 0; $x -lt $bmp.Width; $x += 6) {
            $c = $bmp.GetPixel($x, $y)
            $key = ([int]$c.R -shl 16) -bor ([int]$c.G -shl 8) -bor [int]$c.B
            if ($counts.ContainsKey($key)) { $counts[$key]++ } else { $counts[$key] = 1 }
        }
    }
    if ($counts.Count -eq 0) { return 0 }
    $modal = ($counts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1).Key
    $refR = ($modal -shr 16) -band 0xFF; $refG = ($modal -shr 8) -band 0xFF; $refB = $modal -band 0xFF

    $differing = 0; $sampled = 0
    for ($y = 0; $y -lt $bmp.Height; $y += 3) {
        for ($x = 0; $x -lt $bmp.Width; $x += 3) {
            $sampled++
            $c = $bmp.GetPixel($x, $y)
            if ([Math]::Abs($c.R - $refR) + [Math]::Abs($c.G - $refG) + [Math]::Abs($c.B - $refB) -gt 12) { $differing++ }
        }
    }
    if ($sampled -eq 0) { return 0 }
    return $differing / $sampled
}

function Measure-Accent([System.Drawing.Bitmap]$bmp) {
    # Counts accent-coloured pixels (the primary button fill and the progress fill are the two
    # large accent areas). Tolerant to the 1 px antialiasing edge, not to a grey accent.
    # Full-pixel scan (not the 3 px ink grid): the download bar is only 6 DIP high, so a coarse
    # grid can sample it into single digits and turn a present accent into a false failure.
    $count = 0
    for ($y = 0; $y -lt $bmp.Height; $y += 1) {
        for ($x = 0; $x -lt $bmp.Width; $x += 1) {
            $c = $bmp.GetPixel($x, $y)
            if ([Math]::Abs($c.R - $accentRgb[0]) -le 20 -and
                [Math]::Abs($c.G - $accentRgb[1]) -le 20 -and
                [Math]::Abs($c.B - $accentRgb[2]) -le 20) { $count++ }
        }
    }
    return $count
}

function Get-PageBackground([System.Drawing.Bitmap]$bmp, [double]$scale) {
    # The page's own surface: the right-hand 12 DIP padding strip, below the title bar. Cards
    # live between x = 12 DIP and width - 12 DIP, so this point is never inside a card.
    $x = $bmp.Width - [int](6 * $scale) - 1
    $y = [int](120 * $scale)
    $x = [Math]::Min([Math]::Max($x, 1), $bmp.Width - 2)
    $y = [Math]::Min([Math]::Max($y, 1), $bmp.Height - 2)
    return $bmp.GetPixel($x, $y)
}

function Save-Shot([IntPtr]$handle, [string]$name) {
    $rect = [UpdV]::RectOf($handle)
    if ($rect.Length -eq 0) { Zz "shot $name FAILED (no rect)"; return }

    # TWO capture paths, in order of fidelity:
    #   1. PrintWindow(PW_RENDERFULLCONTENT) — the window's own redirection surface;
    #   2. BitBlt from the screen DC — what the compositor actually put on the display.
    # Path 2 is the documented fallback for a window whose content lives in a Composition
    # content island: PrintWindow can return the bare window surface while the real pixels are
    # on the screen. A screen capture is only accepted when it still shows the app's own pinned
    # surface — otherwise it captured the desktop behind an un-painted window, which looks like
    # "lots of ink" while containing nothing of the app.
    # The capture that is KEPT for diagnosis when neither path produced an acceptable image:
    # the window's own surface (PrintWindow), because that is the one that describes the window.
    $bmp = $null; $ok = $false; $pathUsed = 'PrintWindow'; $accepted = $false
    $first = $null; $firstOk = $false; $screenInk = 0.0
    foreach ($mode in 'PrintWindow', 'BitBlt') {
        $candidate = New-Object System.Drawing.Bitmap($rect[2], $rect[3])
        $g = [System.Drawing.Graphics]::FromImage($candidate)
        $hdc = $g.GetHdc()
        $ok = if ($mode -eq 'PrintWindow') {
            [UpdV]::PrintTo($handle, $hdc)
        } else {
            [UpdV]::BitBltFromScreen($hdc, $rect[0], $rect[1], $rect[2], $rect[3])
        }
        $g.ReleaseHdc($hdc)
        $g.Dispose()

        $candidateScale = [UpdV]::DpiOf($handle) / 96.0
        $candidateBackground = Get-PageBackground $candidate $candidateScale
        $candidateHex = ('#{0:X2}{1:X2}{2:X2}' -f $candidateBackground.R, $candidateBackground.G, $candidateBackground.B)
        $candidateInk = Measure-Ink $candidate

        if ($mode -eq 'PrintWindow') {
            $first = $candidate; $firstOk = $ok
        } else {
            $screenInk = $candidateInk
            if ($null -ne $bmp) { $bmp.Dispose() }
        }

        $bmp = $candidate
        $pathUsed = $mode
        if ($candidateInk -ge $script:minInk -and $candidateHex -eq $pinnedBackground) {
            $accepted = $true
            break
        }
        if ($mode -eq 'PrintWindow') { $bmp = $null }   # $first holds it; do not double-dispose
    }
    if (-not $accepted) {
        # Neither path saw the app's own surface: keep the window-surface capture and say so.
        if ($null -ne $bmp) { $bmp.Dispose() }
        $bmp = $first
        $ok = $firstOk
        $pathUsed = 'PrintWindow'
        if ($screenInk -ge $script:minInk) { $script:screenShowedSomething = $true }
    } elseif ($null -ne $first -and -not [object]::ReferenceEquals($first, $bmp)) {
        $first.Dispose()
    }

    $ink = Measure-Ink $bmp
    $scale = [UpdV]::DpiOf($handle) / 96.0
    $background = Get-PageBackground $bmp $scale
    $accent = Measure-Accent $bmp
    # Staging, never $OutDir: the committed docs\*.png must survive a failed run.
    $path = Join-Path $shotStaging $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    $script:shots.Add([pscustomobject]@{
        Name       = $name
        Path       = $path
        Background = ('#{0:X2}{1:X2}{2:X2}' -f $background.R, $background.G, $background.B)
        Accent     = $accent
        Ink        = $ink
        Capture    = $pathUsed
    })

    Zz ("shot {0} {1}x{2} physical = {3:N0}x{4:N0} DIP ink={5:P1} bg={6} accentPx={7} {8}={9} → {10}" -f `
        $name, $rect[2], $rect[3], ($rect[2] / $scale), ($rect[3] / $scale), $ink,
        ('#{0:X2}{1:X2}{2:X2}' -f $background.R, $background.G, $background.B), $accent,
        $pathUsed, $ok, $path)
}

# The only place that writes $OutDir. Runs after every assertion passed, so a red run cannot
# leave a blank capture in docs\ where it reads as "the UI really looks like that".
function Publish-Shots {
    foreach ($shot in $script:shots) {
        $target = Join-Path $OutDir $shot.Name
        if ($shot.Ink -lt $script:minInk) {
            throw ("refusing to write {0}: ink {1:P1} is below the {2:P1} floor (blank capture)" -f `
                $target, $shot.Ink, $script:minInk)
        }
        Copy-Item -LiteralPath $shot.Path -Destination $target -Force
        Zz "published $($shot.Name) → $target"
    }
}

function Find-Ui([IntPtr]$root, [string]$automationId) {
    $element = [System.Windows.Automation.AutomationElement]::FromHandle($root)
    if ($null -eq $element) { return $null }
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    return $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-Ui([IntPtr]$root, [string]$automationId, [int]$timeoutMs = 15000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $target = Find-Ui $root $automationId
        if ($null -ne $target) { return $target }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Invoke-Ui([IntPtr]$root, [string]$automationId, [int]$timeoutMs = 15000) {
    $target = Wait-Ui $root $automationId $timeoutMs
    if ($null -eq $target) {
        Zz "UIA: $automationId NOT FOUND"
        return $false
    }
    $pattern = $null
    if ($target.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
        Zz "UIA: invoked $automationId"
        return $true
    }
    Zz "UIA: $automationId has no invoke pattern"
    return $false
}

function Read-UiName([IntPtr]$root, [string]$automationId) {
    $target = Find-Ui $root $automationId
    if ($null -eq $target) { return $null }
    return $target.Current.Name
}

function Wait-UiState([IntPtr]$root, [string[]]$expected, [int]$timeoutMs = 30000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $last = Read-UiName $root 'TxtUpdateState'
        if ($last -and ($expected -contains $last)) { return $last }
        Start-Sleep -Milliseconds 250
    }
    return $last
}

function Start-App([string]$manifestName, [string]$autoMode) {
    Get-Process Performix -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 700
    if (Test-Path -LiteralPath $updateTemp) { Remove-Item -LiteralPath $updateTemp -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $shellLog) { Remove-Item -LiteralPath $shellLog -Force }

    $work = Split-Path -Parent (Resolve-Path $Exe).Path
    $feed = "http://127.0.0.1:$Port/$manifestName"
    $launchArgs = @('--no-autohide', '--trace', "--update-feed=$feed", '--update-no-install', '--update-no-auto')
    if ($autoMode) { $launchArgs += "--update-auto=$autoMode" }
    Zz ("launch: {0} {1}" -f (Split-Path -Leaf $Exe), ($launchArgs -join ' '))
    $proc = Start-Process -FilePath $Exe -WorkingDirectory $work -ArgumentList $launchArgs -PassThru
    $win = Get-ShellWindow -processId $proc.Id
    if ($null -eq $win) { throw "shell window not found (pid $($proc.Id))" }
    Zz ("pid={0} hwnd=0x{1:X} visible={2}" -f $proc.Id, $win.Handle.ToInt64(), $win.Visible)
    return [pscustomobject]@{ Proc = $proc; Win = $win; Feed = $feed }
}

# Expands the shell, opens Settings, then the Updates and About page. Retried because the first
# UIA invoke can land while the WinUI tree is still being built (the shell is a tray panel that
# is created hidden and then shown).
function Open-UpdatePage([IntPtr]$hwnd) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        if ($null -ne (Find-Ui $hwnd 'TxtUpdateState')) {
            Zz "update page is open (attempt $attempt)"
            return $true
        }
        $expanded = Invoke-Ui $hwnd 'BtnExpand' 4000
        Start-Sleep -Milliseconds 900
        $settings = Invoke-Ui $hwnd 'OpenSettings' 8000
        Start-Sleep -Milliseconds 1300
        $meta = Invoke-Ui $hwnd 'BtnSettingsUpdate' 8000
        Start-Sleep -Milliseconds 1500
        Zz "nav attempt ${attempt}: expand=$expanded settings=$settings meta=$meta"
        if ($null -ne (Find-Ui $hwnd 'TxtUpdateState')) {
            Zz "update page is open (attempt $attempt)"
            return $true
        }
    }
    return $false
}

function Stop-App {
    Get-Process Performix -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 700
}

# ── 0. Fixtures: one package with the right hash, one with a wrong hash ───────────────────
Write-Step 'building the fake update packages'

function New-PayloadZip([string]$path, [int]$bytes) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    $payload = New-Object byte[] $bytes
    ([System.Random]::new(20261114)).NextBytes($payload)
    $file = [System.IO.File]::Open($path, [System.IO.FileMode]::Create)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($file, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            $entry = $archive.CreateEntry('Performix.exe', [System.IO.Compression.CompressionLevel]::NoCompression)
            $stream = $entry.Open()
            $stream.Write($payload, 0, $payload.Length)
            $stream.Dispose()
            $readme = [System.Text.Encoding]::ASCII.GetBytes('Performix update package (verification fixture)')
            $entry2 = $archive.CreateEntry('scripts/invoke-update.ps1', [System.IO.Compression.CompressionLevel]::NoCompression)
            $stream2 = $entry2.Open()
            $stream2.Write($readme, 0, $readme.Length)
            $stream2.Dispose()
        } finally {
            $archive.Dispose()
        }
    } finally {
        $file.Dispose()
    }
}

$payloadPath = Join-Path $workRoot 'payload.zip'
$badPayloadPath = Join-Path $workRoot 'payload-bad.zip'
New-PayloadZip $payloadPath ($PayloadMegabytes * 1024 * 1024)
New-PayloadZip $badPayloadPath (2 * 1024 * 1024)

$payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
$payloadSize = (Get-Item -LiteralPath $payloadPath).Length
$badPayloadSize = (Get-Item -LiteralPath $badPayloadPath).Length
$wrongHash = '0' * 64
Zz ("payload: {0} bytes sha256={1}" -f $payloadSize, $payloadHash)
Zz ("bad payload: {0} bytes (the manifest will publish a wrong sha256)" -f $badPayloadSize)

# The fixture versions are DERIVED FROM THE EXE UNDER TEST, not hard-coded.
#
# They used to be the literals 0.1.0-alpha.2 / .3 / .4 with minVersion 0.1.0-alpha.1, which
# silently turned into "已是最新" (and made the minVersion gate untestable) the moment the build
# under test was bumped to the same version as the "newer" manifest. Reading the running version
# and stepping past it keeps this script honest across every release bump.
$runningVersion = $null
try {
    $runningVersion = (Get-Item -LiteralPath (Resolve-Path $Exe).Path).VersionInfo.ProductVersion
} catch {
    $runningVersion = $null
}
if ($runningVersion) { $runningVersion = ($runningVersion -split '\+')[0].Trim() }

function Step-Version([string]$version, [int]$steps) {
    # "0.1.0-alpha.2" + 1 → "0.1.0-alpha.3"; a stable "0.1.0" + 1 → "0.1.1".
    if ($version -match '^(?<core>\d+(?:\.\d+){0,2})(?:-(?<pre>[0-9A-Za-z.\-]+))?$') {
        $core = $Matches['core']
        $pre = $Matches['pre']
        if ($pre) {
            if ($pre -match '^(?<label>.*?)(?<n>\d+)$') {
                return ('{0}-{1}{2}' -f $core, $Matches['label'], ([int]$Matches['n'] + $steps))
            }
            return ('{0}-{1}.{2}' -f $core, $pre, $steps)
        }
        $parts = $core.Split('.')
        while ($parts.Count -lt 3) { $parts += '0' }
        $parts[2] = [string]([int]$parts[2] + $steps)
        return ($parts -join '.')
    }
    return $null
}

if (-not $runningVersion -or -not (Step-Version $runningVersion 1)) {
    throw "could not read a semantic version from $Exe (got '$runningVersion'): the update fixtures need to be newer than the build under test"
}

$availableVersion = Step-Version $runningVersion 1
$failedVersion = Step-Version $runningVersion 2
$manualVersion = Step-Version $runningVersion 3
# The minVersion gate needs a floor ABOVE the build under test, so RequiresManualInstall fires.
$manualMinVersion = Step-Version $runningVersion 2

Zz ("build under test: {0} → fixtures {1} / {2} / {3} (manual minVersion {4})" -f `
    $runningVersion, $availableVersion, $failedVersion, $manualVersion, $manualMinVersion)

$publishedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$notes = "Update page verification build.`r`n- real state machine, determinate progress`r`n- source shown from the manifest hit`r`n- notes area scrolls past 240 DIP"

function New-Manifest([string]$assetFile, [long]$size, [string]$sha, [string]$manifestVersion,
    [string]$minVersion = $runningVersion) {
    $manifest = [ordered]@{
        schema      = 1
        channel     = 'alpha'
        version     = $manifestVersion
        publishedAt = $publishedAt
        notes       = $notes
        minVersion  = $minVersion
        assets      = @([ordered]@{
            name      = 'Performix-win-x64.zip'
            url       = "http://127.0.0.1:$Port/$assetFile"
            sizeBytes = $size
            sha256    = $sha
        })
    }
    return ($manifest | ConvertTo-Json -Depth 6)
}

$availableManifestPath = Join-Path $workRoot 'update-available.json'
$failedManifestPath = Join-Path $workRoot 'update-failed.json'
$manualManifestPath = Join-Path $workRoot 'update-manual.json'
[System.IO.File]::WriteAllText($availableManifestPath, (New-Manifest 'payload.zip' $payloadSize $payloadHash $availableVersion),
    (New-Object System.Text.UTF8Encoding($false)))
[System.IO.File]::WriteAllText($failedManifestPath, (New-Manifest 'payload-bad.zip' $badPayloadSize $wrongHash $failedVersion),
    (New-Object System.Text.UTF8Encoding($false)))
# The minVersion gate: a newer release whose minVersion is above the build under test, so the
# state machine reports RequiresManualInstall and CanDownload stays false.
[System.IO.File]::WriteAllText($manualManifestPath,
    (New-Manifest 'payload.zip' $payloadSize $payloadHash $manualVersion $manualMinVersion),
    (New-Object System.Text.UTF8Encoding($false)))
Zz ("manifests: {0}, {1}, {2}" -f $availableManifestPath, $failedManifestPath, $manualManifestPath)

# ── 1. Local feed server (own process so the throttled transfer survives the UIA work) ────
Write-Step "starting the local feed on http://127.0.0.1:$Port/ (chunk delay $ChunkDelayMs ms)"
# Start-Process joins an -ArgumentList array with spaces and does not add quotes, so every path
# is quoted here by hand (the repository lives under "G:\DeepSeek DSH\..." with spaces).
$serverArguments = (
    '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Port {1} -ZipPath "{2}" -BadZipPath "{3}" ' +
    '-ManifestAvailablePath "{4}" -ManifestFailedPath "{5}" -ManifestManualPath "{6}" -LogPath "{7}" -ChunkDelayMs {8}'
) -f $serverScript, $Port, $payloadPath, $badPayloadPath, $availableManifestPath, $failedManifestPath, $manualManifestPath, $serverLog, $ChunkDelayMs
$serverProcess = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $serverArguments -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 1500
if (-not (Test-Path -LiteralPath $serverLog)) { throw 'the local update feed did not start' }

Write-Step 'pinning the appearance so all four screenshots share one theme'
Set-PinnedAppearance

try {
    # ── 2. idle ──────────────────────────────────────────────────────────────────────────
    Write-Step 'scenario 1/5: idle (no check has run)'
    $run = Start-App 'update-available.json' $null
    Start-Sleep -Milliseconds 4500     # telemetry first frame, like the other verify scripts
    if (-not (Open-UpdatePage $run.Win.Handle)) { Zz "WARNING: the update page did not open" }
    $state = Wait-UiState $run.Win.Handle @('未检查', 'Not checked') 10000
    Zz "state='$state' version='$(Read-UiName $run.Win.Handle 'TxtUpdateVersion')' channel='$(Read-UiName $run.Win.Handle 'TxtUpdateChannel')' source='$(Read-UiName $run.Win.Handle 'TxtUpdateSource')'"
    Save-Shot $run.Win.Handle 'update-idle.png'
    Stop-App

    # ── 3. available ─────────────────────────────────────────────────────────────────────
    Write-Step 'scenario 2/5: available (--update-auto=check against the local manifest)'
    $run = Start-App 'update-available.json' 'check'
    Start-Sleep -Milliseconds 4500
    if (-not (Open-UpdatePage $run.Win.Handle)) { Zz "WARNING: the update page did not open" }
    $state = Wait-UiState $run.Win.Handle @('有新版', 'Update available') 20000
    Zz "state='$state' version='$(Read-UiName $run.Win.Handle 'TxtUpdateVersion')' source='$(Read-UiName $run.Win.Handle 'TxtUpdateSource')' published='$(Read-UiName $run.Win.Handle 'TxtUpdatePublished')'"
    Zz "target='$(Read-UiName $run.Win.Handle 'TxtUpdateTarget')'"
    Zz "notes: $((Read-UiName $run.Win.Handle 'TxtUpdateNotes') -replace "`r?`n", ' | ')"
    Save-Shot $run.Win.Handle 'update-available.png'
    Stop-App

    # ── 4. downloading ───────────────────────────────────────────────────────────────────
    # The page is opened BEFORE the transfer starts and the download is triggered by the real
    # page button, so the throttled 32 MB transfer is guaranteed to be in flight while the
    # screenshot is taken (an --update-auto=download launch races the UIA navigation).
    Write-Step 'scenario 3/5: downloading (page button → throttled local transfer)'
    $run = Start-App 'update-available.json' 'check'
    Start-Sleep -Milliseconds 4500
    if (-not (Open-UpdatePage $run.Win.Handle)) { Zz "WARNING: the update page did not open" }
    $state = Wait-UiState $run.Win.Handle @('有新版', 'Update available') 20000
    Zz "state before download='$state'"
    [void](Invoke-Ui $run.Win.Handle 'BtnUpdatePrimary' 8000)
    $state = Wait-UiState $run.Win.Handle @('下载中…', 'Downloading…') 20000
    # Long enough that the bar carries a readable percentage as well as bytes/speed; the
    # downloading state has no primary button, so its accent pixels ARE this bar fill.
    Start-Sleep -Milliseconds 4000
    Zz "state='$state' progress='$(Read-UiName $run.Win.Handle 'TxtUpdateProgress')' percent='$(Read-UiName $run.Win.Handle 'TxtUpdatePercent')'"
    Save-Shot $run.Win.Handle 'update-downloading.png'
    Stop-App

    # ── 5. failed (wrong sha256) ─────────────────────────────────────────────────────────
    Write-Step 'scenario 4/5: failed (page button → wrong sha256 in the manifest)'
    $run = Start-App 'update-failed.json' 'check'
    Start-Sleep -Milliseconds 4500
    if (-not (Open-UpdatePage $run.Win.Handle)) { Zz "WARNING: the update page did not open" }
    $state = Wait-UiState $run.Win.Handle @('有新版', 'Update available') 20000
    Zz "state before download='$state'"
    [void](Invoke-Ui $run.Win.Handle 'BtnUpdatePrimary' 8000)
    $state = Wait-UiState $run.Win.Handle @('更新失败', 'Update failed') 40000
    Zz "state='$state' failure='$(Read-UiName $run.Win.Handle 'TxtUpdateFailure')'"
    Zz "primary='$(Read-UiName $run.Win.Handle 'BtnUpdatePrimary')' secondary='$(Read-UiName $run.Win.Handle 'BtnUpdateSecondary')'"
    Save-Shot $run.Win.Handle 'update-failed.png'
    Stop-App
    Write-Step 'scenario 5/5: manual install (manifest minVersion above this build)'
    $run = Start-App 'update-manual.json' 'check'
    Start-Sleep -Milliseconds 4500
    if (-not (Open-UpdatePage $run.Win.Handle)) { Zz "WARNING: the update page did not open" }
    $state = Wait-UiState $run.Win.Handle @('需手动安装', 'Needs manual install') 20000
    $reason = Read-UiName $run.Win.Handle 'TxtUpdateBlockReason'
    $primary = Read-UiName $run.Win.Handle 'BtnUpdatePrimary'
    $primaryTarget = Find-Ui $run.Win.Handle 'BtnUpdatePrimary'
    $primaryEnabled = if ($null -ne $primaryTarget) { $primaryTarget.Current.IsEnabled } else { $null }
    Zz "state='$state' reason='$reason' primary='$primary' enabled=$primaryEnabled"
    if ($state -notin @('需手动安装', 'Needs manual install')) {
        throw "the minVersion gate did not reach the status line (state='$state')"
    }
    if ([string]::IsNullOrWhiteSpace($reason)) {
        throw 'the minVersion gate rendered no block reason'
    }
    # CanDownload is false below minVersion, so the button must be inert, not a live download.
    if ($primaryEnabled -ne $false) {
        throw "the download button is still enabled below minVersion (enabled=$primaryEnabled)"
    }
    Save-Shot $run.Win.Handle 'update-manual-install.png'
    Stop-App
}
finally {
    Stop-App
    if ($null -ne $serverProcess) {
        try { Stop-Process -Id $serverProcess.Id -Force } catch { }
    }
    Restore-Appearance
}

# ── 6. Screenshot assertions (do not trust PrintWindow blindly) ──────────────────────────
Write-Step 'asserting the five screenshots share one theme (the manual-install one has no accent on its button)'

if ($script:shots.Count -ne 5) {
    throw "expected 5 screenshots, captured $($script:shots.Count)"
}

# The minVersion-gated state renders an INERT primary button, and a disabled WinUI button paints
# ButtonBackgroundDisabled — there is no accent fill to find on the BUTTON. That is the
# assertion's point, so the accent check for that one shot is a ceiling rather than a floor:
# since the page grew an "Automatic updates" ToggleSwitch (accent fill when on, ~800 px at 100 %
# scale), "zero accent pixels" is no longer a true statement about a correct page. The ceiling
# still fails the run if the download button is live: an enabled primary button paints an
# accent slab of ~15 000 px, an order of magnitude above the switch.
$noAccentShot = 'update-manual-install.png'
$maxAccentOnInertShot = 3000

$reference = $script:shots[0]
$failed = $false
foreach ($shot in $script:shots) {
    Zz ("{0}: bg={1} accentPx={2} ink={3:P1}" -f $shot.Name, $shot.Background, $shot.Accent, $shot.Ink)
    if ($shot.Background -ne $reference.Background) {
        Write-Warning "$($shot.Name) page background $($shot.Background) != $($reference.Name) $($reference.Background)"
        $failed = $true
    }
    if ($shot.Name -eq $noAccentShot) {
        if ($shot.Accent -ge $maxAccentOnInertShot) {
            Write-Warning ("$($shot.Name) has $($shot.Accent) accent pixels (ceiling $maxAccentOnInertShot): " +
                "the download button is not disabled below minVersion")
            $failed = $true
        }
    } elseif ($shot.Accent -lt 20) {
        Write-Warning "$($shot.Name) has only $($shot.Accent) accent pixels: the accent fill is missing or not the fixed #3B82F6"
        $failed = $true
    }
    if ($shot.Ink -lt $script:minInk) {
        Write-Warning ("{0} looks blank (ink {1:P1})" -f $shot.Name, $shot.Ink)
        $failed = $true
    }
}

$expectedBackground = $pinnedBackground
if ($reference.Background -ne $expectedBackground) {
    Write-Warning "page background $($reference.Background) is not the pinned solid surface ${expectedBackground}: PrintWindow is still capturing something else"
    $failed = $true
}

$appearanceLine = $null
if (Test-Path -LiteralPath $shellLog) {
    $appearanceLine = Get-Content $shellLog |
        Where-Object { $_ -match 'appearance: init theme=' } |
        Select-Object -Last 1
}
if ($null -eq $appearanceLine) {
    Write-Warning 'shell.log has no appearance: init line, so the theme pinning cannot be proven'
    $failed = $true
} elseif ($appearanceLine -notmatch "theme=$Theme" -or $appearanceLine -notmatch 'material=Solid') {
    Write-Warning "the app did not start in the pinned theme/material: $appearanceLine"
    $failed = $true
} else {
    Zz "theme pin confirmed by shell.log: $appearanceLine"
}

if ($failed) {
    # Say WHY, not just that it failed. Both paths (PrintWindow and the screen) are tried by
    # Save-Shot; when every shot is blank even from the screen DC, the window is not presenting
    # its content at all and no capture technique can help — that is an application-side
    # failure, not a limitation of this script. The alternatives, in order:
    #   * Windows.Graphics.Capture (Windows.UI.Composition + Direct3D interop host): reads the
    #     composition layer directly, so it survives a blind PrintWindow — but it still sees
    #     nothing if the island is not composited, which is the case when the screen is blank too;
    #   * launch the app with the window chrome off (a verification switch) when the chrome is
    #     what breaks the content island;
    #   * run the verification on a machine whose Windows build matches the bundled Windows App
    #     SDK (see the note in this file's header).
    $blankFromScreen = @($script:shots | Where-Object { $_.Capture -eq 'BitBlt' -and $_.Ink -lt $script:minInk })
    if ($blankFromScreen.Count -eq $script:shots.Count) {
        Write-Warning ("every capture is blank from BOTH paths (PrintWindow and screen BitBlt): the " +
            "window is not presenting its content, so no capture technique can recover it. " +
            "This is an application / OS-side composition failure, not a screenshot-script one.")
    } elseif ($script:screenShowedSomething) {
        Write-Warning ("PrintWindow returned nothing but the screen DID show pixels: this is the " +
            "Composition-content-island case. Neither GDI path captured the app surface, so the " +
            "next step is Windows.Graphics.Capture (reads the composition layer directly, needs a " +
            "Direct3D interop host) - do not relax the assertions.")
    } elseif ($script:screenShowedSomething) {
        Write-Warning ("PrintWindow returned nothing but the screen DID show pixels: this is the " +
            "Composition-content-island case. Neither GDI path captured the app surface, so the " +
            "next step is Windows.Graphics.Capture (reads the composition layer directly, needs a " +
            "Direct3D interop host) - do not relax the assertions.")
    }
    Write-Warning ("the captures are kept for diagnosis in {0}; {1} was NOT touched" -f $shotStaging, $OutDir)
    throw 'screenshot assertions failed'
}
Zz ("all five screenshots: one page background ({0}); accent present in the four interactive states" -f $reference.Background)

Write-Step "publishing the five screenshots to $OutDir (all assertions passed)"
Publish-Shots

# ── 7. Evidence ──────────────────────────────────────────────────────────────────────────
Write-Step 'shell.log update lines (last run)'
if (Test-Path -LiteralPath $shellLog) {
    Get-Content $shellLog | Where-Object { $_ -match 'update:' } | ForEach-Object { Zz $_ }
} else {
    Zz 'shell.log NOT FOUND'
}

Write-Step 'local feed request log'
if (Test-Path -LiteralPath $serverLog) {
    Get-Content $serverLog | ForEach-Object { Zz $_ }
} else {
    Zz 'update-server.log NOT FOUND'
}

Write-Step 'screenshots'
foreach ($name in 'update-idle.png', 'update-available.png', 'update-downloading.png', 'update-failed.png', 'update-manual-install.png') {
    $path = Join-Path $OutDir $name
    if (Test-Path -LiteralPath $path) {
        Zz ("{0}  {1:N1} KB" -f $path, ((Get-Item -LiteralPath $path).Length / 1KB))
    } else {
        Zz "$path MISSING"
    }
}

Write-Step 'done'
