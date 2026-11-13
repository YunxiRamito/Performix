// WPF compatibility shims — allow the linked WPF ViewModel files to compile
// under WinUI 3 without modification. These types shadow the real WPF types;
// no WPF DLL is ever loaded. All dispatch is forwarded to DispatcherQueue.

using Microsoft.UI.Dispatching;

#pragma warning disable IDE0130 // Namespace does not match folder structure

// ── System.Windows.Media.RenderOptions shim (used by MainViewModel.Gpu.cs) ───
// Under WinUI 3 there is no ProcessRenderMode toggle; these are no-ops.
namespace System.Windows.Media
{
    internal enum RenderMode { Default = 0, SoftwareOnly = 1 }

    internal static class RenderOptions
    {
        // Backing field so get/set round-trips correctly even though it's meaningless.
        private static RenderMode _processRenderMode = RenderMode.Default;
        public static RenderMode ProcessRenderMode
        {
            get => _processRenderMode;
            set => _processRenderMode = value;
        }
    }
}

// ── System.Windows.Interop.RenderMode shim ───────────────────────────────────
namespace System.Windows.Interop
{
    // The linked code writes `System.Windows.Interop.RenderMode.SoftwareOnly`.
    using System.Windows.Media;
    internal static class RenderMode
    {
        public static readonly System.Windows.Media.RenderMode SoftwareOnly =
            System.Windows.Media.RenderMode.SoftwareOnly;
    }
}

// ── System.Windows.Threading shim ────────────────────────────────────────────
namespace System.Windows.Threading
{
    public enum DispatcherPriority
    {
        Inactive = 0,
        SystemIdle = 1,
        ApplicationIdle = 2,
        ContextIdle = 3,
        Background = 4,
        Input = 5,
        Loaded = 6,
        Render = 7,
        DataBind = 8,
        Normal = 9,
        Send = 10,
    }

    internal sealed class DispatcherOperation
    {
        public DispatcherOperation(System.Threading.Tasks.Task task) => Task = task;
        public System.Threading.Tasks.Task Task { get; }
    }

    // 与 WPF 的 DispatcherOperation<T> 对应：InvokeAsync(Func<T>) 的 .Task 类型是 Task<T>。
    internal sealed class DispatcherOperation<T>
    {
        public DispatcherOperation(System.Threading.Tasks.Task<T> task) => Task = task;
        public System.Threading.Tasks.Task<T> Task { get; }
    }

    internal sealed class Dispatcher
    {
        private readonly DispatcherQueue _queue;

        private Dispatcher(DispatcherQueue queue) => _queue = queue;

        // ── UI-thread queue capture ─────────────────────────────────────────────
        // WPF's Dispatcher.CurrentDispatcher has *process* semantics for the UI thread:
        // App.Current.Dispatcher (and Dispatcher.CurrentDispatcher from a worker) hands back
        // the UI dispatcher and InvokeAsync() posts back to the UI. WinUI 3 has only a
        // per-thread DispatcherQueue, so a background thread sees null — which used to throw
        // here. The linked WPF ViewModels hit exactly that: MainViewModel.OnPowerModeChanged
        // (MainViewModel.cs:303) does `App.Current?.Dispatcher?.InvokeAsync(...)` from the
        // hardware service's PowerModeChanged callback (a worker thread), which is raised from
        // inside `_hw.SetPowerMode(...)` — the throw then escaped SetPowerMode and surfaced as
        // the "模式切换失败 / Dispatcher.CurrentDispatcher called off the UI thread" error box.
        //
        // Fix: App.OnLaunched captures the UI queue once; the getter falls back to it.
        private static volatile DispatcherQueue? _uiQueue;
        private static int _uiQueueThreadId;
        private static int _fallbackCount;

        /// <summary>The UI-thread queue captured by <see cref="CaptureUiQueue"/> (null before then).</summary>
        internal static DispatcherQueue? UiQueue => _uiQueue;

        /// <summary>
        /// Captures the UI thread's DispatcherQueue. Must be called on the UI thread, once,
        /// as early as possible (App.OnLaunched, before the shell / ViewModels exist).
        /// </summary>
        internal static bool CaptureUiQueue(DispatcherQueue? queue)
        {
            if (queue is null)
            {
                Predex.WinUI.Services.ShellLog.Write("compat",
                    $"CaptureUiQueue: no DispatcherQueue for thread {Environment.CurrentManagedThreadId} — not the UI thread?");
                return false;
            }

            // Idempotent: a second call keeps the first (the real UI queue) with its thread id.
            if (_uiQueue is not null) return true;

            _uiQueueThreadId = Environment.CurrentManagedThreadId;
            _uiQueue = queue;
            Predex.WinUI.Services.ShellLog.Write("compat",
                $"captured UI DispatcherQueue in Dispatcher.UiQueue (thread {_uiQueueThreadId})");
            return true;
        }

        /// <summary>
        /// WPF semantics: the current thread's dispatcher when there is one, otherwise the
        /// UI dispatcher (so worker-thread `App.Current.Dispatcher.InvokeAsync(...)` marshals
        /// back to the UI instead of throwing).
        /// </summary>
        public static Dispatcher CurrentDispatcher
        {
            get
            {
                var forThread = DispatcherQueue.GetForCurrentThread();
                if (forThread is not null)
                    return new Dispatcher(forThread);

                DispatcherQueue? uiQueue = _uiQueue;
                if (uiQueue is not null)
                {
                    // Bounded trace: a handful of lines pin down which worker-thread call-sites
                    // rely on the fallback, without spamming shell.log on every lookup.
                    int seen = Interlocked.Increment(ref _fallbackCount);
                    if (seen <= 4)
                    {
                        string caller = seen == 1 ? $" caller={DescribeCaller()}" : string.Empty;
                        Predex.WinUI.Services.ShellLog.Write("compat",
                            $"Dispatcher.CurrentDispatcher off the UI thread (thread {Environment.CurrentManagedThreadId}) → using captured UI queue (thread {_uiQueueThreadId}); InvokeAsync will marshal back [{seen}/4]{caller}");
                    }
                    return new Dispatcher(uiQueue);
                }

                throw new InvalidOperationException(
                    $"Dispatcher.CurrentDispatcher has no UI thread to dispatch to: called on thread " +
                    $"{Environment.CurrentManagedThreadId} (IsUiThread=false) and no UI DispatcherQueue was " +
                    $"captured (App.OnLaunched → Dispatcher.CaptureUiQueue). This means the call happens " +
                    $"before the shell starts or after the UI thread is gone. Callers: {DescribeCaller()}");
            }
        }

        /// <summary>
        /// "A.B ← C.D ← …" for the last few frames — enough to identify a WPF-style call-site
        /// (e.g. <c>MainViewModel.OnPowerModeChanged</c>) in shell.log / the exception message.
        /// </summary>
        private static string DescribeCaller()
        {
            try
            {
                var frames = new System.Diagnostics.StackTrace(skipFrames: 2, fNeedFileInfo: false);
                var names = new System.Collections.Generic.List<string>(4);
                for (int i = 0; i < frames.FrameCount && names.Count < 4; i++)
                {
                    System.Reflection.MethodBase? method = frames.GetFrame(i)?.GetMethod();
                    if (method is null) continue;
                    string? type = method.DeclaringType?.Name;
                    names.Add(type is null ? method.Name : $"{type}.{method.Name}");
                }
                return names.Count == 0 ? "<unknown>" : string.Join(" ← ", names);
            }
            catch (Exception ex)
            {
                return $"<frames unavailable: {ex.GetType().Name}>";
            }
        }

        public bool CheckAccess() => _queue.HasThreadAccess;
        public bool HasShutdownStarted => false;
        public bool HasShutdownFinished => false;

        public DispatcherOperation BeginInvoke(Action callback) => Enqueue(DispatcherPriority.Normal, callback);

        public DispatcherOperation BeginInvoke(DispatcherPriority priority, Action callback) => Enqueue(priority, callback);

        // Delegate overload used by some WPF call-sites: BeginInvoke(priority, delegate, arg)
        public DispatcherOperation BeginInvoke(DispatcherPriority priority, Delegate method, object? arg = null) =>
            Enqueue(priority, () => method.DynamicInvoke(arg));

        private DispatcherOperation Enqueue(DispatcherPriority priority, Action callback)
        {
            _queue.TryEnqueue(MapPriority(priority), new DispatcherQueueHandler(() => callback()));
            return new DispatcherOperation(System.Threading.Tasks.Task.CompletedTask);
        }

        public System.Threading.Tasks.Task InvokeAsync(Action callback) => InvokeAsync(callback, DispatcherPriority.Normal);

        public System.Threading.Tasks.Task InvokeAsync(Action callback, DispatcherPriority priority)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource();
            _queue.TryEnqueue(MapPriority(priority), () =>
            {
                try { callback(); tcs.SetResult(); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        // WPF 的 InvokeAsync(Func<Task>) 返回 DispatcherOperation<Task>，
        // 其 .Task 是 Task<Task>，调用方用 .Task.Unwrap() 展平。
        public DispatcherOperation<System.Threading.Tasks.Task> InvokeAsync(Func<System.Threading.Tasks.Task> callback) =>
            InvokeAsync(callback, DispatcherPriority.Normal);

        public DispatcherOperation<System.Threading.Tasks.Task> InvokeAsync(Func<System.Threading.Tasks.Task> callback, DispatcherPriority priority)
        {
            var inner = new System.Threading.Tasks.TaskCompletionSource<System.Threading.Tasks.Task>();
            _queue.TryEnqueue(MapPriority(priority), () =>
            {
                try { inner.SetResult(callback()); }
                catch (Exception ex) { inner.SetException(ex); }
            });
            return new DispatcherOperation<System.Threading.Tasks.Task>(inner.Task);
        }

        public void Invoke(Action callback)
        {
            if (CheckAccess()) { callback(); return; }
            var done = new System.Threading.ManualResetEventSlim(false);
            Exception? error = null;
            _queue.TryEnqueue(() =>
            {
                try { callback(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            done.Wait();
            if (error is not null) throw error;
        }

        public System.Threading.Tasks.Task<T> InvokeAsync<T>(Func<T> callback)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<T>();
            _queue.TryEnqueue(() =>
            {
                try { tcs.SetResult(callback()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        private static DispatcherQueuePriority MapPriority(DispatcherPriority p) => p switch
        {
            DispatcherPriority.Background   => DispatcherQueuePriority.Low,
            DispatcherPriority.Input        => DispatcherQueuePriority.Normal,
            DispatcherPriority.Normal       => DispatcherQueuePriority.Normal,
            DispatcherPriority.Send         => DispatcherQueuePriority.High,
            _                               => DispatcherQueuePriority.Normal,
        };
    }

    // DispatcherTimer shim — wraps DispatcherQueueTimer so UiDebouncer / other
    // WPF-style timers compile and behave correctly under WinUI 3.
    internal sealed class DispatcherTimer : IDisposable
    {
        private DispatcherQueueTimer? _inner;
        private TimeSpan _interval = TimeSpan.FromSeconds(1);

        public event EventHandler? Tick;
        public bool IsEnabled { get; private set; }

        public TimeSpan Interval
        {
            get => _interval;
            set
            {
                _interval = value;
                if (_inner is not null) _inner.Interval = value;
            }
        }

        public DispatcherTimer()
        {
            // Same fallback as CurrentDispatcher: a timer built off the UI thread must still
            // tick on the UI thread, not silently never fire (UiDebouncer relies on that).
            var q = DispatcherQueue.GetForCurrentThread() ?? Dispatcher.UiQueue;
            if (q is not null)
            {
                _inner = q.CreateTimer();
                _inner.IsRepeating = true;
                _inner.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Predex.WinUI.Services.ShellLog.Write("compat",
                    $"DispatcherTimer created on thread {Environment.CurrentManagedThreadId} before the UI queue was captured — it will never tick");
            }
        }

        // Additional constructor that matches WPF's DispatcherTimer(DispatcherPriority).
        public DispatcherTimer(DispatcherPriority priority) : this() { }

        public void Start()
        {
            IsEnabled = true;
            if (_inner is null) return;
            _inner.Interval = _interval;
            _inner.IsRepeating = true;
            _inner.Start();
        }

        public void Stop()
        {
            IsEnabled = false;
            _inner?.Stop();
        }

        public void Dispose() => _inner?.Stop();
    }
}

// ── System.Windows shim (Application.Current.Dispatcher pattern) ─────────────
namespace System.Windows
{
    // Minimal shim so any service/VM that calls
    // Application.Current.Dispatcher.BeginInvoke(...) compiles.
    internal sealed class Application
    {
        private static Application? _current;
        public static Application? Current => _current;

        public Threading.Dispatcher Dispatcher =>
            Threading.Dispatcher.CurrentDispatcher;

        // WPF 版把窗口对象赋给 Owner；WinUI 3 没有对应概念，占位即可。
        public object? MainWindow { get; set; }

        // Called once from App.xaml.cs after the WinUI 3 app is running.
        internal static void SetCurrent() => _current ??= new Application();
    }
}
