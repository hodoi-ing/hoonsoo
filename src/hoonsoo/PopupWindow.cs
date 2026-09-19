using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Hoonsoo;

public sealed class PopupWindow : Window
{
    // Room for the soft shadow so elevation is not clipped by the window edge.
    private const double ShadowGutter = 16;

    private readonly StackPanel body = new();
    private readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420 };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer copyFeedbackTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly Border card;
    // Shadow and content are siblings: an Effect on the element that holds text renders it through an
    // intermediate surface, which is what costs the popup its ClearType.
    private readonly Grid shell = new();
    private Button? copyFeedbackButton;
    private DateTime deadline;
    private bool autoClose;
    private int seconds;
    private bool escapeWasDown;
    private Native.EscapeInterceptor? escapeInterceptor;
    private bool closing;
    private bool closed;
    private Native.Point anchor;
    private bool dragging;
    private bool movedByUser;
    private Native.Point dragCursor;
    // Physical pixels, read from the HWND: this window is placed with SetWindowPos, so WPF's Left/Top stay
    // NaN and using them here would make every drag a no-op.
    private int dragLeft, dragTop;
    private IReadOnlyList<System.Drawing.Rectangle> keepClear = [];

    public event Action? Reread;
    public event Action? SelectRegion;
    public event Action? SettingsRequested;
    public event Action? Dismissed;

    public PopupWindow()
    {
        Title = "훈수";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 600;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        FontFamily = Ui.AppFont;
        FontSize = 13.5;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var root = new StackPanel { Margin = new Thickness(16) };

        // Header: Brand logo dot + Title + Close Button
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var close = Ui.CloseButton((_, _) => RequestClose());
        DockPanel.SetDock(close, Dock.Right);
        top.Children.Add(close);

        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titlePanel.Children.Add(Ui.LogoImage(24));
        titlePanel.Children.Add(new TextBlock
        {
            Text = "훈수",
            FontWeight = FontWeights.SemiBold,
            FontSize = 15.5,
            Foreground = Theme.BrushTextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        });
        top.Children.Add(titlePanel);
        root.Children.Add(top);

        root.Children.Add(new Border { Height = 1, Background = Theme.BrushHairline, Margin = new Thickness(0, 0, 0, 12) });

        // Actions: one primary action, the rest stay quiet so the hierarchy reads at a glance.
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        actions.Children.Add(Ui.Button("↻ 마지막 드래그", (_, _) => Reread?.Invoke(), true));
        actions.Children.Add(Ui.Button("영역 OCR (새로 드래그)", (_, _) => SelectRegion?.Invoke()));
        actions.Children.Add(Ui.Button("설정", (_, _) => SettingsRequested?.Invoke()));
        root.Children.Add(actions);

        scroll.Content = body;
        root.Children.Add(scroll);

        // Elevation comes from the shadow; the hairline just keeps the edge crisp.
        card = new Border
        {
            Background = Theme.BrushBgCard,
            BorderBrush = Theme.BrushHairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radii.Popup),
            Child = root,
            Margin = new Thickness(ShadowGutter)
        };
        shell.Children.Add(new Border
        {
            Background = Theme.BrushBgCard,
            CornerRadius = new CornerRadius(Radii.Popup),
            Effect = Ui.PopupShadow(),
            Margin = new Thickness(ShadowGutter)
        });
        shell.Children.Add(card);
        Content = shell;

        // The whole card drags the window. The old handle lived on the title strip only and read WPF
        // Left/Top, which are NaN for a SetWindowPos-placed window, so dragging did nothing at all.
        card.Cursor = Cursors.SizeAll;
        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsInteractive(e.OriginalSource)) return;
            if (!BeginDrag()) return;
            card.CaptureMouse();
            e.Handled = true;
        };
        card.PreviewMouseMove += (_, e) =>
        {
            if (!dragging) return;
            Native.GetCursorPos(out var cursor);
            DragTo(cursor.X, cursor.Y);
            e.Handled = true;
        };
        card.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            card.ReleaseMouseCapture();
            e.Handled = true;
        };

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowLongPtr(handle, -20, new IntPtr(Native.GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000L | 0x80L));
            HwndSource.FromHwnd(handle)?.AddHook(NoActivate);
            escapeInterceptor = new Native.EscapeInterceptor(() => Dispatcher.Invoke(() => RequestClose(animate: false)));
        };

        Loaded += (_, _) => Position();
        SizeChanged += (_, _) => Position();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) RequestClose(animate: false); };

        timer.Tick += (_, _) =>
        {
            bool down = (Native.GetAsyncKeyState(0x1B) & 0x8000) != 0;
            if (down && !escapeWasDown) { RequestClose(animate: false); return; }
            escapeWasDown = down;
            if (IsMouseOver) deadline = DateTime.UtcNow.AddSeconds(seconds);
            if (autoClose && DateTime.UtcNow >= deadline) RequestClose();
        };

        copyFeedbackTimer.Tick += (_, _) => ResetCopyFeedback();

        Closed += (_, _) =>
        {
            closed = true;
            escapeInterceptor?.Dispose();
            escapeInterceptor = null;
            timer.Stop();
            ResetCopyFeedback();
            Dismissed?.Invoke();
        };
    }

    private static IntPtr NoActivate(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x21) { handled = true; return new IntPtr(3); }
        return IntPtr.Zero;
    }

    public void Present(Native.Point point, Settings settings, IReadOnlyList<System.Drawing.Rectangle>? keepClear = null)
    {
        anchor = point;
        movedByUser = false;
        dragging = false;
        this.keepClear = keepClear ?? [];
        seconds = settings.CloseSeconds;
        autoClose = false;
        deadline = DateTime.UtcNow.AddSeconds(seconds);
        escapeWasDown = (Native.GetAsyncKeyState(0x1B) & 0x8000) != 0;
        closing = false;
        Show();
        Position();

        // Enter motion: origin-aware (grows out of the cursor corner), short and eased so it
        // reads as instant instead of as a delay.
        Motion.EnterWindow(this);
        Motion.Enter(shell, fromY: 7, fromScale: 0.97, origin: new Point(0, 0), duration: Motion.Base);
        timer.Start();
    }

    /// <summary>
    /// Fade/scale out first, then close, so the popup never just vanishes.
    /// ESC dismisses instantly: keyboard-initiated actions must not wait on motion.
    /// </summary>
    public void RequestClose(bool animate = true)
    {
        if (closing || closed) return;
        closing = true;
        timer.Stop();
        copyFeedbackTimer.Stop();
        if (!animate) { Close(); return; }
        Motion.ExitWindow(this, shell, () => { if (!closed) Close(); });
    }

    public void Status(string text)
    {
        body.Children.Clear();
        var cardElement = new Border
        {
            Background = Theme.BrushBgCard,
            BorderBrush = Theme.BrushHairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radii.Card),
            Padding = new Thickness(13, 11, 13, 11),
            Margin = new Thickness(0, 0, 0, 6)
        };
        cardElement.Child = Ui.Text(text, 13.5, false);
        body.Children.Add(cardElement);
        Motion.FadeSlideIn(cardElement, 4);
        autoClose = false;
    }

    public void Error(string message, Settings settings)
    {
        body.Children.Clear();
        var cardElement = new Border
        {
            Background = Theme.BrushBgCard,
            BorderBrush = Theme.BrushDangerBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radii.Card),
            Padding = new Thickness(13, 11, 13, 11),
            Margin = new Thickness(0, 0, 0, 6)
        };
        var tb = Ui.Text(message, 13.5, false);
        tb.Foreground = Theme.BrushDanger;
        cardElement.Child = tb;
        body.Children.Add(cardElement);
        Motion.FadeSlideIn(cardElement, 4);
        StartClose(settings);
    }

    private void StartClose(Settings settings)
    {
        autoClose = settings.AutoClose;
        deadline = DateTime.UtcNow.AddSeconds(settings.CloseSeconds);
    }

    public void Result(TranslationResult result, Settings settings)
    {
        ResetCopyFeedback();
        body.Children.Clear();

        // Main translated text card
        var resultCard = new Border
        {
            Background = Theme.BrushBgCard,
            BorderBrush = Theme.BrushHairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radii.Card),
            Padding = new Thickness(16, 14, 16, 15),
            Margin = new Thickness(0, 0, 0, 10),
            Effect = Ui.CardShadow()
        };
        var resultText = Ui.Text(result.Error ?? result.TranslatedText, 15, false);
        if (result.Error != null)
        {
            resultText.Foreground = Theme.BrushDanger;
            resultCard.Child = resultText;
        }
        else
        {
            resultText.FontWeight = FontWeights.Medium;
            var resultStack = new StackPanel();
            resultStack.Children.Add(Ui.Caption("번역"));
            resultStack.Children.Add(resultText);
            resultCard.Child = resultStack;
        }
        body.Children.Add(resultCard);

        // Footer info bar: Capture Method + Copy Button
        var infoBar = new DockPanel { Margin = new Thickness(0, 2, 0, 8) };
        var copyBtn = Ui.Button("복사", (sender, _) =>
        {
            try
            {
                Clipboard.SetText(result.Error is null ? result.TranslatedText : result.OriginalText);
                ShowCopyFeedback((Button)sender);
            }
            catch { }
        }, false, false, true);
        DockPanel.SetDock(copyBtn, Dock.Right);
        infoBar.Children.Add(copyBtn);

        var methodChip = Ui.Chip(result.CaptureMethod);
        methodChip.HorizontalAlignment = HorizontalAlignment.Left;
        infoBar.Children.Add(methodChip);
        body.Children.Add(infoBar);

        // Original Text Expander
        var originalExpander = new Expander
        {
            Header = "원문 보기",
            Margin = new Thickness(0, 4, 0, 6)
        };
        Ui.StyleExpander(originalExpander);
        AnimateExpander(originalExpander);
        var originalCard = new Border
        {
            Background = Theme.BrushBgInset,
            BorderBrush = Theme.BrushHairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radii.Chip),
            Padding = new Thickness(11),
            Margin = new Thickness(0, 4, 0, 4)
        };
        originalCard.Child = Ui.Text(result.OriginalText, 13, true);
        originalExpander.Content = originalCard;
        body.Children.Add(originalExpander);

        // Developer terms are a glossary from the local dictionary, so they are plain labels: nothing
        // here needs a network call, and the chips stay readable at the popup's own font size.
        if (settings.Terms && result.DetectedTerms.Count > 0)
        {
            var terms = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            foreach (var term in result.DetectedTerms)
            {
                var chip = Ui.Chip(term.Term + " · " + term.KoreanName);
                chip.Margin = new Thickness(0, 2, 6, 2);
                terms.Children.Add(chip);
            }

            var termsExpander = new Expander
            {
                Header = "개발용어",
                Margin = new Thickness(0, 4, 0, 6)
            };
            Ui.StyleExpander(termsExpander);
            AnimateExpander(termsExpander);
            termsExpander.Content = terms;
            body.Children.Add(termsExpander);
        }

        // Stagger so the result settles in instead of snapping in all at once.
        Motion.StaggerIn(body, 45, 6);
        StartClose(settings);
    }

    /// <summary>Expander content eases in instead of popping, which also masks the instant height change.</summary>
    private static void AnimateExpander(Expander expander)
    {
        expander.Expanded += (_, _) =>
        {
            if (expander.Content is FrameworkElement content) Motion.FadeSlideIn(content, -5, Motion.Fast);
        };
    }

    private void ShowCopyFeedback(Button button)
    {
        if (copyFeedbackButton is not null) copyFeedbackButton.Content = "복사";
        copyFeedbackButton = button;
        button.Content = "✓ 복사됨";
        copyFeedbackTimer.Stop();
        copyFeedbackTimer.Start();
    }

    private void ResetCopyFeedback()
    {
        copyFeedbackTimer.Stop();
        if (copyFeedbackButton is not null) copyFeedbackButton.Content = "복사";
        copyFeedbackButton = null;
    }

    private void Position()
    {
        if (!IsLoaded) return;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(anchor.X, anchor.Y)).WorkingArea;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!movedByUser) Native.SetWindowPos(hwnd, new IntPtr(-1), anchor.X, anchor.Y, 0, 0, 0x11);
        double scale = Math.Max(96, Native.GetDpiForWindow(hwnd)) / 96.0;
        MaxWidth = Math.Max(180, screen.Width / scale);
        MaxHeight = Math.Max(120, screen.Height / scale);
        scroll.MaxHeight = Math.Max(70, Math.Min(420, screen.Height / scale - 160));
        if (movedByUser) return;
        int width = (int)Math.Ceiling(ActualWidth * scale), height = (int)Math.Ceiling(ActualHeight * scale);
        // Beside the captured text, never on top of it.
        var pos = Placement.BesideSource(width, height, anchor.X + 18, anchor.Y + 22, screen, keepClear);
        Native.SetWindowPos(hwnd, new IntPtr(-1), pos.X, pos.Y, 0, 0, 0x11);
    }

    /// <summary>Buttons, expanders and scrollbars keep their own input; everything else drags the window.</summary>
    /// <summary>
    /// Captures the window's current physical rect and the pointer origin for a drag. Internal so the
    /// release test runner can drive the very same path without synthesising OS input.
    /// </summary>
    internal bool BeginDrag()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var rect)) return false;
        dragLeft = rect.Left;
        dragTop = rect.Top;
        Native.GetCursorPos(out dragCursor);
        // Before the first move, not after: SetWindowPos can re-enter Position() through a layout pass, and
        // while movedByUser was still false that pass snapped the window straight back to the anchor —
        // which is why a drag looked like it did nothing.
        movedByUser = true;
        dragging = true;
        return true;
    }

    /// <summary>
    /// Moves the window so it follows the pointer, in physical pixels. Takes the pointer position rather
    /// than reading it, so the release tests can drive the same arithmetic without synthesising input.
    /// </summary>
    internal void DragTo(int cursorX, int cursorY)
    {
        movedByUser = true;
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), dragLeft + (cursorX - dragCursor.X), dragTop + (cursorY - dragCursor.Y), 0, 0, 0x11);
    }

    private static bool IsInteractive(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or TextBoxBase or ScrollBar or Thumb) return true;
        }
        return false;
    }
}
