using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Hoonsoo;
public record DetectedTerm(string Term, string KoreanName);
public record TranslationResult(string OriginalText, string TranslatedText, IReadOnlyList<DetectedTerm> DetectedTerms, string CaptureMethod, string? Error = null);
public record CaptureResult(string Text, string Method, bool Quality = true);
public sealed record Settings
{
    public uint Modifiers { get; set; } = 3;
    public uint Key { get; set; } = 0x44;
    public uint RegionModifiers { get; set; } = 3;
    public uint RegionKey { get; set; } = 0x52;
    public uint AreaModifiers { get; set; } = 3;
    public uint AreaKey { get; set; } = 0x4F;
    public uint ArModifiers { get; set; } = 6;
    public uint ArKey { get; set; } = 0x20;
    public bool Terms { get; set; } = true;
    public bool ProtectCode { get; set; } = true;
    public bool Ocr { get; set; } = true;
    public bool AutoClose { get; set; } = true;
    public int CloseSeconds { get; set; } = 20;
    public bool Startup { get; set; }
    /// <summary>"system" | "light" | "dark". Kept as a string so settings.json stays human-editable.</summary>
    public string Theme { get; set; } = "system";
    /// <summary>"overlay" | "popup" | "both" — where a dragged region answers. String for the same reason as <see cref="Theme"/>.</summary>
    public string RegionResult { get; set; } = RegionResultModes.Both;
    /// <summary>On-screen size for the full-screen AR subtitles, in percent. Independent of <see cref="RegionTextPercent"/>.</summary>
    public int ArTextPercent { get; set; } = 100;
    /// <summary>On-screen size for the dragged-area plate, in percent. Independent of <see cref="ArTextPercent"/>.</summary>
    public int RegionTextPercent { get; set; } = 100;
    public HashSet<string> Excluded { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double ArTextScale => ArTextPercent / 100.0;
    public double RegionTextScale => RegionTextPercent / 100.0;
    /// <summary>Offered sizes. The slider-less picker and <see cref="SnapTextPercent"/> share this list.</summary>
    public static readonly int[] TextPercentSteps = [60, 70, 80, 90, 100, 110, 125, 150, 175, 200];
    /// <summary>Nearest offered size, which also bounds the value: 1000 % and -5 % land on 200 % and 60 %.</summary>
    public static int SnapTextPercent(int value)
    {
        int best = TextPercentSteps[0];
        foreach (int step in TextPercentSteps) if (Math.Abs(step - value) < Math.Abs(best - value)) best = step;
        return best;
    }
    public Settings Copy() => this with { Excluded = new(Excluded, StringComparer.OrdinalIgnoreCase) };
    public void Normalize()
    {
        Excluded = new((Excluded ?? []).Select(ProcessIdentity.Normalize).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
        CloseSeconds = Math.Clamp(CloseSeconds, 3, 300);
        Theme = Hoonsoo.Theme.Normalize(this.Theme);
        RegionResult = RegionResultModes.Normalize(RegionResult);
        ArTextPercent = SnapTextPercent(ArTextPercent);
        RegionTextPercent = SnapTextPercent(RegionTextPercent);
        if (!Hotkey.IsValid(Modifiers, Key)) { Key = 0x44; Modifiers = 3; }
        if (!Hotkey.IsValid(RegionModifiers, RegionKey)) { RegionKey = 0x52; RegionModifiers = 3; }
        if (!Hotkey.IsValid(AreaModifiers, AreaKey)) { AreaKey = 0x4F; AreaModifiers = 3; }
        if (!Hotkey.IsValid(ArModifiers, ArKey)) { ArKey = 0x20; ArModifiers = 6; }
    }
}
/// <summary>
/// Where the answer to a dragged region goes. Both surfaces exist already (the click-through plate drawn by
/// <see cref="ArOverlayWindow"/> and the popup window), so the mode only picks which of them is used.
/// </summary>
public static class RegionResultModes
{
    public const string Overlay = "overlay";
    public const string Popup = "popup";
    public const string Both = "both";
    /// <summary>Unknown, empty and null all fall back to <see cref="Both"/>, the pre-setting behaviour.</summary>
    public static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        Overlay => Overlay,
        Popup => Popup,
        _ => Both,
    };
    public static bool ShowsOverlay(string? value) => Normalize(value) is Overlay or Both;
    public static bool ShowsPopup(string? value) => Normalize(value) is Popup or Both;
}
/// <summary>
/// How a region selection ended. The selector used to collapse every ending into "null", which is why a
/// refused drag looked identical to pressing ESC — nothing happened and nothing said why.
/// </summary>
public enum RegionSelectionOutcome { Selected, Cancelled, TooSmall, TooLarge, Unavailable }
public static class RegionSelection
{
    /// <summary>What to tell the user, or null when they cancelled on purpose and need no message.</summary>
    public static string? Guidance(RegionSelectionOutcome outcome) => outcome switch
    {
        RegionSelectionOutcome.TooSmall => "영역이 너무 작습니다. 8픽셀 이상 드래그하세요.",
        RegionSelectionOutcome.TooLarge => "영역이 너무 큽니다. 더 작은 영역을 드래그하세요.",
        RegionSelectionOutcome.Unavailable => "영역 선택 창을 열 수 없습니다. 다시 시도하세요.",
        _ => null,
    };
}
public static class ProcessIdentity
{
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        name = name.Trim().Replace('\\', '/').Split('/').Last().ToLowerInvariant();
        return name.Length == 0 ? "" : name.EndsWith(".exe", StringComparison.Ordinal) ? name : name + ".exe";
    }
    public static bool Allowed(string? name, Settings settings) => name is not null && !settings.Excluded.Contains(Normalize(name));
}
public static class TextQuality
{
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 6000) return "";
        var lines = text.Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct();
        var s = string.Join("\n", lines);
        if (s.Length < 3 || s.Length > 4000 || s.Count(char.IsLetter) < 2 || !Regex.IsMatch(s, "[a-zA-Z]{2}")) return "";
        if (s.Count(c => char.IsControl(c) && c != '\n' && c != '\t') > 0) return "";
        return s;
    }
}
public static class CapturePipeline
{
    public static async Task<CaptureResult?> RunAsync(Func<CancellationToken, Task<CaptureResult?>> uia, Func<CancellationToken, Task<CaptureResult?>> ocr, bool enableOcr, CancellationToken token)
    {
        for (int i = 0; i < 2; i++)
        {
            token.ThrowIfCancellationRequested();
            var r = await uia(token);
            if (r is { Quality: true } && TextQuality.Clean(r.Text).Length > 0) return r;
            if (i == 0) await Task.Delay(140, token);
        }
        return enableOcr ? await ocr(token) : null;
    }
}
public static class Placement
{
    public static (int X, int Y) Clamp(int x, int y, int width, int height, int left, int top, int right, int bottom)
        => (Math.Clamp(x, left, Math.Max(left, right - width)), Math.Clamp(y, top, Math.Max(top, bottom - height)));

    /// <summary>
    /// Picks a spot that covers none of <paramref name="keepClear"/> — right, left, below, then above the
    /// source — and falls back to the caller's own default when even those would leave the screen. Shared by
    /// the popup and the dragged-area plate so neither opens on top of the text the user is reading.
    /// </summary>
    public static (int X, int Y) BesideSource(int width, int height, int fallbackX, int fallbackY, Rectangle screen, IReadOnlyList<Rectangle> keepClear)
    {
        foreach (var source in keepClear)
        {
            (int X, int Y)[] candidates =
            [
                (source.Right + 12, source.Top),
                (source.Left - width - 12, source.Top),
                (source.Left, source.Bottom + 12),
                (source.Left, source.Top - height - 12)
            ];
            foreach (var (x, y) in candidates)
            {
                if (x < screen.Left || y < screen.Top || x + width > screen.Right || y + height > screen.Bottom) continue;
                if (keepClear.Any(r => Overlaps(x, y, width, height, r))) continue;
                return (x, y);
            }
        }
        return Clamp(fallbackX, fallbackY, width, height, screen.Left, screen.Top, screen.Right, screen.Bottom);
    }

    public static bool Overlaps(int x, int y, int width, int height, Rectangle r)
        => x < r.Right && r.Left < x + width && y < r.Bottom && r.Top < y + height;
}
