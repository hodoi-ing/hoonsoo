using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hoonsoo;

/// <summary>
/// Motion tokens and helpers, ported from design-engineering rules to WPF.
/// Principles applied:
///  - Strong ease-out on enter AND exit. Never ease-in (it feels sluggish).
///  - UI animations stay under 300ms (press 110ms, small popover 140-180ms).
///  - Nothing animates from scale(0); entrance starts at 0.95-0.98 with opacity 0.
///  - Popovers are origin-aware: they scale from the cursor, not from center.
///  - Elements appearing together get a 30-80ms stagger.
/// </summary>
public static class Motion
{
    public static readonly Duration Press = new(TimeSpan.FromMilliseconds(110));
    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(140));
    public static readonly Duration Base = new(TimeSpan.FromMilliseconds(180));
    public static readonly Duration Slow = new(TimeSpan.FromMilliseconds(240));

    /// <summary>Strong ease-out, cubic-bezier(0.23, 1, 0.32, 1). Enter/exit default.</summary>
    public static EasingFunctionBase EaseOut() => new CubicBezierEase(0.23, 1, 0.32, 1);

    /// <summary>Strong ease-in-out, cubic-bezier(0.77, 0, 0.175, 1). On-screen movement.</summary>
    public static EasingFunctionBase EaseInOut() => new CubicBezierEase(0.77, 0, 0.175, 1);

    public static (ScaleTransform Scale, TranslateTransform Translate) EnsureTransform(FrameworkElement element, Point? origin = null)
    {
        if (element.RenderTransform is TransformGroup existing && existing.Children.Count == 2
            && existing.Children[0] is ScaleTransform existingScale && existing.Children[1] is TranslateTransform existingTranslate)
        {
            if (origin is { } requested) element.RenderTransformOrigin = requested;
            return (existingScale, existingTranslate);
        }

        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        element.RenderTransform = group;
        element.RenderTransformOrigin = origin ?? new Point(0, 0);
        return (scale, translate);
    }

    /// <summary>Fade + scale + slide entrance. Nothing ever enters from scale(0).</summary>
    public static void Enter(FrameworkElement element, double fromY = 6, double fromScale = 0.97, Point? origin = null, Duration? duration = null, double delayMs = 0)
    {
        var (scale, translate) = EnsureTransform(element, origin);
        var d = duration ?? Base;
        var ease = EaseOut();
        var delay = TimeSpan.FromMilliseconds(delayMs);

        element.Opacity = 1;
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.Y = 0;

        // FillBehavior.Stop so the clock retires when the motion ends instead of holding the property.
        // A held opacity/transform clock keeps the element on an intermediate alpha layer, which forces
        // grayscale anti-aliasing for the rest of its life — the text never gets its ClearType back.
        // Base values below (opacity 1, scale 1, offset 0) are the animation's end state, so stopping
        // the clock is visually identical to holding it.
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease, BeginTime = delay, FillBehavior = FillBehavior.Stop });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fromScale, 1, d) { EasingFunction = ease, BeginTime = delay, FillBehavior = FillBehavior.Stop });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(fromScale, 1, d) { EasingFunction = ease, BeginTime = delay, FillBehavior = FillBehavior.Stop });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, d) { EasingFunction = ease, BeginTime = delay, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>Fade + slide only. Used for content swapping (status to result, tab panes).</summary>
    public static void FadeSlideIn(FrameworkElement element, double fromY = 5, Duration? duration = null, double delayMs = 0)
        => Enter(element, fromY, 1.0, null, duration ?? Fast, delayMs);

    /// <summary>Fade out and invoke <paramref name="completed"/> when the exit motion finishes.</summary>
    public static void Exit(FrameworkElement element, Action? completed = null, double toY = 4, double toScale = 1.0, Duration? duration = null)
    {
        var (scale, translate) = EnsureTransform(element);
        var d = duration ?? Fast;
        var ease = EaseOut();
        var storyboard = new Storyboard();

        storyboard.Children.Add(Animate(element, UIElement.OpacityProperty, 1, 0, d, ease));
        storyboard.Children.Add(Animate(scale, ScaleTransform.ScaleXProperty, 1, toScale, d, ease));
        storyboard.Children.Add(Animate(scale, ScaleTransform.ScaleYProperty, 1, toScale, d, ease));
        storyboard.Children.Add(Animate(translate, TranslateTransform.YProperty, 0, toY, d, ease));

        if (completed is not null) storyboard.Completed += (_, _) => completed();
        storyboard.Begin();
    }

    /// <summary>Fade a window in. Used for transparent popup surfaces.</summary>
    public static void EnterWindow(Window window, Duration? duration = null)
    {
        window.Opacity = 1;
        // Same reason as Enter: a window that is still "animating" its opacity composites through an
        // alpha layer, so every glyph inside it loses subpixel rendering.
        window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration ?? Fast) { EasingFunction = EaseOut(), FillBehavior = FillBehavior.Stop });
    }

    /// <summary>Fade a window out while its content scales away, then run <paramref name="completed"/>.</summary>
    public static void ExitWindow(Window window, FrameworkElement content, Action? completed = null, double toScale = 0.99, double toY = 3, Duration? duration = null)
    {
        var (scale, translate) = EnsureTransform(content);
        var d = duration ?? Fast;
        var ease = EaseOut();
        var storyboard = new Storyboard();

        storyboard.Children.Add(Animate(window, UIElement.OpacityProperty, window.Opacity, 0, d, ease));
        storyboard.Children.Add(Animate(scale, ScaleTransform.ScaleXProperty, 1, toScale, d, ease));
        storyboard.Children.Add(Animate(scale, ScaleTransform.ScaleYProperty, 1, toScale, d, ease));
        storyboard.Children.Add(Animate(translate, TranslateTransform.YProperty, 0, toY, d, ease));

        if (completed is not null) storyboard.Completed += (_, _) => completed();
        storyboard.Begin();
    }

    /// <summary>Stagger 30-80ms per item so lists settle instead of snapping in at once.</summary>
    public static void StaggerIn(Panel panel, double stepMs = 40, double fromY = 5, int maxItems = 6)
    {
        for (int i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is not FrameworkElement child) continue;
            int index = Math.Min(i, maxItems);
            FadeSlideIn(child, fromY, Fast, index * stepMs);
        }
    }

    /// <summary>Buttons must feel responsive to press: scale 0.97 on active.</summary>
    public static void AttachPress(FrameworkElement element, double depth = 0.97)
    {
        var (scale, _) = EnsureTransform(element, new Point(0.5, 0.5));
        bool pressed = false;

        void To(double from, double to, Duration d)
        {
            var ease = EaseOut();
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(from, to, d) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(from, to, d) { EasingFunction = ease });
        }

        element.PreviewMouseLeftButtonDown += (_, _) => { pressed = true; To(1, depth, Press); };
        element.PreviewMouseLeftButtonUp += (_, _) => { if (!pressed) return; pressed = false; To(depth, 1, Fast); };
        element.MouseLeave += (_, _) => { if (!pressed) return; pressed = false; To(depth, 1, Fast); };
        element.IsEnabledChanged += (_, _) => { if (!pressed) return; pressed = false; To(depth, 1, Fast); };
    }

    /// <summary>Hover palette installed for one locally-owned brush. Mutable so a theme swap can re-seed it.</summary>
    private sealed class HoverRoles
    {
        public Color Normal;
        public Color Hover;
        public Color Pressed;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SolidColorBrush, HoverRoles> hoverRoles = new();

    /// <summary>
    /// Animated color transition for hover states (hover/color changes ease).
    /// The brush must be owned by this control: animating a shared theme brush would pin it to one colour.
    /// </summary>
    public static void AttachHover(SolidColorBrush brush, FrameworkElement target, Color normal, Color hover, Color pressed)
    {
        var roles = new HoverRoles { Normal = normal, Hover = hover, Pressed = pressed };
        hoverRoles.Remove(brush);
        hoverRoles.Add(brush, roles);

        brush.Color = roles.Normal;
        target.MouseEnter += (_, _) => AnimateColor(brush, roles.Hover, Fast);
        target.MouseLeave += (_, _) => AnimateColor(brush, roles.Normal, Fast);
        target.PreviewMouseLeftButtonDown += (_, _) => AnimateColor(brush, roles.Pressed, Press);
        target.PreviewMouseLeftButtonUp += (_, _) => AnimateColor(brush, target.IsMouseOver ? roles.Hover : roles.Normal, Fast);
    }

    /// <summary>
    /// Re-seed a brush that <see cref="AttachHover"/> wired so it matches a new palette. An animation owns
    /// ColorProperty, so the running animation must be cleared before the local value can take effect.
    /// </summary>
    public static void RetargetHover(SolidColorBrush brush, FrameworkElement target, Color normal, Color hover, Color pressed)
    {
        if (!hoverRoles.TryGetValue(brush, out var roles)) return;
        roles.Normal = normal;
        roles.Hover = hover;
        roles.Pressed = pressed;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        brush.Color = target.IsMouseOver ? hover : normal;
    }

    public static void AnimateColor(SolidColorBrush brush, Color to, Duration duration)
    {
        // Never animate a shared palette brush: the animation would take ownership of Color and the next
        // theme swap would silently do nothing. Animate a locally-owned brush instead.
        if (brush.IsFrozen) return;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, duration) { EasingFunction = EaseOut() });
    }

    private static DoubleAnimation Animate(DependencyObject target, DependencyProperty property, double from, double to, Duration duration, EasingFunctionBase ease)
    {
        var animation = new DoubleAnimation(from, to, duration) { EasingFunction = ease };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        return animation;
    }
}

/// <summary>
/// CSS-style cubic-bezier easing so we can use the strong curves the rules call for
/// (WPF's built-in CubicEase is much weaker). EaseInCore holds the raw curve and the
/// mode is pinned to EaseIn so WPF does not mirror it.
/// </summary>
public sealed class CubicBezierEase : EasingFunctionBase
{
    private readonly double x1;
    private readonly double y1;
    private readonly double x2;
    private readonly double y2;

    public CubicBezierEase()
        : this(0.23, 1, 0.32, 1)
    {
    }

    public CubicBezierEase(double x1, double y1, double x2, double y2)
    {
        this.x1 = x1;
        this.y1 = y1;
        this.x2 = x2;
        this.y2 = y2;
        EasingMode = EasingMode.EaseIn;
    }

    protected override double EaseInCore(double normalizedTime)
    {
        if (normalizedTime <= 0) return 0;
        if (normalizedTime >= 1) return 1;
        double t = SolveT(normalizedTime);
        return Curve(t, y1, y2);
    }

    protected override Freezable CreateInstanceCore() => new CubicBezierEase(x1, y1, x2, y2);

    private double SolveT(double x)
    {
        double t = x;
        for (int i = 0; i < 8; i++)
        {
            double error = Curve(t, x1, x2) - x;
            if (Math.Abs(error) < 1e-5) break;
            double slope = Derivative(t, x1, x2);
            if (Math.Abs(slope) < 1e-6) break;
            t -= error / slope;
        }
        return Math.Clamp(t, 0, 1);
    }

    private static double Curve(double t, double a1, double a2)
    {
        double inverse = 1 - t;
        return (3 * inverse * inverse * t * a1) + (3 * inverse * t * t * a2) + (t * t * t);
    }

    private static double Derivative(double t, double a1, double a2)
    {
        double inverse = 1 - t;
        return (3 * inverse * inverse * a1) + (6 * inverse * t * (a2 - a1)) + (3 * t * t * (1 - a2));
    }
}
