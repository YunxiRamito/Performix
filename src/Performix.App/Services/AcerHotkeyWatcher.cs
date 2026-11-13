// ACPI-WMI hotkey watcher for the chassis keys the keyboard hook cannot see
// (Turbo / "extreme mode" key on PH317-55).
//
// Why not a keyboard hook (docs\KEY-EVENT-SOURCES.md §1.2): the Turbo key never becomes a
// scan code. The EC raises an ACPI notify, the Acer firmware provider turns it into a
// root\wmi *extrinsic* event — class APGeEvent, property EventDetail (uint8[8]) — and the
// official stack consumes exactly that:
//
//   PSAdminAgent.exe: "SELECT * FROM APGeEvent" → treadstone::TsWmiEventSink::Indicate
//     detail[0] = 1 / 7 / 8 / 9 / 12      (event category, dispatch table)
//     detail[0] == 7 && detail[1] == 4    → HotKeyAction::Press_Turbo_key[_Operating_Mode]
//
// That byte id is *inferred from PSAdminAgent's disassembly*, never observed on this
// machine yet — so this watcher logs every event verbatim first and only then raises
// TurboKeyRequested. Read shell.log after a real key press before trusting the mapping.
//
// Subscription is a consumer model (every ManagementEventWatcher is an independent
// consumer and gets its own copy), so we never steal the event from PSSvc / PSAdminAgent.
// The real exclusion is the WMI method mutex AOD_WMI_HANDLER_MUTEX: with the official GUI
// running both halves act on the same press ("double click", Handover §9) — hence the
// warning this watcher writes instead of killing anything.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace Predex.WinUI.Services
{
    [SupportedOSPlatform("windows")]
    internal sealed class AcerHotkeyWatcher : IDisposable
    {
        /// <summary>detail[0] of the "game key family" branch in PSAdminAgent.</summary>
        public const byte CategoryGameKey = 0x07;
        /// <summary>detail[1] of the Turbo / extreme-mode key inside that branch (inferred,待实测).</summary>
        public const byte TurboKeySubId = 0x04;
        /// <summary>detail[1] == 1: hotkey-profile notify (detail[2] = number).</summary>
        public const byte ProfileNotifySubId = 0x01;
        /// <summary>detail[1] == 2: custom key (detail[2] = key number).</summary>
        public const byte CustomKeySubId = 0x02;

        private const string WmiNamespace = @"root\wmi";
        private const string LogArea = "acer hotkey watcher";

        /// <summary>APGeEvent is the verified class; AcerGenericEvent has the same shape and is the fallback.</summary>
        private static readonly string[] EventClasses = { "APGeEvent", "AcerGenericEvent" };

        /// <summary>
        /// Official binaries that consume the very same ACPI event: PredatorSense GUI,
        /// PSAdminAgent and the Quick Access pair. PSSvc (the service) is expected to run
        /// and is deliberately not listed — it only exposes WMI *methods*, no event sink.
        /// </summary>
        private static readonly string[] OfficialConsumers =
            { "PredatorSense", "PSAdminAgent", "PSAgent", "QAAdminAgent", "QALockHandler" };

        private readonly object _gate = new();
        private Thread? _thread;
        private ManualResetEventSlim? _stop;
        private ManagementEventWatcher? _watcher;
        private bool _disposed;
        private DateTime _lastOfficialStackCheckUtc = DateTime.MinValue;

        /// <summary>Every ACPI event, raw: up to 8 bytes of EventDetail (already logged as well).</summary>
        public event Action<byte[]>? EventDetailReceived;
        /// <summary>
        /// detail[0]==7 && detail[1]==4. The caller decides what "Turbo mode" means; this
        /// watcher never touches WMI methods or hardware by itself.
        /// </summary>
        public event Action<byte[]>? TurboKeyRequested;

        /// <summary>True once the WMI subscription is live (evidence line is in shell.log).</summary>
        public bool IsSubscribed { get; private set; }
        /// <summary>Class that accepted the subscription (APGeEvent / AcerGenericEvent).</summary>
        public string? SubscribedClass { get; private set; }
        /// <summary>Why the subscription failed, when it did.</summary>
        public string? FailureReason { get; private set; }

        // ── Lifecycle ────────────────────────────────────────────────────────────
        public void Start()
        {
            lock (_gate)
            {
                if (_disposed || _thread is not null) return;
                _stop = new ManualResetEventSlim(false);
                _thread = new Thread(WatchThread)
                {
                    IsBackground = true,
                    Name = "Predex.AcerHotkey"
                };
                // ManagementEventWatcher callbacks are MTA-apathetic but Start/Stop and the
                // sink COM objects want a consistent MTA; PreySense.Core\TurboKeyWatcher does
                // the same (docs\KEY-EVENT-SOURCES.md §3).
                _thread.SetApartmentState(ApartmentState.MTA);
                _thread.Start();
            }
        }

        private void WatchThread()
        {
            try
            {
                if (!TrySubscribe(out ManagementEventWatcher? watcher, out string? error))
                {
                    FailureReason = error;
                    ShellLog.Write(LogArea,
                        $"SUBSCRIBE FAILED on {WmiNamespace} ({error}) — chassis keys stay unhandled");
                    return;
                }

                lock (_gate) _watcher = watcher;
                IsSubscribed = true;
                ShellLog.Write(LogArea,
                    $"subscribed {WmiNamespace}:{SubscribedClass}; waiting for events (APGeEvent primary, AcerGenericEvent fallback)");
                WarnIfOfficialStackRunning();
                _stop?.Wait();
            }
            catch (Exception ex)
            {
                FailureReason = $"{ex.GetType().Name}: {ex.Message}";
                ShellLog.Write(LogArea, $"watcher thread failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Unsubscribe();
            }
        }

        /// <summary>
        /// Subscribes APGeEvent, falling back to AcerGenericEvent. No WITHIN: these are
        /// extrinsic (dynamic) events, a polling interval would turn them into a 1 Hz query.
        /// </summary>
        private bool TrySubscribe(out ManagementEventWatcher? watcher, out string? error)
        {
            watcher = null;
            error = null;

            var scope = new ManagementScope(WmiNamespace);
            try
            {
                scope.Connect();
            }
            catch (Exception ex)
            {
                error = $"scope {WmiNamespace}: {ex.GetType().Name}: {ex.Message}";
                return false;
            }

            foreach (string className in EventClasses)
            {
                ManagementEventWatcher? candidate = null;
                try
                {
                    var query = new WqlEventQuery($"SELECT * FROM {className}");
                    candidate = new ManagementEventWatcher(scope, query);
                    candidate.EventArrived += OnEventArrived;
                    candidate.Start();

                    SubscribedClass = className;
                    watcher = candidate;
                    return true;
                }
                catch (Exception ex)
                {
                    // Class missing / access denied / provider not ready: log and try the next one.
                    error = $"{className}: {ex.GetType().Name}: {ex.Message}";
                    ShellLog.Write(LogArea,
                        $"class {className} unavailable ({ex.GetType().Name}: {ex.Message})");
                    if (candidate is not null)
                    {
                        try { candidate.EventArrived -= OnEventArrived; } catch { }
                        try { candidate.Stop(); } catch { }
                        try { candidate.Dispose(); } catch { }
                    }
                }
            }

            return false;
        }

        private void Unsubscribe()
        {
            ManagementEventWatcher? watcher;
            lock (_gate)
            {
                watcher = _watcher;
                _watcher = null;
            }
            if (watcher is null) return;

            try { watcher.EventArrived -= OnEventArrived; } catch { }
            try { watcher.Stop(); } catch { }
            try { watcher.Dispose(); } catch { }
            IsSubscribed = false;
            ShellLog.Write(LogArea, "unsubscribed");
        }

        // ── Event handling ───────────────────────────────────────────────────────
        private void OnEventArrived(object? sender, EventArrivedEventArgs e)
        {
            try
            {
                byte[] detail = ReadEventDetail(e.NewEvent, out string rawDescription, out bool parsed);

                // The identification line: exactly the 8 bytes, always, before any decoding.
                ShellLog.Write($"acer event: {FormatDetail(detail)}");

                if (!parsed)
                {
                    ShellLog.Write(LogArea, $"EventDetail not a byte array ({rawDescription}) — ignored");
                    return;
                }

                byte category = detail.Length > 0 ? detail[0] : (byte)0;
                byte sub = detail.Length > 1 ? detail[1] : (byte)0;
                byte arg = detail.Length > 2 ? detail[2] : (byte)0;
                ShellLog.Write(LogArea,
                    $"decoded category=0x{category:X2} sub=0x{sub:X2} arg=0x{arg:X2} ({Describe(category, sub, arg)})");

                EventDetailReceived?.Invoke(detail);

                if (category == CategoryGameKey && sub == TurboKeySubId)
                {
                    WarnIfOfficialStackRunning();
                    ShellLog.Write(LogArea,
                        "turbo/extreme key event → raising TurboKeyRequested");
                    TurboKeyRequested?.Invoke(detail);
                }
            }
            catch (Exception ex)
            {
                // A throwing callback on the WMI sink thread would kill the subscription.
                ShellLog.Write(LogArea, $"event handling failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Pulls EventDetail out of the WMI event object. It is VARIANT VT_ARRAY|VT_UI1, so
        /// .NET normally hands us a byte[]; the array/scalar fallbacks are there so an
        /// unparsable event still gets logged instead of dropped.
        /// </summary>
        private static byte[] ReadEventDetail(ManagementBaseObject? wmiEvent, out string rawDescription, out bool parsed)
        {
            rawDescription = "null";
            parsed = false;
            if (wmiEvent is null) return Array.Empty<byte>();

            object? raw = null;
            try { raw = wmiEvent.Properties["EventDetail"]?.Value; } catch { }
            if (raw is null)
            {
                try { raw = wmiEvent["EventDetail"]; } catch { }
            }

            rawDescription = raw is null ? "null" : $"{raw.GetType().Name}:{raw}";
            switch (raw)
            {
                case byte[] bytes:
                    parsed = true;
                    return bytes;
                case Array array when array.Rank == 1:
                {
                    var copy = new byte[array.Length];
                    for (int i = 0; i < array.Length; i++)
                    {
                        try { copy[i] = Convert.ToByte(array.GetValue(i)); }
                        catch { return Array.Empty<byte>(); }
                    }
                    parsed = true;
                    return copy;
                }
                case byte b:
                    parsed = true;
                    return new[] { b };
                case ushort us:
                    parsed = true;
                    return new[] { (byte)(us & 0xFF), (byte)((us >> 8) & 0xFF) };
                default:
                    return Array.Empty<byte>();
            }
        }

        private static string FormatDetail(byte[] detail)
        {
            if (detail.Length == 0) return "(empty)";
            var text = new StringBuilder(detail.Length * 3);
            for (int i = 0; i < detail.Length; i++)
            {
                if (i > 0) text.Append(' ');
                text.Append(detail[i].ToString("X2"));
            }
            return text.ToString();
        }

        /// <summary>Human label for the categories PSAdminAgent dispatches on (KEY-EVENT-SOURCES §2.1).</summary>
        private static string Describe(byte category, byte sub, byte arg) => category switch
        {
            0x01 => "keyboard backlight state change",
            0x07 => sub switch
            {
                ProfileNotifySubId => "hotkey profile notify",
                CustomKeySubId => $"custom key #{arg}",
                TurboKeySubId => "TURBO / extreme mode key",
                _ => "game key family (unknown sub id)"
            },
            0x08 => "power source change (bit0=AC, bit2..3=PD)",
            0x09 => "notify 0x8005",
            0x0C => "advanced-setting hotkey",
            _ => "unknown category"
        };

        // ── Official-stack mutual exclusion (warn only, never kill) ───────────────
        private void WarnIfOfficialStackRunning()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastOfficialStackCheckUtc).TotalSeconds < 5) return;
            _lastOfficialStackCheckUtc = now;

            var found = new List<string>();
            foreach (string name in OfficialConsumers)
            {
                try
                {
                    Process[] processes = Process.GetProcessesByName(name);
                    if (processes.Length == 0) continue;
                    foreach (Process process in processes)
                    {
                        try { found.Add($"{name}(pid {process.Id})"); }
                        finally { process.Dispose(); }
                    }
                }
                catch
                {
                    // WMI/perf-counter hiccup while enumerating: not worth a log line.
                }
            }

            if (found.Count == 0) return;
            ShellLog.Write(LogArea,
                $"WARNING official Acer stack running: {string.Join(", ", found)} — the same ACPI event is handled twice " +
                "(PSAdminAgent TsWmiEventSink + our watcher, AOD_WMI_HANDLER_MUTEX); close the official Predator Sense GUI before testing. Not killing it.");
        }

        // ── Teardown ─────────────────────────────────────────────────────────────
        public void Stop()
        {
            Thread? thread;
            lock (_gate)
            {
                _stop?.Set();
                thread = _thread;
                _thread = null;
            }

            if (thread is not null && thread.IsAlive && !thread.Join(1500))
                ShellLog.Write(LogArea, "watcher thread did not stop promptly");

            lock (_gate)
            {
                _stop?.Dispose();
                _stop = null;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            Stop();
        }
    }
}
