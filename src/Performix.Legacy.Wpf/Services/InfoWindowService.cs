using System;
using System.Windows;
using PreySense.Frontend.Views;

namespace PreySense.Frontend.Services
{
    /// <summary>
    /// 右下角信息小窗的单例入口。任何线程都可调用，内部自动切到 UI 线程。
    /// </summary>
    public static class InfoWindowService
    {
        private static readonly object Gate = new();
        private static InfoWindow? _window;

        /// <summary>结果提示：3 秒后自动收起（Warning 同）。</summary>
        public static void ShowSuccess(string title, string? detail = null) => ShowResult(InfoOutcome.Success, title, detail);

        public static void ShowFailure(string title, string? detail = null) => ShowResult(InfoOutcome.Failure, title, detail);

        public static void ShowWarning(string title, string? detail = null) => ShowResult(InfoOutcome.Warning, title, detail);

        /// <summary>普通信息：8 秒后自动收起。</summary>
        public static void ShowInformation(string title, string? detail = null) => ShowResult(InfoOutcome.Information, title, detail);

        public static void ShowResult(InfoOutcome outcome, string title, string? detail = null)
        {
            RunOnUi(() =>
            {
                InfoWindow window = EnsureWindow();
                window.CancelAutoHide();
                window.Apply(outcome, title, detail, busy: false, progress: null);
                Present(window);
                window.ScheduleAutoHide(window.HoldSeconds);
            });
        }

        /// <summary>进行中：常驻，直到 <see cref="Dismiss"/> 或被新的结果替换。</summary>
        public static void ShowWorking(string title, string? detail = null, double? progress = null)
        {
            RunOnUi(() =>
            {
                InfoWindow window = EnsureWindow();
                window.CancelAutoHide();
                window.Apply(InfoOutcome.Information, title, detail, busy: true, progress: progress);
                Present(window);
            });
        }

        /// <summary>更新进度条数值（不改变其它内容）。</summary>
        public static void SetProgress(double percent, string? detail = null)
        {
            RunOnUi(() =>
            {
                if (_window is null) return;
                if (detail is not null) _window.SetDetail(detail);
                _window.SetProgressValue(percent);
            });
        }

        public static void Dismiss()
        {
            RunOnUi(() => { if (_window is not null) Dismiss(_window); });
        }

        internal static void Dismiss(InfoWindow window)
        {
            window.CancelAutoHide();
            window.PlayHide(() =>
            {
                window.Hide();
            });
        }

        private static void Present(InfoWindow window)
        {
            if (!window.IsVisible)
            {
                window.AlignToBottomRight();
                window.Show();
                window.PlayShow();
            }
            else
            {
                window.PlayShow();
            }
        }

        private static InfoWindow EnsureWindow()
        {
            lock (Gate)
            {
                if (_window is null)
                {
                    _window = new InfoWindow();
                    _window.Closed += (_, _) => { lock (Gate) _window = null; };
                }
                return _window;
            }
        }

        private static void RunOnUi(Action action)
        {
            Application? app = Application.Current;
            if (app is null) return;
            if (app.Dispatcher.CheckAccess()) action();
            else app.Dispatcher.BeginInvoke(action);
        }
    }
}
