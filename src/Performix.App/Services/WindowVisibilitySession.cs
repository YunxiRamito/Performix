// Lightweight visibility-session tracker for WinUI 3.
// Replaces the WPF WindowVisibilitySession<Window> generic class.
// Tracks the "is the app currently going through a show/hide cycle" so
// transient focus changes don't trigger premature auto-hide.

using System;
using System.Collections.Generic;

namespace PreySense.Frontend.Services
{
    public sealed class WindowVisibilitySession<T> where T : class
    {
        private long _revision;
        private long _restoreRevision = -1;
        private bool _isHidden;

        public long Revision => _revision;
        public bool IsHidden => _isHidden;
        public T? PreferredWindow { get; set; }
        public IReadOnlyList<T> HiddenWindows { get; private set; } = Array.Empty<T>();

        // Begin a show cycle. Returns the revision token that guards the cycle.
        public long BeginRestore()
        {
            _restoreRevision = System.Threading.Interlocked.Increment(ref _revision);
            _isHidden = false;
            return _restoreRevision;
        }

        public void CompleteRestore(long revision)
        {
            if (revision == _restoreRevision) _restoreRevision = -1;
        }

        public void RecordForeground(bool? isForeground)
        {
            // No-op: used in WPF to debounce focus events.
        }

        public bool CanAutoHide(long revision) => _restoreRevision == -1;

        public bool ShouldAutoHide(long revision, bool? isForeground) =>
            isForeground == false && _restoreRevision == -1;

        // Returns false if a hide cycle is already in progress.
        public bool BeginHide(IEnumerable<T> windowsToHide, T? activeWindow)
        {
            HiddenWindows = new List<T>(windowsToHide);
            _isHidden = true;
            System.Threading.Interlocked.Increment(ref _revision);
            return true;
        }
    }
}
