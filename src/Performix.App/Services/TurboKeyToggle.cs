// Turbo key semantics: the chassis Turbo key is a TOGGLE, not a "jump to Turbo" button.
//
// User-verified hardware behaviour on this machine (authoritative, do not re-derive):
//   press 1 → Turbo (operating-mode byte 0x05), the keyboard Turbo LED comes on
//   press 2 → back to the mode it was in before the first press, the LED goes off
// The LED is owned by firmware / EC and follows the operating mode; nothing in this file
// (or in its callers) may write keyboard lighting (SetGamingRgbKb / LED paths).
//
// Direction comes from the live mode, not from a stored flag: MainViewModel.ActivePowerMode
// is the mode the ViewModel last confirmed against the EC (pipe cmd 33), so a press after a
// process restart still toggles the right way without a new config field. The remembered
// "previous" byte is only a shortcut for the way back, and the fallback when the live mode
// cannot be read (0xFF after a failed switch).

using System;
using System.Threading;
using System.Threading.Tasks;
using PreySense.Frontend.ViewModels;
using PreySense.Mode;

namespace Predex.WinUI.Services
{
    internal static class TurboKeyToggle
    {
        /// <summary>Acer Turbo operating mode (MainViewModel.PowerModes: 5 = Turbo, 狂暴).</summary>
        internal const byte TurboMode = 0x05;

        /// <summary>Balanced (均衡) — the documented fallback when no previous mode is known.</summary>
        internal const byte BalancedMode = 0x01;

        /// <summary>
        /// Held for a whole press: direction decision + SetModeAsync. A press that lands while
        /// a switch is still running waits here instead of firing a second SetModeAsync into
        /// the EC — two overlapping operating-mode writes are exactly what the Acer mode
        /// handshake cannot survive.
        /// </summary>
        private static readonly SemaphoreSlim Gate = new(1, 1);

        /// <summary>A stuck switch must not queue presses forever; give up on the wait instead.</summary>
        private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(8);

        /// <summary>Last non-Turbo mode seen. Per-process memory only — never persisted.</summary>
        private static byte _previousMode = BalancedMode;
        private static bool _hasPrevious;

        /// <summary>
        /// Runs one Turbo-key press (toggle Turbo ↔ previous mode).
        /// Call from the UI thread — the ViewModel is touched directly.
        /// </summary>
        /// <param name="viewModel">The shell's MainViewModel (owner of the mode switch).</param>
        /// <param name="origin">
        /// Suffix appended to the log line, e.g. <c>" [simulated]"</c>. Empty for a real press.
        /// </param>
        public static async Task ToggleAsync(MainViewModel viewModel, string origin)
        {
            if (!await Gate.WaitAsync(GateTimeout).ConfigureAwait(true))
            {
                ShellLog.Write("turbo key", $"press dropped: previous switch still running after {GateTimeout.TotalSeconds:0}s{origin}");
                return;
            }

            try
            {
                byte current = await ResolveCurrentModeAsync(viewModel).ConfigureAwait(true);
                byte target;
                string suffix;

                if (current != TurboMode)
                {
                    // Not in Turbo: this press goes to Turbo and remembers where it came from.
                    _previousMode = current;
                    _hasPrevious = true;
                    target = TurboMode;
                    suffix = string.Empty;
                }
                else if (_hasPrevious && ProfileManager.IsSupportedMode(_previousMode))
                {
                    target = _previousMode;
                    suffix = " (previous)";
                }
                else
                {
                    target = BalancedMode;
                    suffix = " (fallback, no previous mode known)";
                }

                ShellLog.Write("turbo key", $"0x{current:X2} -> 0x{target:X2}{suffix}{origin}");

                // The gate stays held until SetModeAsync has handed the request off, which is
                // what keeps a second press from writing the operating mode in parallel. The
                // duration is also the serialisation evidence in the trace.
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                await viewModel.SetModeAsync(target).ConfigureAwait(true);
                ShellLog.Write("turbo key",
                    $"0x{current:X2} -> 0x{target:X2} switch call finished in {stopwatch.ElapsedMilliseconds} ms{origin}");
            }
            catch (Exception ex)
            {
                ShellLog.Write("turbo key", $"press failed: {ex.GetType().Name}: {ex.Message}{origin}");
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// Live operating mode. <c>ActivePowerMode</c> is the fast path (it is reconciled to the
        /// EC after every confirmed switch); 0xFF / garbage means the last switch failed, so ask
        /// the hardware once — the pipe round trip blocks, hence the Task.Run.
        /// </summary>
        private static async Task<byte> ResolveCurrentModeAsync(MainViewModel viewModel)
        {
            byte mode = viewModel.ActivePowerMode;
            if (ProfileManager.IsSupportedMode(mode))
                return mode;

            ShellLog.Write("turbo key", $"ActivePowerMode=0x{mode:X2} is not a live mode, reading the hardware");
            try
            {
                byte? live = await Task.Run(() =>
                    viewModel.TryGetPowerMode(out byte hardware) ? (byte?)hardware : null).ConfigureAwait(true);
                if (live is { } found && ProfileManager.IsSupportedMode(found))
                    return found;
            }
            catch (Exception ex)
            {
                ShellLog.Write("turbo key", $"live mode read failed: {ex.GetType().Name}: {ex.Message}");
            }

            return _hasPrevious && ProfileManager.IsSupportedMode(_previousMode) ? _previousMode : BalancedMode;
        }
    }
}
