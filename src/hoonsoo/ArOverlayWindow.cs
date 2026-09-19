using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace Hoonsoo;

public sealed class ArOverlayWindow : Window
{
    // Subtitle sizing. The floor keeps Korean legible; the ceiling stays below the region overlay's
    // floor (RegionOverlayFontMin = 18) so a dragged area always reads larger than the AR HUD.
    private const double ArFontMin = 13;
    private const double ArFontMax = 17;

    // A dragged-area result is the text the user explicitly asked about, so its plate starts above the AR
    // ceiling: the two ranges must never overlap, or "bigger than the AR subtitle" stops being true.
    internal const double RegionOverlayFontMin = 18;
    private const double RegionOverlayFontMax = 34;

    /// <summary>The monitor this overlay covers, in physical pixels.</summary>
    internal Rectangle ScreenBounds => screenBounds;

    /// <summary>
    /// Subtitle font for one OCR line. Public because the size contract is a user-visible promise:
    /// a dragged-area plate must always read larger than an AR subtitle (see RegionFontFor).
    /// </summary>
    public static double ArFontFor(double lineHeightDip, double scale = 1.0)
    {
        if (scale <= 0) scale = 1.0;
        return Math.Clamp(lineHeightDip * 0.88 * scale, ArFontMin * scale, ArFontMax * scale);
    }

    /// <summary>Plate font for a dragged region. Always above <see cref="ArFontFor"/>.</summary>
    public static double RegionFontFor(double regionHeightDip, double scale = 1.0)
    {
        if (scale <= 0) scale = 1.0;
        return Math.Clamp(regionHeightDip * 0.9 * scale, RegionOverlayFontMin * scale, RegionOverlayFontMax * scale);
    }

    private readonly Rectangle screenBounds;
    private readonly Canvas canvas;
    private readonly DispatcherTimer pollTimer;
    private Native.EscapeInterceptor? escapeInterceptor;
    private readonly Action? onDismiss;
    // Layout size in DIPs. Kept apart from Window.Width/Height because those are NaN until the window is
    // shown, and every chip position is computed against the surface size.
    private double layoutWidth;
    private double layoutHeight;
    private bool isClosing;

    public ArOverlayWindow(Rectangle screenBounds, Action? onDismiss = null)
    {
        this.screenBounds = screenBounds;
        layoutWidth = screenBounds.Width;
        layoutHeight = screenBounds.Height;
        this.onDismiss = onDismiss;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;

        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        canvas = new Canvas { Background = Brushes.Transparent };
        // A layered window loses ClearType by default. The chips paint an opaque plate, which is exactly
        // the case ClearTypeHint exists to re-enable, so the glyphs get subpixel rendering back.
        RenderOptions.SetClearTypeHint(canvas, ClearTypeHint.Enabled);
        Content = canvas;

        pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        pollTimer.Tick += OnPollTick;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        var exStyle = Native.GetWindowLongPtr(hwnd, -20);
        Native.SetWindowLongPtr(hwnd, -20, (IntPtr)(exStyle.ToInt64() | 0x00000020 | 0x00000080 | 0x08000000));

        ApplyPhysicalBounds(GetScale());
        escapeInterceptor = new Native.EscapeInterceptor(() => Dispatcher.Invoke(Dismiss));
        pollTimer.Start();
    }

    /// <summary>Monitor scale for the window's own handle, falling back to the composition target.</summary>
    private double GetScale()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            uint dpi = Native.GetDpiForWindow(hwnd);
            if (dpi > 0) return dpi / 96.0;
        }
        var source = PresentationSource.FromVisual(this);
        double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        return scale > 0 ? scale : 1.0;
    }

    /// <summary>
    /// WPF lays out in DIPs while SetWindowPos speaks physical pixels, and the two must agree exactly:
    /// the previous code divided bounds that WPF had already treated as DIPs, so at 125%/150% the overlay
    /// sat on a fractional origin and the compositor resampled every glyph. Size from the monitor scale,
    /// then pin the physical rect.
    /// </summary>
    private void ApplyPhysicalBounds(double scale)
    {
        Width = Math.Round(screenBounds.Width / scale);
        Height = Math.Round(screenBounds.Height / scale);
        layoutWidth = Width;
        layoutHeight = Height;
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), screenBounds.Left, screenBounds.Top, screenBounds.Width, screenBounds.Height, 0x10);
    }

    /// <summary>Whole device pixels only: a chip on a half-pixel DIP origin renders soft.</summary>
    private static double Snap(double dip, double scale) => scale > 0 ? Math.Round(dip * scale) / scale : dip;

    /// <summary>An opaque plate plus the ClearType hint is what makes a subtitle read sharp over live content.</summary>
    private static void MakeCrisp(Border plate, FrameworkElement content)
    {
        plate.SnapsToDevicePixels = true;
        plate.UseLayoutRounding = true;
        content.SnapsToDevicePixels = true;
        content.UseLayoutRounding = true;
        RenderOptions.SetClearTypeHint(plate, ClearTypeHint.Enabled);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyPhysicalBounds(newDpi.DpiScaleX > 0 ? newDpi.DpiScaleX : 1.0);
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (isClosing) return;

        short escState = Native.GetAsyncKeyState(0x1B);
        if ((escState & 0x8000) != 0)
        {
            Dismiss();
            return;
        }

        short lbtnState = Native.GetAsyncKeyState(0x01);
        if ((lbtnState & 0x8000) != 0)
        {
            Dismiss();
            return;
        }
    }

    public void ShowLoading(string message)
    {
        canvas.Children.Clear();
        var pill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 17, 24, 39)),
            BorderBrush = new SolidColorBrush(Theme.Accent),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18, 8, 18, 8)
        };

        var text = new TextBlock
        {
            Text = message,
            Foreground = Brushes.White,
            FontFamily = Ui.AppFont,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold
        };
        pill.Child = text;
        MakeCrisp(pill, text);

        // No DropShadowEffect anywhere on the overlay: an Effect renders the plate into an intermediate
        // surface, which costs the text its ClearType and softens every glyph edge.
        double scale = GetScale();
        Canvas.SetLeft(pill, Snap(Math.Max(20, (layoutWidth - 260) / 2), scale));
        Canvas.SetTop(pill, Snap(40, scale));
        canvas.Children.Add(pill);
        Motion.Enter(pill, fromY: -7, fromScale: 0.96, origin: new System.Windows.Point(0.5, 0), duration: Motion.Fast);
    }

    public void DisplaySubtitles(IReadOnlyList<ArBlock> blocks, int screenLeft, int screenTop, double scale = 1.0)
    {
        canvas.Children.Clear();

        // One scale for both layout and snapping: the overlay spans a single monitor, so the window's own
        // DPI is the authority rather than a composition target that can still describe the old monitor.
        double dpiX = GetScale();
        double dpiY = dpiX;

        int validCount = blocks.Count(b => !string.IsNullOrWhiteSpace(b.TranslatedText));
        if (validCount == 0) return;

        var hintPill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 15, 23, 42)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(190, 56, 189, 248)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13, 6, 13, 6)
        };
        var hintText = new TextBlock
        {
            Text = $"✨ 활성 창 훈수 자막 ({validCount}개 문맥) · 아무 곳이나 클릭하거나 ESC로 닫기",
            Foreground = new SolidColorBrush(Color.FromRgb(240, 246, 252)),
            FontFamily = Ui.AppFont,
            FontSize = 12.5,
            FontWeight = FontWeights.Medium
        };
        hintPill.Child = hintText;
        MakeCrisp(hintPill, hintText);
        Canvas.SetLeft(hintPill, Snap(Math.Max(16, (layoutWidth - 360) / 2), dpiX));
        Canvas.SetTop(hintPill, Snap(16, dpiY));
        canvas.Children.Add(hintPill);
        Motion.Enter(hintPill, fromY: -7, fromScale: 0.97, origin: new System.Windows.Point(0.5, 0), duration: Motion.Fast);

        var allocatedRegions = new List<System.Windows.Rect>();
        int chipIndex = 0;

        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.TranslatedText)) continue;

            double dipX = (block.Bounds.Left - screenLeft) / dpiX;
            double dipY = (block.Bounds.Top - screenTop) / dpiY;
            double dipH = block.Bounds.Height / dpiY;

            double fontSize = ArFontFor(dipH, scale);
            var chip = CreateSubtitleChip(block.TranslatedText, fontSize, Math.Max(180, layoutWidth - dipX - 16));
            double targetTop = dipY + dipH + 2;
            double estChipH = fontSize * 1.35 + 6;
            double estChipW = Math.Min(block.TranslatedText.Length * fontSize * 0.8 + 14, chip.MaxWidth);

            if (targetTop + estChipH > layoutHeight)
            {
                targetTop = Math.Max(0, dipY - estChipH - 1.5);
            }

            var chipRect = new System.Windows.Rect(dipX, targetTop, estChipW, estChipH);
            for (int r = 0; r < allocatedRegions.Count; r++)
            {
                if (allocatedRegions[r].IntersectsWith(chipRect))
                {
                    targetTop = allocatedRegions[r].Bottom + 2;
                    chipRect = new System.Windows.Rect(dipX, targetTop, estChipW, estChipH);
                }
            }
            allocatedRegions.Add(chipRect);

            Canvas.SetLeft(chip, Snap(Math.Max(0, dipX), dpiX));
            Canvas.SetTop(chip, Snap(targetTop, dpiY));
            canvas.Children.Add(chip);
            // Subtitles land one after another instead of all popping at once.
            Motion.FadeSlideIn(chip, 3, Motion.Fast, Math.Min(chipIndex, 8) * 30);
            chipIndex++;
        }
    }

    /// <summary>
    /// Immediate on-screen answer for a dragged area: one large plate anchored to the region the user
    /// picked, with the same click-through/ESC dismissal as the AR HUD. The plate is deliberately bigger
    /// than any AR subtitle (RegionOverlayFontMin > ArFontMax) because a hand-picked region is the text the
    /// user actually asked about, and it must read at a glance.
    /// </summary>
    public Rectangle? DisplayRegionResult(Rectangle region, string text, bool pending, double scale = 1.0)
    {
        canvas.Children.Clear();
        double monitorScale = GetScale();
        double regionX = (region.Left - screenBounds.Left) / monitorScale;
        double regionY = (region.Top - screenBounds.Top) / monitorScale;
        double regionW = region.Width / monitorScale;
        double regionH = region.Height / monitorScale;

        double fontSize = RegionFontFor(regionH, scale);
        var plate = CreateRegionPlate(text, fontSize, pending, Math.Max(220, Math.Min(layoutWidth - 32, Math.Max(regionW, 320))));
        plate.Measure(new System.Windows.Size(plate.MaxWidth, double.PositiveInfinity));
        double w = Math.Min(plate.DesiredSize.Width, plate.MaxWidth);
        double h = plate.DesiredSize.Height;

        // Beside the dragged area, never over it: right, left, below, then above, in physical pixels.
        int wPx = (int)Math.Ceiling(w * monitorScale), hPx = (int)Math.Ceiling(h * monitorScale);
        var (xPx, yPx) = Placement.BesideSource(wPx, hPx, region.Left, region.Bottom + 6, screenBounds, [region]);
        double x = (xPx - screenBounds.Left) / monitorScale;
        double y = (yPx - screenBounds.Top) / monitorScale;

        Canvas.SetLeft(plate, Snap(x, monitorScale));
        Canvas.SetTop(plate, Snap(y, monitorScale));
        canvas.Children.Add(plate);
        Motion.Enter(plate, fromY: 4, fromScale: 0.98, origin: new System.Windows.Point(0, 0.5), duration: Motion.Fast);
        return new Rectangle(xPx, yPx, wPx, hPx);
    }

    public static Border CreateSubtitleChip(string text, double fontSize, double maxWidth)
    {
        var chip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 15, 23, 42)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 56, 189, 248)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3.5),
            Padding = new Thickness(6, 2, 6, 2),
            MaxWidth = maxWidth
        };

        var tb = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontFamily = Ui.AppFont,
            FontSize = fontSize,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap
        };
        chip.Child = tb;
        MakeCrisp(chip, tb);
        return chip;
    }

    public static Border CreateRegionPlate(string text, double fontSize, bool pending, double maxWidth)
    {
        var plate = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 12, 18, 32)),
            BorderBrush = new SolidColorBrush(pending ? Theme.Accent : Color.FromRgb(56, 189, 248)),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 7, 12, 8),
            MaxWidth = maxWidth
        };
        var tb = new TextBlock
        {
            Text = text,
            // Pending text is dimmed with a colour, never with element opacity: an opacity value below 1
            // puts the plate back on an alpha layer, which is what costs it ClearType.
            Foreground = pending ? new SolidColorBrush(Color.FromRgb(203, 213, 225)) : Brushes.White,
            FontFamily = Ui.AppFont,
            FontSize = fontSize,
            FontWeight = pending ? FontWeights.Medium : FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = fontSize * 1.35
        };
        plate.Child = tb;
        MakeCrisp(plate, tb);
        return plate;
    }

    public void Dismiss()
    {
        if (isClosing) return;
        isClosing = true;
        pollTimer.Stop();
        escapeInterceptor?.Dispose();
        escapeInterceptor = null;
        try { onDismiss?.Invoke(); }
        catch { }

        // Quick fade so the overlay does not blink out of existence.
        var fade = new DoubleAnimation(1, 0, Motion.Fast) { EasingFunction = Motion.EaseOut() };
        fade.Completed += (_, _) => { try { Close(); } catch { } };
        canvas.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
