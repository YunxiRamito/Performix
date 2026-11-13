// WinUI 3 tray icon service — keeps the same public surface as the WPF
// TrayIconManager (WndProc / Dispose) so MainWindow.xaml.cs compiles.
// Uses native Shell_NotifyIcon + window subclassing to receive callbacks.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PreySense.Frontend.Services
{
    public sealed class TrayIconManager : IDisposable
    {
        private const int WM_USER = 0x0400;
        public const int WM_TRAY_CALLBACK = WM_USER + 2048;

        private const int NIM_ADD = 0x00000000;
        private const int NIM_MODIFY = 0x00000001;
        private const int NIM_DELETE = 0x00000002;
        private const int NIM_SETVERSION = 0x00000004;

        private const int NIF_MESSAGE = 0x00000001;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;
        private const int NOTIFYICON_VERSION_4 = 4;

        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_CONTEXTMENU = 0x007B;

        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint MF_STRING = 0x00000000;
        private const uint MF_SEPARATOR = 0x00000800;
        private const uint MIIM_BITMAP = 0x00000080;
        private const uint DIB_RGB_COLORS = 0;
        private const uint DI_NORMAL = 0x0003;

        private const uint CMD_OPEN = 2001;
        private const uint CMD_QUIT = 2002;

        private const int IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const int IDI_APPLICATION = 32512;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MENUITEMINFO
        {
            public uint cbSize;
            public uint fMask;
            public uint fType;
            public uint fState;
            public uint wID;
            public IntPtr hSubMenu;
            public IntPtr hbmpChecked;
            public IntPtr hbmpUnchecked;
            public UIntPtr dwItemData;
            public IntPtr dwTypeData;
            public uint cch;
            public IntPtr hbmpItem;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cx, int cy, uint fuLoad);
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern uint ExtractIconEx(string szFileName, int nIconIndex, out IntPtr phLarge, out IntPtr phSmall, uint nIcons);
        [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr hInst, IntPtr lpName);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern uint RegisterWindowMessage(string s);
        [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uID, string text);
        [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lp);
        [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lp);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();

        // Window subclassing so we can intercept WM_TRAY_CALLBACK on the WinUI 3 HWND.
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, WndProcDelegate lpfn);
        [DllImport("user32.dll")] private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private const int GWLP_WNDPROC = -4;

        private readonly IntPtr _hWnd;
        private readonly Action _onOpen;
        private readonly Action _onQuit;
        private readonly Action _onLeftClick;
        private readonly uint _taskbarCreatedMsg;
        private NOTIFYICONDATA _nid;
        private IntPtr _hIcon = IntPtr.Zero;
        private IntPtr _prevWndProc = IntPtr.Zero;
        private WndProcDelegate? _wndProcDelegate; // keep alive — GC guard
        private bool _isCreated;
        private bool _disposed;

        public TrayIconManager(IntPtr hWnd, Action onOpen, Action onQuit, Action? onLeftClick = null)
        {
            _hWnd = hWnd;
            _onOpen = onOpen;
            _onQuit = onQuit;
            _onLeftClick = onLeftClick ?? onOpen;
            _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
            ApplyMenuTheme(ThemeManager.IsLightTheme);
            SubclassWindow();
            InitTrayIcon();
        }

        private void SubclassWindow()
        {
            _wndProcDelegate = WndProcImpl;
            _prevWndProc = SetWindowLongPtr(_hWnd, GWLP_WNDPROC, _wndProcDelegate);
        }

        private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (!_disposed)
            {
                if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
                {
                    Shell_NotifyIcon(NIM_ADD, ref _nid);
                    _nid.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
                    Shell_NotifyIcon(NIM_SETVERSION, ref _nid);
                    return IntPtr.Zero;
                }
                if (msg == WM_TRAY_CALLBACK)
                {
                    uint evt = (uint)(lParam.ToInt64() & 0xFFFF);
                    // Left click toggles: the panel has no close button any more, so
                    // clicking the tray icon again must hide it.
                    if (evt == WM_LBUTTONUP || evt == WM_LBUTTONDBLCLK)
                        { (_onLeftClick ?? _onOpen)?.Invoke(); return IntPtr.Zero; }
                    if (evt == WM_RBUTTONUP || evt == WM_CONTEXTMENU)
                        { ShowContextMenu(); return IntPtr.Zero; }
                }
            }
            return CallWindowProc(_prevWndProc, hWnd, msg, wParam, lParam);
        }

        /// <summary>True when Shell_NotifyIcon(NIM_ADD) succeeded (used by the S1 trace log).</summary>
        internal bool IsCreated => _isCreated;

        // Legacy hook entry point — not used for WinUI 3 (subclassing is used instead).
        public IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
            => IntPtr.Zero;

        private void InitTrayIcon()
        {
            _hIcon = LoadAppIcon();
            _nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hWnd, uID = 1001,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAY_CALLBACK,
                hIcon = _hIcon, szTip = "Performix"
            };
            _isCreated = Shell_NotifyIcon(NIM_ADD, ref _nid);
            if (_isCreated) { _nid.uTimeoutOrVersion = NOTIFYICON_VERSION_4; Shell_NotifyIcon(NIM_SETVERSION, ref _nid); }
        }

        private static IntPtr LoadAppIcon()
        {
            try { string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appicon.ico"); if (File.Exists(p)) { var h = LoadImage(IntPtr.Zero, p, IMAGE_ICON, 16, 16, LR_LOADFROMFILE); if (h != IntPtr.Zero) return h; } } catch { }
            try { string? exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName; if (!string.IsNullOrEmpty(exe)) { ExtractIconEx(exe, 0, out _, out IntPtr small, 1); if (small != IntPtr.Zero) return small; } } catch { }
            return LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
        }

        internal static void ApplyMenuTheme(bool isLight)
        {
            try { SetPreferredAppMode(isLight ? 3 : 2); FlushMenuThemes(); }
            catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        }

        private void ShowContextMenu()
        {
            ApplyMenuTheme(ThemeManager.IsLightTheme);
            GetCursorPos(out POINT pt);
            SetForegroundWindow(_hWnd);
            IntPtr hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return;
            AppendMenu(hMenu, MF_STRING, CMD_OPEN, "Performix");
            AppendMenu(hMenu, MF_SEPARATOR, 0, string.Empty);
            AppendMenu(hMenu, MF_STRING, CMD_QUIT, LocalizationService.Translate("Quit"));
            uint cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.x, pt.y, _hWnd, IntPtr.Zero);
            DestroyMenu(hMenu);
            PostMessage(_hWnd, 0, IntPtr.Zero, IntPtr.Zero);
            if (cmd == CMD_OPEN) _onOpen?.Invoke();
            else if (cmd == CMD_QUIT) _onQuit?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_isCreated) { Shell_NotifyIcon(NIM_DELETE, ref _nid); _isCreated = false; }
            if (_hIcon != IntPtr.Zero) { DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
        }
    }
}
