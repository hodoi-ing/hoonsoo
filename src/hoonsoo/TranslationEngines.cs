using System;
using System.Collections.Generic;
using System.Linq;

namespace Hoonsoo;

/// <summary>
/// Which backend turns the captured text into the target language, and the language pair it uses.
/// Kept as strings so settings.json stays hand-editable, like Theme and RegionResult: an unknown
/// or missing value has to fall back to the free engine rather than fail to load.
/// </summary>
public static class TranslationEngines
{
    /// <summary>Undocumented Google web endpoint plus MyMemory. No key, works out of the box.</summary>
    public const string Free = "free";
    /// <summary>Google Cloud Translation v2. Needs an API key.</summary>
    public const string Google = "google";
    /// <summary>DeepL API. Free keys end with ":fx" and use a different host.</summary>
    public const string DeepL = "deepl";
    /// <summary>Naver Cloud NMT. Needs a Client ID and secret, entered as "ClientID:ClientSecret".</summary>
    public const string Papago = "papago";

    public static readonly string[] All = [Free, Google, DeepL, Papago];

    public static string Normalize(string? value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return All.Contains(normalized) ? normalized : Free;
    }

    /// <summary>True when the engine cannot work without a key; drives the settings hint and the failure text.</summary>
    public static bool RequiresKey(string? engine) => Normalize(engine) != Free;

    public sealed record LanguageOption(string Code, string Label);

    /// <summary>
    /// Languages offered in settings. Deliberately short: the local OCR model is still English-only,
    /// so a non-English source only makes sense for UI Automation text or the free engine's own detection.
    /// </summary>
    public static readonly LanguageOption[] Languages =
    [
        new("en", "영어"),
        new("ko", "한국어"),
        new("ja", "일본어"),
        new("zh-CN", "중국어(간체)"),
        new("zh-TW", "중국어(번체)"),
        new("es", "스페인어"),
        new("fr", "프랑스어"),
        new("de", "독일어"),
        new("ru", "러시아어"),
        new("pt", "포르투갈어"),
        new("it", "이탈리아어"),
        new("vi", "베트남어"),
        new("th", "태국어"),
        new("id", "인도네시아어"),
        new("ar", "아랍어"),
        new("hi", "힌디어"),
    ];

    /// <summary>Known code or the caller's fallback, so a hand-edited settings.json cannot break a request.</summary>
    public static string NormalizeCode(string? code, string fallback)
    {
        var value = (code ?? "").Trim();
        var hit = Languages.FirstOrDefault(x => string.Equals(x.Code, value, StringComparison.OrdinalIgnoreCase));
        return hit?.Code ?? fallback;
    }

    public static string Label(string code)
    {
        var value = (code ?? "").Trim();
        var hit = Languages.FirstOrDefault(x => string.Equals(x.Code, value, StringComparison.OrdinalIgnoreCase));
        return hit?.Label ?? value;
    }

    /// <summary>DeepL wants upper-case codes and collapses the two Chinese variants into one.</summary>
    public static string ToDeepL(string code) => code.ToUpperInvariant() switch
    {
        "ZH-CN" => "ZH",
        "ZH-TW" => "ZH",
        var other => other,
    };
}

/// <summary>
/// The engine settings a provider instance is built from. Immutable on purpose: saving settings builds a
/// new provider instead of mutating a live one, so an in-flight translation keeps its own configuration.
/// </summary>
public sealed record TranslationOptions(string Engine, string Source, string Target, string ApiKey)
{
    public static readonly TranslationOptions Default = new(TranslationEngines.Free, "en", "ko", "");

    public static TranslationOptions From(Settings settings) => new(
        TranslationEngines.Normalize(settings.Engine),
        TranslationEngines.NormalizeCode(settings.SourceLanguage, "en"),
        TranslationEngines.NormalizeCode(settings.TargetLanguage, "ko"),
        (settings.ApiKey ?? "").Trim());
}
