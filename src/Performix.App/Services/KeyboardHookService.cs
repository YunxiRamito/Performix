// Global low-level keyboard hook for WinUI 3.
// Keeps the same public constructor signature as the WPF KeyboardHook so
// MainWindow.xaml.cs compiles unmodified.

using System;
using System.Runtime.InteropServices;

namespace PreySense.Frontend.Services
{
    public sealed class KeyboardHook : IDisposable
    {
        // VKey constants
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;

        // ── Predator key (the Acer logo key above the number row) ────────────────
        // Hardware path: scan code E0 75. The low-level hook receives it as
        //   vk=0xFF  scan=0x75  flags=LLKHF_EXTENDED
        // measured 5/5 on this machine (docs\predator-key-capture.txt:22-26). Match the
        // scan code, never a virtual key: 0xFF is not a VK Windows ever assigns, and the
        // old VK_PREDATOR = 0xCC could only match synthetic keybd_event injection.
        private const uint ScanPredator = 0x75;
        private const uint LLKHF_EXTENDED = 0x01;

        /// <summary>
        /// Synthetic virtual key that no hardware sends; kept only so docs\s1-verify.ps1 and
        /// UI automation can still drive "Predator key pressed" through keybd_event(0xCC).
        /// </summary>
        private const uint VkPredatorInjected = 0xCC;

        // REMOVED as dead code (this machine, PH317-55):
        //   VK_MODE_KEY = 0xC3  (this fork) and SC_MODE_KEY = 0x85 (the WPF fork) are PHN16
        //   scan codes that never arrive here.
        //   The chassis mode / Turbo key is not a keyboard event at all: it is an ACPI-WMI
        //   extrinsic event (root\wmi:APGeEvent), consumed by Services\AcerHotkeyWatcher.cs.
        //   docs\KEY-EVENT-SOURCES.md §4 "id 错了" and §7 待实测 #1/#2.

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private readonly Action _onPredatorSensePressed;
        private readonly Action<int>? _onPredatorNumberPressed;
        private readonly Action? _onPredatorRPressed;
        private LowLevelKeyboardProc? _hookProc;  // keep alive
        private IntPtr _hookHandle = IntPtr.Zero;
        private bool _disposed;

        private bool _predatorDown;  // tracks whether the Predator key is held

        public KeyboardHook(
            Action onPredatorSensePressed,
            Action<int>? onPredatorNumberPressed = null,
            Action? onPredatorRPressed = null,
            Action? onModeKeyPressed = null)
        {
            _onPredatorSensePressed = onPredatorSensePressed;
            _onPredatorNumberPressed = onPredatorNumberPressed;
            _onPredatorRPressed = onPredatorRPressed;

            // onModeKeyPressed is kept only for source compatibility with ShellServices: no
            // keyboard event produces the chassis mode key on this machine (see the block
            // above), so this hook will never invoke it.
            _ = onModeKeyPressed;

            _hookProc = HookCallback;
            using var module = System.Diagnostics.Process.GetCurrentProcess().MainModule;
            _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc,
                GetModuleHandle(module?.ModuleName), 0);
        }

        /// <summary>True when SetWindowsHookEx returned a hook handle (used by the S1 trace log).</summary>
        internal bool IsInstalled => _hookHandle != IntPtr.Zero;

        /// <summary>
        /// Scan-code match for the Predator key. The injected VK is the second branch so the
        /// automation path keeps working without weakening the hardware match.
        /// </summary>
        private static bool IsPredatorKey(KBDLLHOOKSTRUCT kb) =>
            (kb.scanCode == ScanPredator && (kb.flags & LLKHF_EXTENDED) != 0)
            || kb.vkCode == VkPredatorInjected;

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int message = (int)wParam;
                bool down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
                bool up = message == WM_KEYUP || message == WM_SYSKEYUP;
                bool isPredator = IsPredatorKey(kb);

                if (down && isPredator)
                {
                    if (!_predatorDown)
                    {
                        _predatorDown = true;
                        _onPredatorSensePressed?.Invoke();
                    }
                    return (IntPtr)1; // consume
                }

                if (down && _predatorDown)
                {
                    // Predator + 1–5 → mode shortcut
                    if (kb.vkCode >= (uint)'1' && kb.vkCode <= (uint)'5')
                    {
                        _onPredatorNumberPressed?.Invoke((int)(kb.vkCode - (uint)'0'));
                        return (IntPtr)1;
                    }
                    // Predator + R → overlay toggle
                    if (kb.vkCode == (uint)'R')
                    {
                        _onPredatorRPressed?.Invoke();
                        return (IntPtr)1;
                    }
                }

                // Clear the held state on either key-up message (E0 75 reports both).
                if (up && isPredator) _predatorDown = false;
            }

            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_hookHandle != IntPtr.Zero) { UnhookWindowsHookEx(_hookHandle); _hookHandle = IntPtr.Zero; }
            _hookProc = null;
        }
    }
}
