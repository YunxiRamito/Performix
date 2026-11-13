# 实时录制低调键盘事件，识别 Predator 键 / 狂暴模式键的 VK 与扫描码。
# 只读监听：不吞键、不改注册表、不改任何系统设置。事件逐条追加写盘，随时可读。
param(
    [int]$Seconds = 600,
    [string]$OutFile = (Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\predator-key-capture.txt')
)

$dir = Split-Path -Parent $OutFile
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
Remove-Item $OutFile -ErrorAction SilentlyContinue
"# capture started $(Get-Date -Format 'HH:mm:ss')  (WH_KEYBOARD_LL, pass-through)" | Set-Content -Path $OutFile -Encoding utf8

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;

public static class KeyTap2
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private static HookProc _proc = Hook;
    private static IntPtr _hook = IntPtr.Zero;
    private static string _path;
    private static DateTime _start;
    private static int _count;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    private static IntPtr Hook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            KBDLLHOOKSTRUCT d = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            _count++;
            string line = string.Format("{0}  #{1,-3} vk=0x{2:X2} scan=0x{3:X2} ext={4} injected={5} info=0x{6:X}",
                DateTime.Now.ToString("HH:mm:ss.fff"), _count, d.vkCode, d.scanCode,
                (d.flags & 0x01) != 0, (d.flags & 0x10) != 0, (ulong)d.dwExtraInfo);
            try { File.AppendAllText(_path, line + Environment.NewLine); } catch { }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public static string Run(int seconds, string path)
    {
        _path = path;
        _start = DateTime.UtcNow;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            return "HOOK_FAILED error=" + Marshal.GetLastWin32Error();

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            MSG msg;
            while (PeekMessage(out msg, IntPtr.Zero, 0, 0, 1)) { }
            System.Threading.Thread.Sleep(10);
        }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        return "done, events=" + _count;
    }
}
'@

$result = [KeyTap2]::Run($Seconds, $OutFile)
"# stopped $(Get-Date -Format 'HH:mm:ss')  $result" | Add-Content -Path $OutFile -Encoding utf8
$result
