// Idempotent INotifyPropertyChanged subscription owned by a shell page.
//
// Why this exists (fans-page RPM freeze): the shell caches page instances in a dictionary
// (Shell\ShellWindow.xaml.cs, `_pages`), so Loaded / Unloaded fire many times on the SAME
// page object and `Vm` is assigned exactly once, at construction. A page that subscribes only
// inside the `Vm` setter therefore loses the subscription on the first Unloaded and never gets
// it back — the setter returns early (`if (ReferenceEquals(_vm, value)) return;`), so the
// second `Vm ??= ...` in OnLoaded never re-subscribes. The page then shows stale text until
// something recreates it.
//
// Attach() is safe to call on every Loaded (it never leaves more than one live subscription)
// and Detach() is safe on every Unloaded, so neither path depends on "did the setter run".
//
// No WinUI, no hardware, no static state: this file is linked into Performix.Tests, where the
// attach/detach bookkeeping (the part that was wrong) is unit-tested directly.

using System;
using System.ComponentModel;

namespace Predex.WinUI.Services
{
    /// <summary>
    /// One-slot PropertyChanged subscription: <see cref="Attach"/> replaces, never duplicates.
    /// </summary>
    public sealed class PropertyChangedSubscription
    {
        private INotifyPropertyChanged? _source;
        private PropertyChangedEventHandler? _handler;

        /// <summary>Attach calls that actually registered a handler (diagnostics / tests).</summary>
        public int AttachCount { get; private set; }

        /// <summary>Detach calls that actually removed a handler (diagnostics / tests).</summary>
        public int DetachCount { get; private set; }

        public bool IsAttached => _source is not null;

        public INotifyPropertyChanged? Source => _source;

        /// <summary>
        /// Subscribe to <paramref name="source"/>. Calling it again (same or different source,
        /// same or different handler) first drops the previous subscription, so the page can
        /// call it from every Loaded without accumulating duplicate handlers.
        /// </summary>
        public void Attach(INotifyPropertyChanged? source, PropertyChangedEventHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            Detach();
            if (source is null) return;

            _source = source;
            _handler = handler;
            source.PropertyChanged += handler;
            AttachCount++;
        }

        /// <summary>Unsubscribe. A no-op when nothing is attached.</summary>
        public void Detach()
        {
            if (_source is null) return;

            PropertyChangedEventHandler? handler = _handler;
            if (handler is not null) _source.PropertyChanged -= handler;
            _source = null;
            _handler = null;
            DetachCount++;
        }
    }
}
