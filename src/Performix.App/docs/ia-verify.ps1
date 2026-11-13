# IA phase 1 acceptance screenshots (docs\design\UI-ARCH.md §1/§2).
#
#   pwsh -NoProfile -File src\Performix.App\docs\ia-verify.ps1 -Exe "<...>\Performix.exe"
#
# Produces (repo docs\):
#   ia-settings-hub.png      the Settings directory page: 4 navigation rows + version top right
#   ia-general.png           Settings → General
#   ia-back-to-settings.png  after Back from General: the directory page again (proves the
#                            PageBackTargets table; the old shell returned Home)
#   ia-power.png             Settings → Power Profiles
#   ia-meta.png              Settings → Updates and About (honest "no update feed" state)
#
# The window enumeration / PrintWindow / UIA-Click primitives are the same ones
# docs\s1-verify.ps1 … docs\s6-verify.ps1 used in the WPF-era verification rounds (EnumWindows
# by PID, PrintWindow(PW_RENDERFULLCONTENT) + a non-blank ink check so an overlapping window
# cannot fake a screenshot, UIA AutomationId clicks).
#
# Read-only with respect to hardware: only navigation rows and the back button are clicked.
# No mode tile, GPU tile, toggle or Apply control is touched. The caller still has to run
# scripts\Restore-AutoFan.ps1 after the run (real-machine discipline).
#
# Log evidence: exe-directory shell.log gets `nav: navigate Settings -> General` /
# `nav: back General -> Settings` lines from ShellWindow (started with --trace).

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$OutDir = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path 'docs')
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

public static class IAV
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
            found.Add(new WinInfo
            {
                Handle = h, ClassName = cls.ToString(), Title = txt.ToString(), Visible = IsWindowVisible(h)
            });
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool PrintTo(IntPtr hwnd, IntPtr hdc) => PrintWindow(hwnd, hdc, 2);

    public static int[] RectOf(IntPtr h)
    {
        RECT r;
        if (!GetWindowRect(h, out r)) return new int[0];
        return new[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }

    public static int DpiOf(IntPtr h) { return (int)GetDpiForWindow(h); }
}
'@

[IAV]::MakeDpiAware()

function Write-Step([string]$text) { Write-Output "== $text" }

function Get-ShellWindow([int]$processId, [int]$tries = 80) {
    for ($i = 0; $i -lt $tries; $i++) {
        foreach ($w in [IAV]::WindowsOf([uint32]$processId)) {
            if ($w.ClassName -eq 'WinUIDesktopWin32WindowClass' -and $w.Title -eq 'Performix') { return $w }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Report-Rect([IntPtr]$handle, [string]$label) {
    $r = [IAV]::RectOf($handle)
    $scale = [IAV]::DpiOf($handle) / 96.0
    Write-Output ("   {0}: {1}x{2} physical = {3:N0}x{4:N0} DIP" -f $label, $r[2], $r[3], ($r[2] / $scale), ($r[3] / $scale))
}

function Measure-Ink([System.Drawing.Bitmap]$bmp) {
    $reference = $bmp.GetPixel(1, 1)
    $differing = 0; $sampled = 0
    for ($y = 0; $y -lt $bmp.Height; $y += 3) {
        for ($x = 0; $x -lt $bmp.Width; $x += 3) {
            $sampled++
            $c = $bmp.GetPixel($x, $y)
            if ([Math]::Abs($c.R - $reference.R) + [Math]::Abs($c.G - $reference.G) + [Math]::Abs($c.B - $reference.B) -gt 12) { $differing++ }
        }
    }
    if ($sampled -eq 0) { return 0 }
    return $differing / $sampled
}

function Save-Shot([IntPtr]$handle, [string]$name) {
    $rect = [IAV]::RectOf($handle)
    if ($rect.Length -eq 0) { Write-Output "   shot $name FAILED (no rect)"; return }
    $bmp = New-Object System.Drawing.Bitmap($rect[2], $rect[3])
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [IAV]::PrintTo($handle, $hdc)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $ink = Measure-Ink $bmp
    $path = Join-Path $OutDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output ("   shot {0} {1}x{2} ink={3:P1} PrintWindow={4}" -f $name, $rect[2], $rect[3], $ink, $ok)
}

function Find-Ui([IntPtr]$root, [string]$automationId) {
    $element = [System.Windows.Automation.AutomationElement]::FromHandle($root)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    return $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-Ui([IntPtr]$root, [string]$automationId) {
    $target = Find-Ui $root $automationId
    if ($null -eq $target) { Write-Output "   UIA: $automationId NOT FOUND"; return $false }
    $pattern = $null
    if ($target.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
        Write-Output "   UIA: invoked $automationId"
        return $true
    }
    Write-Output "   UIA: $automationId has no invoke pattern"
    return $false
}

function Read-UiText([IntPtr]$root, [string]$automationId) {
    $target = Find-Ui $root $automationId
    if ($null -eq $target) { Write-Output "   UIA: $automationId NOT FOUND"; return }
    Write-Output ("   UIA: {0} = '{1}'" -f $automationId, $target.Current.Name)
}

function Start-Shell([string[]]$extraArgs) {
    Get-Process Performix -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
    $work = Split-Path -Parent (Resolve-Path $Exe).Path
    $logPath = Join-Path $work 'shell.log'
    if (Test-Path $logPath) { Remove-Item $logPath -Force }
    # Start from the collapsed home density so the expand → entry-row sequence is deterministic
    # (ShellUiState lives in %LOCALAPPDATA%\Predex\shell-ui.json, not in the output directory).
    $uiState = Join-Path $env:LOCALAPPDATA 'Predex\shell-ui.json'
    if (Test-Path $uiState) { Remove-Item $uiState -Force }
    $launchArgs = @('--no-autohide', '--trace') + $extraArgs
    Write-Output ("   launch: {0} {1}" -f (Split-Path -Leaf $Exe), ($launchArgs -join ' '))
    $proc = Start-Process -FilePath $Exe -WorkingDirectory $work -ArgumentList $launchArgs -PassThru
    $win = Get-ShellWindow -processId $proc.Id
    if ($null -eq $win) { throw "shell window not found (pid $($proc.Id))" }
    Write-Output ("   pid={0} hwnd=0x{1:X} visible={2}" -f $proc.Id, $win.Handle.ToInt64(), $win.Visible)
    return [pscustomobject]@{ Proc = $proc; Win = $win; Log = $logPath }
}

function Stop-Shell {
    Get-Process Performix -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 700
}

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

# ═══ Settings directory + its four sub-pages ═════════════════════════════════
$run = Start-Shell @()
$proc = $run.Proc; $win = $run.Win; $logPath = $run.Log
Start-Sleep -Milliseconds 5000    # telemetry first frame
$hwnd = $win.Handle

Write-Step 'expand home (the entry row lives in the expanded density)'
[void](Invoke-Ui $hwnd 'BtnExpand')
Start-Sleep -Milliseconds 900

Write-Step 'open Settings (directory page)'
[void](Invoke-Ui $hwnd 'OpenSettings')
Start-Sleep -Milliseconds 1600
Report-Rect $hwnd 'Settings hub'
Read-UiText $hwnd 'TxtSettingsVersion'
Save-Shot $hwnd 'ia-settings-hub.png'

Write-Step 'Settings → General'
[void](Invoke-Ui $hwnd 'BtnSettingsGeneral')
Start-Sleep -Milliseconds 1500
Report-Rect $hwnd 'General'
Save-Shot $hwnd 'ia-general.png'

Write-Step 'Back from General (must be Settings, not Home)'
[void](Invoke-Ui $hwnd 'BtnBack')
Start-Sleep -Milliseconds 1200
Report-Rect $hwnd 'after Back'
Read-UiText $hwnd 'TxtSettingsVersion'
Save-Shot $hwnd 'ia-back-to-settings.png'

Write-Step 'Settings → Power Profiles'
[void](Invoke-Ui $hwnd 'BtnSettingsPower')
Start-Sleep -Milliseconds 1500
Report-Rect $hwnd 'Power Profiles'
Save-Shot $hwnd 'ia-power.png'

Write-Step 'Back → Settings → Updates and About'
[void](Invoke-Ui $hwnd 'BtnBack')
Start-Sleep -Milliseconds 1200
[void](Invoke-Ui $hwnd 'BtnSettingsMeta')
Start-Sleep -Milliseconds 1500
Report-Rect $hwnd 'Updates and About'
Read-UiText $hwnd 'TxtUpdateState'
Read-UiText $hwnd 'TxtMetaVersion'
Save-Shot $hwnd 'ia-meta.png'

Write-Step 'shell.log navigation evidence'
if (Test-Path $logPath) {
    Get-Content $logPath | Where-Object { $_ -match 'nav: navigate|nav: back' } | ForEach-Object { "   $_" }
} else {
    Write-Output '   shell.log NOT FOUND'
}

Stop-Shell
Write-Step 'done'
