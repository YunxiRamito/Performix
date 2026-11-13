using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PreySense.Frontend.Services;

namespace PreySense.Frontend.Views
{
    /// <summary>信息窗结果类型。</summary>
    public enum InfoOutcome
    {
        Success,
        Failure,
        Warning,
        Information
    }

    /// <summary>
    /// 右下角信息小窗（仿 Dafeiyu-Go 的 InfoWindow 设计）：
    /// 无边框、置顶、不可缩放，钉在工作区右下角；内容高度自适应，滑入/滑出。
    /// 三种用法：结果提示（自动收起）、进行中（常驻）、进度（带进度条）。
    /// </summary>
    public partial class InfoWindow : Window
    {
        private const double ScreenMargin = 16;
        private const int SlideDurationMs = 220;
        private const int ResultHoldSeconds = 3;
        private const int InformationHoldSeconds = 8;

        private DispatcherTimer? _hideTimer;
        private bool _closing;
        private readonly TranslateTransform _shellShift = new(0, 0);

        public InfoWindow()
        {
            InitializeComponent();
            Opacity = 0;
            // Window 本身不接受 RenderTransform（WPF 会抛 InvalidOperationException），
            // 位移动画必须作用在子元素上。
            Shell.RenderTransform = _shellShift;
        }

        internal InfoOutcome Outcome { get; private set; }

        internal bool IsBusy { get; private set; }

        /// <summary>应用一次内容；不负责显示/隐藏，由 <c>InfoWindowService</c> 协调。</summary>
        internal void Apply(InfoOutcome outcome, string title, string? detail, bool busy, double? progress)
        {
            Outcome = outcome;
            IsBusy = busy;

            TitleText.Text = title;
            DetailText.Text = detail ?? string.Empty;
            DetailText.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;

            Brush accent = outcome switch
            {
                InfoOutcome.Success => (Brush)FindResource("SuccessBrush"),
                InfoOutcome.Failure => (Brush)FindResource("DangerBrush"),
                InfoOutcome.Warning => (Brush)FindResource("WarningBrush"),
                _ => (Brush)FindResource("AccentBrush")
            };

            Dot.Fill = accent;
            Ring.Stroke = accent;
            Bar.Foreground = accent;

            // 进行中：圆点淡出、转圈淡入 + 旋转
            Dot.Opacity = busy ? 0 : 1;
            Ring.Opacity = busy ? 1 : 0;
            if (busy) StartRingSpin(); else StopRingSpin();

            if (progress is double value)
            {
                Bar.Visibility = Visibility.Visible;
                Bar.IsIndeterminate = false;
                Bar.Value = Math.Clamp(value, 0, 100);
            }
            else if (busy)
            {
                Bar.Visibility = Visibility.Visible;
                Bar.IsIndeterminate = true;
            }
            else
            {
                Bar.Visibility = Visibility.Collapsed;
                Bar.IsIndeterminate = false;
            }

            // 高度自适应后重新贴右下角
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(AlignToBottomRight));
        }

        internal int HoldSeconds => IsBusy
            ? 0
            : Outcome == InfoOutcome.Information ? InformationHoldSeconds : ResultHoldSeconds;

        internal void ScheduleAutoHide(int seconds)
        {
            _hideTimer?.Stop();
            if (seconds <= 0) return;
            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _hideTimer.Tick += (_, _) =>
            {
                _hideTimer?.Stop();
                InfoWindowService.Dismiss(this);
            };
            _hideTimer.Start();
        }

        internal void CancelAutoHide() => _hideTimer?.Stop();

        internal void SetDetail(string text)
        {
            DetailText.Text = text;
            DetailText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(AlignToBottomRight));
        }

        internal void SetProgressValue(double percent)
        {
            Bar.Visibility = Visibility.Visible;
            Bar.IsIndeterminate = false;
            Bar.Value = Math.Clamp(percent, 0, 100);
        }

        internal void AlignToBottomRight()
        {
            Rect work = SystemParameters.WorkArea;
            // 先让内容尺寸确定（SizeToContent 生效），再定位
            UpdateLayout();
            Left = work.Right - ActualWidth - ScreenMargin;
            Top = work.Bottom - ActualHeight - ScreenMargin;
        }

        internal void PlayShow()
        {
            _closing = false;
            var slide = new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(SlideDurationMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(SlideDurationMs));
            _shellShift.BeginAnimation(TranslateTransform.XProperty, slide);
            BeginAnimation(OpacityProperty, fade);
        }

        internal void PlayHide(Action? completed)
        {
            if (_closing) return;
            _closing = true;
            CancelAutoHide();
            StopRingSpin();

            var slide = new DoubleAnimation(0, 32, TimeSpan.FromMilliseconds(SlideDurationMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(SlideDurationMs));
            if (completed != null) fade.Completed += (_, _) => completed();
            _shellShift.BeginAnimation(TranslateTransform.XProperty, slide);
            BeginAnimation(OpacityProperty, fade);
        }

        private void StartRingSpin()
        {
            RingRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
        }

        private void StopRingSpin() => RingRotate.BeginAnimation(RotateTransform.AngleProperty, null);

        protected override void OnClosed(EventArgs e)
        {
            CancelAutoHide();
            StopRingSpin();
            base.OnClosed(e);
        }
    }
}
