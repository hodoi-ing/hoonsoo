using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace Hoonsoo;

public static class RegionSelector
{
    /// <summary>
    /// A drag that reports why it ended. Every ending used to collapse into "null", so a refused drag was
    /// indistinguishable from pressing ESC — the caller returned without a word and the feature looked like
    /// it had never opened (see <see cref="RegionSelection.Guidance"/>).
    /// </summary>
    internal static async Task<(Rectangle? Rect, RegionSelectionOutcome Outcome)> SelectDetailedAsync(CancellationToken token)
    {
        var completion = new TaskCompletionSource<(Rectangle?, RegionSelectionOutcome)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = new List<Window>();
        Native.Point? start = null;
        var borders = new List<(Window Window, System.Windows.Shapes.Rectangle Border)>();
        DispatcherTimer? escapePoll = null;

        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var canvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(160, 20, 20, 20)) };

            var banner = new Border
            {
                Background = Theme.BrushBgCard,
                BorderBrush = Theme.BrushBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18, 12, 18, 12),
                Margin = new Thickness(24)
            };
            var bannerText = new TextBlock
            {
                Text = "번역할 영역을 드래그하세요 · ESC 취소",
                Foreground = Theme.BrushTextPrimary,
                FontFamily = Ui.AppFont,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold
            };
            banner.Child = bannerText;
            canvas.Children.Add(banner);

            var border = new System.Windows.Shapes.Rectangle
            {
                Stroke = Theme.BrushAccent,
                StrokeThickness = 2.5,
                Fill = new SolidColorBrush(Color.FromArgb(40, 16, 163, 127))
            };
            canvas.Children.Add(border);

            var window = new Window
            {
                Content = canvas,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = true,
                Cursor = Cursors.Cross
            };

            window.SourceInitialized += (_, _) => Native.SetWindowPos(new WindowInteropHelper(window).Handle, new IntPtr(-1), screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height, 0x10);
            window.MouseLeftButtonDown += (_, _) => { Native.GetCursorPos(out var p); start = p; window.CaptureMouse(); };
            window.MouseMove += (_, _) =>
            {
                if (start is not { } first) return;
                Native.GetCursorPos(out var p);
                foreach (var (w, b) in borders)
                {
                    var a = w.PointFromScreen(new System.Windows.Point(Math.Min(first.X, p.X), Math.Min(first.Y, p.Y)));
                    var z = w.PointFromScreen(new System.Windows.Point(Math.Max(first.X, p.X), Math.Max(first.Y, p.Y)));
                    Canvas.SetLeft(b, a.X);
                    Canvas.SetTop(b, a.Y);
                    b.Width = z.X - a.X;
                    b.Height = z.Y - a.Y;
                }
            };
            window.MouseLeftButtonUp += (_, _) =>
            {
                if (start is not { } first) return;
                Native.GetCursorPos(out var p);
                var rect = Rectangle.FromLTRB(Math.Min(first.X, p.X), Math.Min(first.Y, p.Y), Math.Max(first.X, p.X), Math.Max(first.Y, p.Y));
                if (rect.Width < 8 || rect.Height < 8) completion.TrySetResult((null, RegionSelectionOutcome.TooSmall));
                else if ((long)rect.Width * rect.Height > 12000000) completion.TrySetResult((null, RegionSelectionOutcome.TooLarge));
                else completion.TrySetResult((rect, RegionSelectionOutcome.Selected));
            };
            window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) completion.TrySetResult((null, RegionSelectionOutcome.Cancelled)); };
            window.Closed += (_, _) => completion.TrySetResult((null, RegionSelectionOutcome.Cancelled));
            windows.Add(window);
            borders.Add((window, border));
        }

        using var registration = token.Register(() => completion.TrySetCanceled(token));
        try
        {
            if (windows.Count == 0) return (null, RegionSelectionOutcome.Unavailable);
            foreach (var w in windows) w.Show();
            Raise(windows[0]);

            // ESC is polled as well as handled as a key: these windows are shown by a process that is not
            // the foreground one, so they do not necessarily own the focus, and another surface's low-level
            // hook can swallow the key before WPF ever sees it.
            escapePoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            escapePoll.Tick += (_, _) => { if ((Native.GetAsyncKeyState(0x1B) & 0x8000) != 0) completion.TrySetResult((null, RegionSelectionOutcome.Cancelled)); };
            escapePoll.Start();
            return await completion.Task;
        }
        finally
        {
            escapePoll?.Stop();
            foreach (var w in windows)
            {
                w.ReleaseMouseCapture();
                w.Close();
            }
        }
    }

    /// <summary>
    /// Puts the selection surface in front of whatever is already on that monitor. The app is a tray app, so
    /// while the user works elsewhere it is not the foreground process and Show() alone can leave an existing
    /// topmost window (the translation popup, the subtitle plate) above the fresh selection windows.
    /// </summary>
    private static void Raise(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        Native.SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x53); // HWND_TOPMOST | NOSIZE | NOMOVE | NOACTIVATE | SHOWWINDOW
        window.Activate();
        window.Focus();
    }

    public static async Task<Rectangle?> SelectAsync(CancellationToken token)
    {
        var (rect, _) = await SelectDetailedAsync(token);
        return rect;
    }
}
