// Shell motion: show / hide / page transition, plus the shared easing used by the
// window-size animation in ShellWindow.xaml.cs.
//
// Ported from the S0 MainWindow.Animation.cs (that Composition approach was the one good
// part of the old shell) with two changes:
//  • the animated visual is the new edge-to-edge RootGrid, not the old padded ContentPanel;
//  • the slide distance is a small lift (~24 DIP) instead of a full window height, because
//    the window is now a large main window rather than a small popup.
// All motion runs on the compositor thread; only the hide completion callback hops back.

using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;

namespace Predex.WinUI.Shell
{
    public sealed partial class ShellWindow
    {
        private Compositor? _compositor;
        private Visual? _rootVisual;
        private Visual? _pageVisual;

        private void EnsureCompositor()
        {
            if (_compositor is not null) return;
            _rootVisual = ElementCompositionPreview.GetElementVisual(RootGrid);
            _compositor = _rootVisual.Compositor;
            _rootVisual.Clip = _compositor.CreateInsetClip(0, 0, 0, 0);
        }

        /// <summary>cubic-bezier(0.16, 1, 0.3, 1) — fast in, slow settle (system popup feel).</summary>
        internal static double CubicBezierEase(double progress)
        {
            const double X1 = 0.16, Y1 = 1.0, X2 = 0.3, Y2 = 1.0;
            double low = 0, high = 1, t = progress;
            for (int i = 0; i < 20; i++)
            {
                t = (low + high) / 2;
                if (BezierValue(X1, X2, t) < progress) low = t;
                else high = t;
            }
            return BezierValue(Y1, Y2, t);
        }

        private static double BezierValue(double first, double second, double t)
        {
            double inverse = 1 - t;
            return (3 * inverse * inverse * t * first) + (3 * inverse * t * t * second) + (t * t * t);
        }

        internal void PlayShowAnimation()
        {
            EnsureCompositor();
            float lift = 24f;

            var slide = _compositor!.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0f, new Vector3(0, lift, 0));
            slide.InsertKeyFrame(1f, Vector3.Zero,
                _compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.16f, 1.0f), new Vector2(0.30f, 1.0f)));
            slide.Duration = TimeSpan.FromMilliseconds(260);

            var fade = _compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f,
                _compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1.0f)));
            fade.Duration = TimeSpan.FromMilliseconds(180);

            _rootVisual!.StartAnimation("Offset", slide);
            _rootVisual.StartAnimation("Opacity", fade);
        }

        internal void PlayHideAnimation(Action? onComplete = null)
        {
            EnsureCompositor();
            float lift = 18f;

            var slide = _compositor!.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0f, Vector3.Zero);
            slide.InsertKeyFrame(1f, new Vector3(0, lift, 0),
                _compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.4f, 0f), new Vector2(1f, 1f)));
            slide.Duration = TimeSpan.FromMilliseconds(160);

            var fade = _compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 1f);
            fade.InsertKeyFrame(1f, 0f,
                _compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.4f, 0f), new Vector2(1f, 1f)));
            fade.Duration = TimeSpan.FromMilliseconds(140);

            var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            _rootVisual!.StartAnimation("Offset", slide);
            _rootVisual.StartAnimation("Opacity", fade);
            batch.End();

            batch.Completed += (_, _) =>
            {
                // Reset so the next show starts from a clean visual state.
                _rootVisual!.Offset = Vector3.Zero;
                _rootVisual.Opacity = 1;
                onComplete?.Invoke();
            };
        }

        /// <summary>Cross-fade when the page host swaps content.</summary>
        internal void PlayPageTransition()
        {
            EnsureCompositor();
            _pageVisual ??= ElementCompositionPreview.GetElementVisual(PageHost);

            // Opacity only: animating "Offset" on this visual would override the layout
            // offset of the host (y = title bar height) and drag the page up into the
            // title bar. Layout offsets must be left alone unless the element sits at (0,0).
            var fade = _compositor!.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f,
                _compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.16f, 1.0f), new Vector2(0.30f, 1.0f)));
            fade.Duration = TimeSpan.FromMilliseconds(160);

            _pageVisual.StartAnimation("Opacity", fade);
        }
    }
}
