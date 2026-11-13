// Shell-owned background services: tray icon, Predator hotkeys, HID arrival watcher,
// idle memory trimmer.
//
// Why they are not in the window (WINUI3-MIGRATION.md S1 risk ①): the window's lifetime is
// "hide to tray / show again", so anything that must survive a hide has to be owned by
// something with a longer life than a single visibility cycle. The shell window reacts to
// events; it does not own the native plumbing.
//
// VK codes come from the WPF build (red line: don't "fix" them):
//   VK_PREDATOR = 0xCC  (Fn+Home / Predator key)
//   VK_MODE_KEY = 0xC3  (Fn-assignable mode cycle key)

using Microsoft.UI.Dispatching;
using PreySense.Frontend.Services;
using System;

namespace Predex.WinUI.Services
{
    internal sealed class ShellServices : IDisposable
    {
        private readonly MemoryManager _memory;
        private TrayIconManager? _tray;
        private KeyboardHook? _hook;
        private HidDeviceArrivalWatcher? _hid;
        private bool _disposed;

        public bool TrayIconCreated => _tray?.IsCreated == true;
        public bool KeyboardHookInstalled => _hook?.IsInstalled == true;
        public bool HidWatcherCreated => _hid is not null;

        /// <summary>Tray menu "Predex" picked (open the window).</summary>
        public event Action? OpenRequested;
        /// <summary>Tray icon left-clicked: show the window, or hide it when it is visible.</summary>
        public event Action? TrayToggleRequested;
        /// <summary>Predator key pressed (toggle window visibility).</summary>
        public event Action? ToggleRequested;
        /// <summary>Tray menu Quit.</summary>
        public event Action? QuitRequested;
        /// <summary>Predator + R (overlay toggle).</summary>
        public event Action? OverlayToggleRequested;
        /// <summary>Fn mode-cycle key.</summary>
        public event Action? ModeKeyPressed;
        /// <summary>Predator + 1..5 → Acer mode byte.</summary>
        public event Action<byte>? ModeShortcutRequested;

        public ShellServices(IntPtr hWnd, DispatcherQueue queue)
        {
            _memory = new MemoryManager(queue);

            try
            {
                _tray = new TrayIconManager(
                    hWnd,
                    onOpen: () => OpenRequested?.Invoke(),
                    onQuit: () => QuitRequested?.Invoke(),
                    onLeftClick: () => TrayToggleRequested?.Invoke());
            }
            catch (Exception ex) { ShellLog.Write("tray", $"init failed: {ex.Message}"); }

            try { _hid = new HidDeviceArrivalWatcher(hWnd); }
            catch (Exception ex) { ShellLog.Write("hid", $"init failed: {ex.Message}"); }

            try
            {
                _hook = new KeyboardHook(
                    onPredatorSensePressed: () => ToggleRequested?.Invoke(),
                    onPredatorNumberPressed: n =>
                    {
                        byte mode = n switch
                        {
                            1 => 0x06, // Eco
                            2 => 0x00, // Quiet
                            3 => 0x01, // Balanced
                            4 => 0x04, // Performance
                            5 => 0x05, // Turbo
                            _ => 0xFF
                        };
                        if (mode != 0xFF) ModeShortcutRequested?.Invoke(mode);
                    },
                    onPredatorRPressed: () => OverlayToggleRequested?.Invoke(),
                    onModeKeyPressed: () => ModeKeyPressed?.Invoke());
            }
            catch (Exception ex) { ShellLog.Write("hook", $"init failed: {ex.Message}"); }

            ShellLog.Write("shell",
                $"services ready tray={TrayIconCreated} hook={KeyboardHookInstalled} hid={HidWatcherCreated}");
        }

        public void NotifyWindowShown()
        {
            _memory.OnWindowShown();
            OverlayBridge.StartIfNeeded();
        }

        public void NotifyWindowHidden() => _memory.OnWindowHidden();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _hook?.Dispose(); } catch { }
            try { _hid?.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
            try { _memory.Dispose(); } catch { }
            try { OverlayBridge.Dispose(); } catch { }
        }
    }
}
