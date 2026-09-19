using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WpfButton = System.Windows.Controls.Button;

namespace Hoonsoo;

/// <summary>
/// Palette, typography and control templates.
///
/// Runtime theming contract: colours are mutable properties and the shared brushes below are
/// deliberately NOT frozen. A frozen <c>Freezable</c> throws <see cref="InvalidOperationException"/>
/// on any write, so <see cref="Apply"/> could never repaint anything. Every control in the app holds a
/// reference to one of these brush instances, so assigning <c>Color</c> repaints the whole UI without
/// rebuilding a single element. Verified by execution in <c>work/probe-theming</c>.
///
/// Invariant: never hand a theme brush to an animation or a hover helper — animations own the property
/// and would silently freeze the shared brush at an animated colour. Animate a local brush instead.
/// </summary>
public static class Theme
{
    public static Color BgApp { get; private set; }
    public static Color BgCard { get; private set; }
    public static Color BgCardHover { get; private set; }
    public static Color BgCardActive { get; private set; }
    public static Color BgInput { get; private set; }
    public static Color BgInset { get; private set; }
    public static Color Border { get; private set; }
    public static Color BorderSubtle { get; private set; }
    public static Color TextPrimary { get; private set; }
    public static Color TextSecondary { get; private set; }
    public static Color TextMuted { get; private set; }
    public static Color Accent { get; private set; }
    public static Color AccentHover { get; private set; }
    public static Color AccentPressed { get; private set; }
    public static Color Danger { get; private set; }
    public static Color DangerBorder { get; private set; }
    public static Color BgCardPressed { get; private set; }
    public static Color Hairline { get; private set; }
    public static Color HairlineStrong { get; private set; }
    public static Color AccentSoft { get; private set; }
    public static Color AccentRing { get; private set; }

    public static readonly SolidColorBrush BrushBgApp = new();
    public static readonly SolidColorBrush BrushBgCard = new();
    public static readonly SolidColorBrush BrushBgCardHover = new();
    public static readonly SolidColorBrush BrushBgCardActive = new();
    public static readonly SolidColorBrush BrushBgInput = new();
    public static readonly SolidColorBrush BrushBgInset = new();
    public static readonly SolidColorBrush BrushBorder = new();
    public static readonly SolidColorBrush BrushBorderSubtle = new();
    public static readonly SolidColorBrush BrushTextPrimary = new();
    public static readonly SolidColorBrush BrushTextSecondary = new();
    public static readonly SolidColorBrush BrushTextMuted = new();
    public static readonly SolidColorBrush BrushAccent = new();
    public static readonly SolidColorBrush BrushAccentHover = new();
    public static readonly SolidColorBrush BrushDanger = new();
    public static readonly SolidColorBrush BrushDangerBorder = new();
    public static readonly SolidColorBrush BrushHairline = new();
    public static readonly SolidColorBrush BrushHairlineStrong = new();
    public static readonly SolidColorBrush BrushAccentSoft = new();
    public static readonly SolidColorBrush BrushAccentRing = new();

    /// <summary>
    /// One-way binding to a palette brush, for use inside ControlTemplates and Styles.
    ///
    /// Templates MUST reach the palette through this instead of assigning the brush itself: sealing a
    /// template freezes every Freezable it captured, which pins that instance and makes every later
    /// <see cref="Apply(string?)"/> throw. A brush that arrives via a binding is not part of the sealed
    /// tree, and it needs no change notification from the binding anyway — recolouring the brush repaints
    /// every holder on its own.
    /// </summary>
    public static Binding Brush(string name) => new(name) { Source = PaletteBrushes.Instance, Mode = BindingMode.OneWay };

    /// <summary>True when the active palette is the dark one.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Normalised request: "system" | "light" | "dark".</summary>
    public static string Mode { get; private set; } = "system";

        private sealed record Palette(
        Color BgApp, Color BgCard, Color BgCardHover, Color BgCardActive, Color BgInput, Color BgInset,
        Color Border, Color BorderSubtle, Color TextPrimary, Color TextSecondary, Color TextMuted,
        Color Accent, Color AccentHover, Color AccentPressed, Color Danger, Color DangerBorder,
        Color BgCardPressed, Color Hairline, Color HairlineStrong, Color AccentSoft, Color AccentRing);

    // Soft Cloud light palette — the app's original look, unchanged.
    private static readonly Palette LightPalette = new(
        BgApp: Color.FromRgb(243, 244, 247),
        BgCard: Color.FromRgb(255, 255, 255),
        BgCardHover: Color.FromRgb(248, 249, 251),
        BgCardActive: Color.FromRgb(238, 240, 245),
        BgInput: Color.FromRgb(255, 255, 255),
        BgInset: Color.FromRgb(244, 245, 248),
        Border: Color.FromRgb(225, 228, 234),
        BorderSubtle: Color.FromRgb(236, 238, 242),
        TextPrimary: Color.FromRgb(17, 24, 39),
        TextSecondary: Color.FromRgb(75, 85, 99),
        TextMuted: Color.FromRgb(156, 163, 175),
        Accent: Color.FromRgb(1, 152, 254),
        AccentHover: Color.FromRgb(0, 132, 232),
        AccentPressed: Color.FromRgb(0, 112, 200),
        Danger: Color.FromRgb(239, 68, 68),
        DangerBorder: Color.FromArgb(0x3D, 0xEF, 0x44, 0x44),
        BgCardPressed: Color.FromRgb(231, 234, 240),
        Hairline: Color.FromArgb(0x14, 0x0F, 0x17, 0x2A),
        HairlineStrong: Color.FromArgb(0x26, 0x0F, 0x17, 0x2A),
        AccentSoft: Color.FromArgb(0x1A, 0x01, 0x98, 0xFE),
        AccentRing: Color.FromArgb(0x59, 0x01, 0x98, 0xFE));

    // Dark palette. Surfaces step up in lightness (BgApp -> BgInset -> BgCard) so elevation reads
    // without shadows, and hairlines flip to white so edges stay visible on dark.
    private static readonly Palette DarkPalette = new(
        BgApp: Color.FromRgb(0x0E, 0x10, 0x15),
        BgCard: Color.FromRgb(0x16, 0x19, 0x20),
        BgCardHover: Color.FromRgb(0x1E, 0x22, 0x2B),
        BgCardActive: Color.FromRgb(0x25, 0x2A, 0x36),
        BgInput: Color.FromRgb(0x12, 0x14, 0x1A),
        BgInset: Color.FromRgb(0x14, 0x17, 0x1E),
        Border: Color.FromRgb(0x26, 0x2B, 0x36),
        BorderSubtle: Color.FromRgb(0x1D, 0x21, 0x2A),
        TextPrimary: Color.FromRgb(0xF4, 0xF6, 0xF9),
        TextSecondary: Color.FromRgb(0x96, 0xA0, 0xB2),
        TextMuted: Color.FromRgb(0x67, 0x71, 0x82),
        Accent: Color.FromRgb(0x38, 0xA8, 0xFF),
        AccentHover: Color.FromRgb(0x58, 0xB6, 0xFF),
        AccentPressed: Color.FromRgb(0x1E, 0x8A, 0xE8),
        Danger: Color.FromRgb(0xF8, 0x71, 0x71),
        DangerBorder: Color.FromArgb(0x59, 0xF8, 0x71, 0x71),
        BgCardPressed: Color.FromRgb(0x2D, 0x33, 0x40),
        Hairline: Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
        HairlineStrong: Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
        AccentSoft: Color.FromArgb(0x24, 0x38, 0xA8, 0xFF),
        AccentRing: Color.FromArgb(0x66, 0x38, 0xA8, 0xFF));

    static Theme()
    {
        Mode = "light";
        Apply(LightPalette, false);
    }

    public static string Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "light" => "light",
        "dark" => "dark",
        _ => "system",
    };

    /// <summary>
    /// Swap the palette. Safe to call repeatedly: assigning an identical colour is a no-op for WPF,
    /// so the visible cost is limited to the repaint hooks.
    /// </summary>
    public static void Apply(string? mode)
    {
        Mode = Normalize(mode);
        var dark = Mode == "dark" || (Mode == "system" && SystemPrefersDark());
        Apply(dark ? DarkPalette : LightPalette, dark);
    }

    private static void Apply(Palette palette, bool dark)
    {
        IsDark = dark;
        BgApp = palette.BgApp;
        BgCard = palette.BgCard;
        BgCardHover = palette.BgCardHover;
        BgCardActive = palette.BgCardActive;
        BgInput = palette.BgInput;
        BgInset = palette.BgInset;
        Border = palette.Border;
        BorderSubtle = palette.BorderSubtle;
        TextPrimary = palette.TextPrimary;
        TextSecondary = palette.TextSecondary;
        TextMuted = palette.TextMuted;
        Accent = palette.Accent;
        AccentHover = palette.AccentHover;
        AccentPressed = palette.AccentPressed;
        Danger = palette.Danger;
        DangerBorder = palette.DangerBorder;
        BgCardPressed = palette.BgCardPressed;
        Hairline = palette.Hairline;
        HairlineStrong = palette.HairlineStrong;
        AccentSoft = palette.AccentSoft;
        AccentRing = palette.AccentRing;

        BrushBgApp.Color = BgApp;
        BrushBgCard.Color = BgCard;
        BrushBgCardHover.Color = BgCardHover;
        BrushBgCardActive.Color = BgCardActive;
        BrushBgInput.Color = BgInput;
        BrushBgInset.Color = BgInset;
        BrushBorder.Color = Border;
        BrushBorderSubtle.Color = BorderSubtle;
        BrushTextPrimary.Color = TextPrimary;
        BrushTextSecondary.Color = TextSecondary;
        BrushTextMuted.Color = TextMuted;
        BrushAccent.Color = Accent;
        BrushAccentHover.Color = AccentHover;
        BrushDanger.Color = Danger;
        BrushDangerBorder.Color = DangerBorder;
        BrushHairline.Color = Hairline;
        BrushHairlineStrong.Color = HairlineStrong;
        BrushAccentSoft.Color = AccentSoft;
        BrushAccentRing.Color = AccentRing;

        ApplyTrayColorMode();
        RepaintRegistered();
    }

    /// <summary>Apps-vs-system split: the app theme is <c>AppsUseLightTheme</c> (0 = dark).</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The tray menu is a WinForms surface, so it can only follow the palette through the WinForms
    /// colour-mode API. That API is experimental (WFO5001) and Windows 11+ only, and it must be called
    /// before the first WinForms control exists — AppController applies the theme before creating the
    /// NotifyIcon for exactly that reason. Measured behaviour: on a light OS it does not repaint an
    /// already-built context menu, so the tray menu may stay light; nothing else in the app depends on it.
    /// </summary>
    private static void ApplyTrayColorMode()
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
#pragma warning disable WFO5001
            System.Windows.Forms.Application.SetColorMode(
                IsDark ? System.Windows.Forms.SystemColorMode.Dark : System.Windows.Forms.SystemColorMode.Classic);
#pragma warning restore WFO5001
        }
        catch
        {
        }
    }

    // Controls that cached a colour at construction time (animated hover fills, shadows) cannot follow a
    // shared brush, so they register a repaint hook. The table is keyed weakly: a hook lives exactly as
    // long as its element, so popups created per translation cannot accumulate.
    private static readonly System.Collections.Generic.List<WeakReference<FrameworkElement>> paintTargets = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, Action> painters = new();

    public static void RegisterPaint(FrameworkElement element, Action reapply)
    {
        painters.Remove(element);
        painters.Add(element, reapply);
        paintTargets.Add(new WeakReference<FrameworkElement>(element));
        if (paintTargets.Count > 96) paintTargets.RemoveAll(weak => !weak.TryGetTarget(out _));
    }

    private static void RepaintRegistered()
    {
        foreach (var weak in paintTargets)
        {
            if (!weak.TryGetTarget(out var element)) continue;
            if (painters.TryGetValue(element, out var reapply)) reapply();
        }
    }
}

/// <summary>
/// Binding source for colours that live inside ControlTemplates and Styles. Every property reads the live
/// shared brush, so a binding installed once keeps pointing at the palette forever.
/// </summary>
public sealed class PaletteBrushes
{
    public static readonly PaletteBrushes Instance = new();

    private PaletteBrushes()
    {
    }

    public SolidColorBrush BgApp => Theme.BrushBgApp;
    public SolidColorBrush BgCard => Theme.BrushBgCard;
    public SolidColorBrush BgCardHover => Theme.BrushBgCardHover;
    public SolidColorBrush BgCardActive => Theme.BrushBgCardActive;
    public SolidColorBrush BgInput => Theme.BrushBgInput;
    public SolidColorBrush BgInset => Theme.BrushBgInset;
    public SolidColorBrush Border => Theme.BrushBorder;
    public SolidColorBrush BorderSubtle => Theme.BrushBorderSubtle;
    public SolidColorBrush TextPrimary => Theme.BrushTextPrimary;
    public SolidColorBrush TextSecondary => Theme.BrushTextSecondary;
    public SolidColorBrush TextMuted => Theme.BrushTextMuted;
    public SolidColorBrush Accent => Theme.BrushAccent;
    public SolidColorBrush AccentSoft => Theme.BrushAccentSoft;
    public SolidColorBrush AccentRing => Theme.BrushAccentRing;
    public SolidColorBrush Hairline => Theme.BrushHairline;
    public SolidColorBrush HairlineStrong => Theme.BrushHairlineStrong;
    public SolidColorBrush Danger => Theme.BrushDanger;
    public SolidColorBrush DangerBorder => Theme.BrushDangerBorder;
}

/// <summary>One radius scale so every surface rounds consistently.</summary>
public static class Radii
{
    public const double Chip = 6;
    public const double Control = 8;
    public const double Card = 12;
    public const double Popup = 16;
}

public static class Ui
{
    public static readonly FontFamily AppFont = new("Segoe UI");

    public static Button Button(string text, RoutedEventHandler action, bool isPrimary = false, bool isDanger = false, bool isSmall = false)
    {
        var button = new Button
        {
            Content = text,
            Padding = isSmall ? new Thickness(8, 4, 8, 4) : new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 6, 6),
            MinHeight = isSmall ? 26 : 32,
            Cursor = Cursors.Hand,
            Foreground = isPrimary ? Brushes.White : isDanger ? Theme.BrushDanger : Theme.BrushTextPrimary,
            FontFamily = AppFont,
            FontSize = isSmall ? 12 : 13,
            FontWeight = isPrimary ? FontWeights.SemiBold : FontWeights.Normal
        };

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "border";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));

        border.SetBinding(Border.BackgroundProperty, Theme.Brush(isPrimary ? "Accent" : "BgCard"));
        border.SetBinding(Border.BorderBrushProperty, Theme.Brush(isPrimary ? "Accent" : "Border"));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(WpfButton.PaddingProperty));
        border.AppendChild(presenter);

        template.VisualTree = border;

        var disabledTrigger = new Trigger { Property = WpfButton.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(Border.OpacityProperty, 0.45, "border"));
        template.Triggers.Add(disabledTrigger);

        button.Template = template;
        button.Click += action;

        bool wired = false;
        button.Loaded += (_, _) =>
        {
            if (wired) return;
            wired = true;
            WireButtonStates(button, isPrimary);
        };
        return button;
    }

    /// <summary>Fill colours for a button variant. Resolved per call so a palette swap is picked up.</summary>
    private static (Color Normal, Color Hover, Color Pressed) ButtonRoles(bool isPrimary) => isPrimary
        ? (Theme.Accent, Theme.AccentHover, Theme.AccentPressed)
        : (Theme.BgCard, Theme.BgCardHover, Theme.BgCardPressed);

    /// <summary>
    /// Hover and press are animated instead of instant template triggers: the fill eases and
    /// the button scales to 0.97 on press so it feels like the UI heard the user.
    /// </summary>
    private static void WireButtonStates(Button button, bool isPrimary)
    {
        Motion.AttachPress(button);
        if (button.Template?.FindName("border", button) is not Border border) return;
        var (normal, hover, pressed) = ButtonRoles(isPrimary);
        var brush = new SolidColorBrush(normal);
        border.Background = brush;
        Motion.AttachHover(brush, button, normal, hover, pressed);
        // This fill is a brush the button owns rather than a shared theme brush, so a palette swap
        // has to re-seed it by hand.
        Theme.RegisterPaint(button, () =>
        {
            var (nextNormal, nextHover, nextPressed) = ButtonRoles(isPrimary);
            Motion.RetargetHover(brush, button, nextNormal, nextHover, nextPressed);
        });
    }

    public static Button CloseButton(RoutedEventHandler action)
    {
        var btn = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 28,
            Cursor = Cursors.Hand,
            Foreground = Theme.BrushTextSecondary,
            FontFamily = AppFont,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "border";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        template.VisualTree = border;

        btn.Template = template;
        btn.Click += action;
        bool wired = false;
        btn.Loaded += (_, _) =>
        {
            if (wired) return;
            wired = true;
            Motion.AttachPress(btn, 0.94);
            if (btn.Template?.FindName("border", btn) is not Border border) return;
            var brush = new SolidColorBrush(Colors.Transparent);
            border.Background = brush;
            btn.MouseEnter += (_, _) =>
            {
                Motion.AnimateColor(brush, Theme.BgCardHover, Motion.Fast);
                btn.Foreground = Theme.BrushTextPrimary;
            };
            btn.MouseLeave += (_, _) =>
            {
                Motion.AnimateColor(brush, Colors.Transparent, Motion.Fast);
                btn.Foreground = Theme.BrushTextSecondary;
            };
        };
        return btn;
    }

    public static TextBlock Text(string text, double size = 13.5, bool isSecondary = false)
        => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = isSecondary ? Theme.BrushTextSecondary : Theme.BrushTextPrimary,
            FontFamily = AppFont,
            FontSize = size,
            LineHeight = size * 1.45,
            Margin = new Thickness(0, 3, 0, 7)
        };

    public static FrameworkElement Keycap(string key, bool isSmall = false)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = isSmall ? new Thickness(6, 2.5, 6, 2.5) : new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 3,
                ShadowDepth = 1,
                Direction = 270,
                Color = Color.FromRgb(0, 0, 0),
                Opacity = 0.12
            }
        };
        border.SetBinding(Border.BackgroundProperty, Theme.Brush("BgCard"));
        border.SetBinding(Border.BorderBrushProperty, Theme.Brush("Border"));

        var text = new TextBlock
        {
            Text = key,
            FontFamily = new FontFamily("Consolas, Cascadia Code, Segoe UI"),
            FontSize = isSmall ? 11 : 12,
            FontWeight = FontWeights.SemiBold
        };
        text.SetBinding(TextBlock.ForegroundProperty, Theme.Brush("TextPrimary"));
        border.Child = text;
        return border;
    }

    public static System.Windows.Media.Effects.DropShadowEffect CardShadow() => new()
    {
        BlurRadius = 16,
        ShadowDepth = 2,
        Direction = 270,
        Color = Color.FromRgb(15, 23, 42),
        Opacity = 0.08
    };

    // Elevation comes from a soft semi-transparent shadow, not a hard border.
    public static System.Windows.Media.Effects.DropShadowEffect PopupShadow() => new()
    {
        BlurRadius = 26,
        ShadowDepth = 7,
        Direction = 270,
        Color = Color.FromRgb(15, 23, 42),
        Opacity = 0.16
    };

    public static StackPanel KeycapBadge(string combination)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var parts = combination.Split(new[] { " + ", "+" }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            panel.Children.Add(Keycap(parts[i].Trim()));
        }
        return panel;
    }

    public static FrameworkElement LogoImage(double size)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "app.png");
            if (System.IO.File.Exists(path))
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                return new Image
                {
                    Source = bitmap,
                    Width = size,
                    Height = size,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
        }
        catch { }
        return new Border
        {
            Width = size * 0.4,
            Height = size * 0.4,
            CornerRadius = new CornerRadius(size * 0.2),
            Background = Theme.BrushAccent,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    public static void StyleTextBox(TextBox box, bool isReadOnly = false)
    {
        box.Background = isReadOnly ? Theme.BrushBgInset : Theme.BrushBgInput;
        box.Foreground = Theme.BrushTextPrimary;
        box.BorderBrush = Theme.BrushHairlineStrong;
        box.BorderThickness = new Thickness(1);
        box.Padding = new Thickness(10, 7, 10, 7);
        box.FontFamily = AppFont;
        box.FontSize = 13;
        box.CaretBrush = Theme.BrushAccent;
        box.Margin = new Thickness(0, 4, 0, 10);
        box.Template = FieldTemplate(typeof(TextBox));
    }

    /// <summary>Rounded, hairline-bordered field that lights up with the accent on focus.</summary>
    private static ControlTemplate FieldTemplate(Type target)
    {
        var template = new ControlTemplate(target);
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "fieldBorder";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(Radii.Control));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

        var host = new FrameworkElementFactory(typeof(ScrollViewer));
        host.Name = "PART_ContentHost";
        host.SetValue(ScrollViewer.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        host.SetValue(UIElement.FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        border.AppendChild(host);
        template.VisualTree = border;

        var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Brush("Accent"), "fieldBorder"));
        focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1.5), "fieldBorder"));
        template.Triggers.Add(focus);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55, "fieldBorder"));
        template.Triggers.Add(disabled);
        return template;
    }
    /// <summary>Minimal modern scrollbar matching the theme: 7px transparent track with rounded thumb.</summary>
    public static void StyleScrollViewer(ScrollViewer sv)
    {
        var scrollBarStyle = new Style(typeof(ScrollBar));
        scrollBarStyle.Setters.Add(new Setter(FrameworkElement.WidthProperty, 7.0));
        scrollBarStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        scrollBarStyle.Setters.Add(new Setter(Control.TemplateProperty, MinimalScrollBarTemplate()));
        sv.Resources[typeof(ScrollBar)] = scrollBarStyle;
    }

    private static ControlTemplate MinimalScrollBarTemplate()
    {
        const string xaml = @"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                 TargetType='ScrollBar'>
    <Grid Background='Transparent'>
        <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.Thumb>
                <Thumb>
                    <Thumb.Template>
                        <ControlTemplate TargetType='Thumb'>
                            <Border CornerRadius='3' Background='#40808080' Margin='1,0,1,0'/>
                        </ControlTemplate>
                    </Thumb.Template>
                </Thumb>
            </Track.Thumb>
        </Track>
    </Grid>
</ControlTemplate>";
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }


    /// <summary>WPF's stock checkbox looks a decade old; this is a rounded accent box with a stroked check.</summary>
    public static void StyleCheckBox(CheckBox box)
    {
        box.Foreground = Theme.BrushTextPrimary;
        box.FontFamily = AppFont;
        box.FontSize = 12.5;
        // Tighter vertical rhythm: five option rows plus the region card have to fit the shipped window
        // height without pushing the new settings below the scroll fold.
        box.Margin = new Thickness(0, 3, 0, 3);
        box.Cursor = Cursors.Hand;
        box.Template = CheckBoxTemplate();
    }

    private static ControlTemplate CheckBoxTemplate()
    {
        var template = new ControlTemplate(typeof(CheckBox));
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var box = new FrameworkElementFactory(typeof(Border));
        box.Name = "box";
        box.SetValue(FrameworkElement.WidthProperty, 18.0);
        box.SetValue(FrameworkElement.HeightProperty, 18.0);
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        box.SetBinding(Border.BackgroundProperty, Theme.Brush("BgCard"));
        box.SetBinding(Border.BorderBrushProperty, Theme.Brush("HairlineStrong"));
        box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var check = new FrameworkElementFactory(typeof(Path));
        check.Name = "check";
        check.SetValue(Path.DataProperty, Geometry.Parse("M 4.2,9.2 L 7.4,12.4 L 14,5.4"));
        check.SetValue(Shape.StrokeProperty, Brushes.White);
        check.SetValue(Shape.StrokeThicknessProperty, 1.9);
        check.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
        check.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
        check.SetValue(Shape.StrokeLineJoinProperty, PenLineJoin.Round);
        check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        check.SetValue(UIElement.RenderTransformProperty, new TranslateTransform(0.6, 0.6));
        box.AppendChild(check);

        var label = new FrameworkElementFactory(typeof(ContentPresenter));
        label.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 0, 0, 0));
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        label.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        label.SetValue(TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));

        panel.AppendChild(box);
        panel.AppendChild(label);
        template.VisualTree = panel;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Brush("AccentRing"), "box"));
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Brush("AccentSoft"), "box"));
        template.Triggers.Add(hover);

        // Declared after hover so the checked fill wins while hovered.
        var isChecked = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        isChecked.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Brush("Accent"), "box"));
        isChecked.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Brush("Accent"), "box"));
        isChecked.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "check"));
        template.Triggers.Add(isChecked);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5, "box"));
        template.Triggers.Add(disabled);
        return template;
    }

    public static void StyleComboBox(ComboBox box)
    {
        box.Background = Theme.BrushBgInput;
        box.Foreground = Theme.BrushTextPrimary;
        box.BorderBrush = Theme.BrushHairlineStrong;
        box.BorderThickness = new Thickness(1);
        box.FontFamily = AppFont;
        box.FontSize = 13;
        box.Padding = new Thickness(0);
        box.MinHeight = 34;
        box.VerticalContentAlignment = VerticalAlignment.Center;
        box.ItemContainerStyle = ComboBoxItemStyle();
        box.Template = ComboBoxTemplate();

        // The editable part inherits WPF's stock chrome; flatten it in place.
        box.Loaded += (_, _) =>
        {
            if (box.Template?.FindName("PART_EditableTextBox", box) is not TextBox editable) return;
            editable.Background = Brushes.Transparent;
            editable.BorderThickness = new Thickness(0);
            editable.Foreground = Theme.BrushTextPrimary;
            editable.CaretBrush = Theme.BrushAccent;
            editable.Padding = new Thickness(0);
            editable.FontFamily = AppFont;
            editable.FontSize = 13;
            editable.VerticalContentAlignment = VerticalAlignment.Center;
        };
    }

    private static Style ComboBoxItemStyle()
    {
        var style = new Style(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
        style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 1, 0, 1)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Theme.Brush("TextPrimary")));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12.5));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));

        var template = new ControlTemplate(typeof(ComboBoxItem));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "itemBorder";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.MarginProperty, new TemplateBindingExtension(Control.MarginProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(content);
        template.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Brush("BgCardHover"), "itemBorder"));
        template.Triggers.Add(hover);

        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Brush("AccentSoft"), "itemBorder"));
        template.Triggers.Add(selected);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static ControlTemplate ComboBoxTemplate()
    {
        var template = new ControlTemplate(typeof(ComboBox));
        var grid = new FrameworkElementFactory(typeof(Grid));

        // Dropdown surface: rounded card with the same soft elevation as everything else.
        var popup = new FrameworkElementFactory(typeof(Popup));
        popup.Name = "PART_Popup";
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
        popup.SetValue(Popup.IsOpenProperty, new TemplateBindingExtension(ComboBox.IsDropDownOpenProperty));

        var popupBorder = new FrameworkElementFactory(typeof(Border));
        popupBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(Radii.Control));
        popupBorder.SetBinding(Border.BackgroundProperty, Theme.Brush("BgCard"));
        popupBorder.SetBinding(Border.BorderBrushProperty, Theme.Brush("HairlineStrong"));
        popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        popupBorder.SetValue(Border.PaddingProperty, new Thickness(4));
        popupBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 4));
        popupBorder.SetValue(Border.EffectProperty, Ui.CardShadow());

        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(FrameworkElement.MaxHeightProperty, 260.0);
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        popupBorder.AppendChild(scroll);
        popup.AppendChild(popupBorder);
        grid.AppendChild(popup);

        // Field surface + chevron live on a stretched toggle button.
        var toggle = new FrameworkElementFactory(typeof(ToggleButton));
        toggle.Name = "toggleButton";
        toggle.SetValue(UIElement.FocusableProperty, false);
        toggle.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);
        // The field surface is drawn by this toggle, and nothing inherits Background into it, so it has to
        // be fed from the palette directly. Without this the stock Aero2 ToggleButton chrome shows through
        // and the field stays light grey in dark mode.
        toggle.SetBinding(Control.BackgroundProperty, Theme.Brush("BgInput"));
        toggle.SetBinding(Control.BorderBrushProperty, Theme.Brush("HairlineStrong"));
        var openBinding = new Binding("IsDropDownOpen") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent), Mode = BindingMode.TwoWay };
        toggle.SetValue(ToggleButton.IsCheckedProperty, openBinding);

        var toggleTemplate = new ControlTemplate(typeof(ToggleButton));
        var toggleBorder = new FrameworkElementFactory(typeof(Border));
        toggleBorder.Name = "templateRoot";
        toggleBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(Radii.Control));
        toggleBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        toggleBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        toggleBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

        var arrow = new FrameworkElementFactory(typeof(Path));
        arrow.Name = "arrow";
        arrow.SetValue(Path.DataProperty, Geometry.Parse("M 0,0 L 4.5,4.5 L 9,0"));
        arrow.SetBinding(Shape.StrokeProperty, Theme.Brush("TextMuted"));
        arrow.SetValue(Shape.StrokeThicknessProperty, 1.7);
        arrow.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
        arrow.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
        arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 14, 0));
        toggleBorder.AppendChild(arrow);
        toggleTemplate.VisualTree = toggleBorder;

        var toggleHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        toggleHover.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Brush("AccentRing"), "templateRoot"));
        toggleTemplate.Triggers.Add(toggleHover);

        var toggleOpen = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        toggleOpen.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Brush("Accent"), "templateRoot"));
        toggleTemplate.Triggers.Add(toggleOpen);

        var toggleDisabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        toggleDisabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5, "templateRoot"));
        toggleTemplate.Triggers.Add(toggleDisabled);

        toggle.SetValue(Control.TemplateProperty, toggleTemplate);
        grid.AppendChild(toggle);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.Name = "contentPresenter";
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
        content.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));
        content.SetBinding(TextElement.ForegroundProperty, Theme.Brush("TextPrimary"));
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 34, 0));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(UIElement.IsHitTestVisibleProperty, false);
        grid.AppendChild(content);

        var editable = new FrameworkElementFactory(typeof(TextBox));
        editable.Name = "PART_EditableTextBox";
        editable.SetValue(FrameworkElement.VisibilityProperty, Visibility.Collapsed);
        editable.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 34, 0));
        editable.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        editable.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        editable.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        editable.SetValue(Control.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        editable.SetBinding(TextBox.CaretBrushProperty, Theme.Brush("Accent"));
        editable.SetValue(Control.PaddingProperty, new Thickness(0));
        grid.AppendChild(editable);

        template.VisualTree = grid;

        var editableMode = new Trigger { Property = ComboBox.IsEditableProperty, Value = true };
        editableMode.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "PART_EditableTextBox"));
        editableMode.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "contentPresenter"));
        template.Triggers.Add(editableMode);
        return template;
    }

    /// <summary>Expander header as a rounded row with a chevron that points down when open.</summary>
    public static void StyleExpander(Expander exp)
    {
        exp.Foreground = Theme.BrushTextSecondary;
        exp.FontFamily = AppFont;
        exp.FontSize = 12.5;
        exp.Margin = new Thickness(0, 6, 0, 6);

        var template = new ControlTemplate(typeof(Expander));
        var root = new FrameworkElementFactory(typeof(DockPanel));
        root.SetValue(DockPanel.LastChildFillProperty, true);

        var toggle = new FrameworkElementFactory(typeof(ToggleButton));
        toggle.Name = "HeaderSite";
        toggle.SetValue(DockPanel.DockProperty, Dock.Top);
        toggle.SetValue(UIElement.FocusableProperty, false);
        toggle.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
        toggle.SetValue(ContentControl.ContentProperty, new TemplateBindingExtension(HeaderedContentControl.HeaderProperty));
        var expandedBinding = new Binding("IsExpanded") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent), Mode = BindingMode.TwoWay };
        toggle.SetValue(ToggleButton.IsCheckedProperty, expandedBinding);

        var toggleTemplate = new ControlTemplate(typeof(ToggleButton));
        var headerBorder = new FrameworkElementFactory(typeof(Border));
        headerBorder.Name = "headerBorder";
        headerBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(Radii.Control));
        headerBorder.SetBinding(Border.BackgroundProperty, Theme.Brush("BgCard"));
        headerBorder.SetBinding(Border.BorderBrushProperty, Theme.Brush("Hairline"));
        headerBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        headerBorder.SetValue(Border.PaddingProperty, new Thickness(10, 7, 10, 7));

        var chevron = new FrameworkElementFactory(typeof(Path));
        chevron.Name = "chevron";
        chevron.SetValue(Path.DataProperty, Geometry.Parse("M 0,0 L 4.5,4.5 L 9,0"));
        chevron.SetBinding(Shape.StrokeProperty, Theme.Brush("TextMuted"));
        chevron.SetValue(Shape.StrokeThicknessProperty, 1.7);
        chevron.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
        chevron.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
        chevron.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chevron.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 2, 0));
        chevron.SetValue(FrameworkElement.RenderTransformOriginProperty, new Point(0.5, 0.5));
        chevron.SetValue(UIElement.RenderTransformProperty, new RotateTransform(0));
        chevron.SetValue(DockPanel.DockProperty, Dock.Right);

        var headerLabel = new FrameworkElementFactory(typeof(ContentPresenter));
        headerLabel.Name = "headerLabel";
        headerLabel.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        headerLabel.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        // Bound to the palette, not to the templated ToggleButton's Foreground: that one carries the
        // control's default black, which is what made "원문 보기" unreadable on the dark card.
        headerLabel.SetBinding(TextElement.ForegroundProperty, Theme.Brush("TextPrimary"));

        var headerPanel = new FrameworkElementFactory(typeof(DockPanel));
        headerPanel.AppendChild(chevron);
        headerPanel.AppendChild(headerLabel);
        headerBorder.AppendChild(headerPanel);
        toggleTemplate.VisualTree = headerBorder;

        var headerHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        headerHover.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Brush("HairlineStrong"), "headerBorder"));
        headerHover.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Brush("BgCardHover"), "headerBorder"));
        toggleTemplate.Triggers.Add(headerHover);

        var headerOpen = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        headerOpen.Setters.Add(new Setter(FrameworkElement.RenderTransformProperty, new RotateTransform(180), "chevron"));
        headerOpen.Setters.Add(new Setter(TextElement.ForegroundProperty, Theme.Brush("TextPrimary"), "headerBorder"));
        toggleTemplate.Triggers.Add(headerOpen);

        toggle.SetValue(Control.TemplateProperty, toggleTemplate);
        root.AppendChild(toggle);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.Name = "ExpandSite";
        content.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 6, 0, 0));
        content.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        root.AppendChild(content);
        template.VisualTree = root;

        var expanded = new Trigger { Property = Expander.IsExpandedProperty, Value = true };
        expanded.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "ExpandSite"));
        template.Triggers.Add(expanded);

        exp.Template = template;
    }

    public static FrameworkElement Chip(string text)
    {
        var border = new Border
        {
            Background = Theme.BrushBgInset,
            BorderBrush = Theme.BrushHairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radii.Chip),
            Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center
        };
        border.Child = new TextBlock
        {
            Text = text,
            Foreground = Theme.BrushTextMuted,
            FontFamily = AppFont,
            FontSize = 11,
            FontWeight = FontWeights.Medium
        };
        return border;
    }

    /// <summary>Section heading inside cards.</summary>
    public static TextBlock Heading(string text, double top = 0) => new()
    {
        Text = text,
        Foreground = Theme.BrushTextPrimary,
        FontFamily = AppFont,
        FontSize = 13.5,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, top, 0, 2)
    };

    /// <summary>Quiet uppercase-ish label for grouping.</summary>
    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        Foreground = Theme.BrushTextMuted,
        FontFamily = AppFont,
        FontSize = 11.5,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 6)
    };

    /// <summary>Standard card surface: white, hairline edge, low elevation.</summary>
    public static Border Card(double padding = 14, double radius = Radii.Card, bool elevated = false)
    {
        var border = new Border
        {
            Background = Theme.BrushBgCard,
            BorderBrush = Theme.BrushHairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(padding)
        };
        if (elevated) border.Effect = CardShadow();
        return border;
    }
}
