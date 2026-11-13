using System;
using System.Windows;

namespace PreySense.Frontend.Services;

/// <summary>
/// Keeps a window inside the desktop work area. CenterOwner/CenterScreen use the owner's
/// unsized rect, so tall windows can open partly off-screen on high-DPI displays.
/// </summary>
public static class WindowPlacement
{
    public static readonly DependencyProperty EnsureVisibleProperty = DependencyProperty.RegisterAttached(
        "EnsureVisible",
        typeof(bool),
        typeof(WindowPlacement),
        new PropertyMetadata(false, OnEnsureVisibleChanged));

    public static void SetEnsureVisible(DependencyObject element, bool value) =>
        element.SetValue(EnsureVisibleProperty, value);

    public static bool GetEnsureVisible(DependencyObject element) =>
        (bool)element.GetValue(EnsureVisibleProperty);

    private static void OnEnsureVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window) return;
        if (e.NewValue is true)
            window.Loaded += Window_Loaded;
        else
            window.Loaded -= Window_Loaded;
    }

    private static void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
            EnsureWithinWorkArea(window);
    }

    internal static void EnsureWithinWorkArea(Window window)
    {
        Rect work = SystemParameters.WorkArea;
        double width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        double height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0)
            return;

        if (double.IsNaN(window.Left) || double.IsNaN(window.Top))
            return;

        double left = window.Left;
        double top = window.Top;

        if (left + width > work.Right) left = work.Right - width;
        if (top + height > work.Bottom) top = work.Bottom - height;
        if (left < work.Left) left = work.Left;
        if (top < work.Top) top = work.Top;

        if (Math.Abs(left - window.Left) > 0.5) window.Left = left;
        if (Math.Abs(top - window.Top) > 0.5) window.Top = top;
    }
}
