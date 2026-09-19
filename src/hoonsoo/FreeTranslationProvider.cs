using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Hoonsoo;
public sealed class FreeTranslationProvider : IDisposable
{
    private const int MaxConcurrentHttpRequests = 4;
    private const int MaxCachedResults = 100;
    private static DateTime googleRateLimitCooldownUntil = DateTime.MinValue;
    private static readonly object cooldownLock = new();
    public static bool IsGoogleRateLimited
    {
        get { lock (cooldownLock) return DateTime.UtcNow < googleRateLimitCooldownUntil; }
        set { lock (cooldownLock) googleRateLimitCooldownUntil = value ? DateTime.UtcNow.AddMinutes(2) : DateTime.MinValue; }
    }
    private readonly HttpClient client;
    private readonly TranslationOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, TranslationResult> cache = new();
    private readonly Queue<string> cacheOrder = new();
    public FreeTranslationProvider(HttpMessageHandler? handler = null, TranslationOptions? options = null)
    {
        this.options = options ?? TranslationOptions.Default;
        client = handler is null ? new() : new(handler);
        client.Timeout = TimeSpan.FromSeconds(20);
    }
    public async Task<TranslationResult> TranslateAsync(string text, string method, bool terms, bool protect, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (text.Length is 0 or > 4000) return new(text, "", [], method, "번역할 텍스트 길이가 올바르지 않습니다.");
            var id = $"{options.Engine}:{options.Source}:{options.Target}:{terms}:{protect}:{text}";
            if (cache.TryGetValue(id, out var saved)) return saved with { CaptureMethod = method };
            var protection = new CodeProtection(text, protect);
            var pattern = protection.Tokens.Count == 0 ? "(?!)" : string.Join("|", protection.Tokens.Keys.Select(Regex.Escape));
            var segments = new List<(string Text, bool Prose)>();
            int offset = 0;
            foreach (Match match in Regex.Matches(protection.Text, pattern))
            {
                segments.Add((protection.Text[offset..match.Index], true));
                segments.Add((match.Value, false));
                offset = match.Index + match.Length;
            }
            segments.Add((protection.Text[offset..], true));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(25));
            var translated = await TranslateProseAsync(segments, deadline.Token);
            var output = new StringBuilder();
            foreach (var segment in translated) output.Append(segment);
            var result = new TranslationResult(text, protection.Restore(output.ToString()), terms ? Detect(text) : [], method);
            Remember(id, result); return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(text, "", [], method, "무료 번역 요청 시간이 초과되었습니다."); }
        catch { return new(text, "", [], method, FailureMessage()); }
        finally { gate.Release(); }
    }
    // Prose spans are independent, so they are translated concurrently (bounded) while protected spans are copied verbatim.
    // Protected values never enter the translation service. Prose that is equal after trimming maps to one outgoing
    // query and is fetched once; each occurrence keeps its own surrounding whitespace, which is restored separately.
    private async Task<string[]> TranslateProseAsync(IReadOnlyList<(string Text, bool Prose)> segments, CancellationToken token)
    {
        var results = new string[segments.Count];
        var pending = new Dictionary<string, Task<string>>(StringComparer.Ordinal);
        using var limit = new SemaphoreSlim(MaxConcurrentHttpRequests, MaxConcurrentHttpRequests);
        for (int i = 0; i < segments.Count; i++)
        {
            if (!segments[i].Prose) continue;
            var span = segments[i].Text;
            if (!Regex.IsMatch(span, @"\p{L}")) { results[i] = span; continue; }
            var query = span.Trim();
            if (!pending.ContainsKey(query)) pending[query] = TranslateLimitedAsync(query, limit, token);
        }
        if (pending.Count != 0) await Task.WhenAll(pending.Values);
        for (int i = 0; i < segments.Count; i++)
        {
            if (!segments[i].Prose) { results[i] = segments[i].Text; continue; }
            var span = segments[i].Text;
            if (!pending.TryGetValue(span.Trim(), out var task)) { results[i] = span; continue; }
            // The service only ever receives the trimmed query, so this occurrence's own edge whitespace is restored here.
            results[i] = span[..(span.Length - span.TrimStart().Length)] + await task + span[span.TrimEnd().Length..];
        }
        return results;
    }
    private async Task<string> TranslateLimitedAsync(string text, SemaphoreSlim limit, CancellationToken token)
    {
        await limit.WaitAsync(token);
        try { return await TranslateSpan(text, token); }
        finally { limit.Release(); }
    }
    /// <summary>
    /// One trimmed span in, one translated span out. The engine is fixed for the lifetime of the provider,
    /// so every path here reads the same <see cref="options"/> the instance was built from.
    /// </summary>
    private async Task<string> TranslateSpan(string text, CancellationToken token)
    {
        if (!Regex.IsMatch(text, @"\p{L}")) return text;
        token.ThrowIfCancellationRequested();
        // Same language on both sides: no service can make this shorter, and DeepL rejects it outright.
        if (string.Equals(options.Source, options.Target, StringComparison.OrdinalIgnoreCase)) return text;
        var query = text.Trim();
        string? result = options.Engine switch
        {
            TranslationEngines.Google => await GoogleAsync(query, token),
            TranslationEngines.DeepL => await DeepLAsync(query, token),
            TranslationEngines.Papago => await PapagoAsync(query, token),
            _ => await FreeAsync(query, token),
        };
        if (string.IsNullOrWhiteSpace(result)) throw new FormatException();
        return text[..(text.Length - text.TrimStart().Length)] + result + text[text.TrimEnd().Length..];
    }

    /// <summary>
    /// Default engine: the undocumented Google web endpoint, then MyMemory. No key and no account, which is why
    /// the app works out of the box; a single 429 parks both for two minutes.
    /// </summary>
    private async Task<string?> FreeAsync(string text, CancellationToken token)
    {
        string? result = null;
        if (!IsGoogleRateLimited)
        {
            try
            {
                using var response = await client.GetAsync($"https://translate.googleapis.com/translate_a/single?client=gtx&sl={options.Source}&tl={options.Target}&dt=t&q=" + Uri.EscapeDataString(text), token);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    IsGoogleRateLimited = true;
                }
                else if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(token);
                    if (json.Length <= 128000)
                    {
                        using var doc = JsonDocument.Parse(json);
                        result = string.Concat(doc.RootElement[0].EnumerateArray().Select(x => x[0].GetString()));
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(result))
        {
            token.ThrowIfCancellationRequested();
            using var response = await client.GetAsync($"https://api.mymemory.translated.net/get?langpair={options.Source}|{options.Target}&q=" + Uri.EscapeDataString(text), token);
            response.EnsureSuccessStatusCode();
            result = ParseMyMemory(await response.Content.ReadAsStringAsync(token));
        }

        return result;
    }

    /// <summary>Google Cloud Translation v2. A bad or exhausted key comes back as a non-success status.</summary>
    private async Task<string?> GoogleAsync(string text, CancellationToken token)
    {
        if (options.ApiKey.Length == 0) return null;
        using var body = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["q"] = new[] { text },
                ["source"] = options.Source,
                ["target"] = options.Target,
                ["format"] = "text",
            }),
            Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("https://translation.googleapis.com/language/translate/v2?key=" + Uri.EscapeDataString(options.ApiKey), body, token);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync(token);
        if (json.Length > 128000) return null;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("translations", out var list)) return null;
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0) return null;
        if (!list[0].TryGetProperty("translatedText", out var translated)) return null;
        // The v2 API HTML-escapes its answers, so an & in the source arrives as &amp;.
        return System.Net.WebUtility.HtmlDecode(translated.GetString() ?? "").Trim();
    }

    /// <summary>
    /// DeepL API. A key ending in ":fx" is the free tier and lives on its own host, so the endpoint is picked
    /// from the key rather than asked for twice in settings.
    /// </summary>
    private async Task<string?> DeepLAsync(string text, CancellationToken token)
    {
        if (options.ApiKey.Length == 0) return null;
        var endpoint = options.ApiKey.EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["text"] = text,
            ["source_lang"] = TranslationEngines.ToDeepL(options.Source),
            ["target_lang"] = TranslationEngines.ToDeepL(options.Target),
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = body };
        request.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + options.ApiKey);
        using var response = await client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync(token);
        if (json.Length > 128000) return null;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("translations", out var list)) return null;
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0) return null;
        return list[0].TryGetProperty("text", out var translated) ? translated.GetString()?.Trim() : null;
    }

    /// <summary>
    /// Naver Cloud NMT. It authenticates with two headers, so the key field carries "ClientID:ClientSecret"
    /// instead of adding a second text box for one engine.
    /// </summary>
    private async Task<string?> PapagoAsync(string text, CancellationToken token)
    {
        var separator = options.ApiKey.IndexOf(':');
        if (separator <= 0) return null;
        var id = options.ApiKey[..separator].Trim();
        var secret = options.ApiKey[(separator + 1)..].Trim();
        if (id.Length == 0 || secret.Length == 0) return null;
        using var body = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["source"] = options.Source,
                ["target"] = options.Target,
                ["text"] = text,
            }),
            Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://naveropenapi.apigw.ntruss.com/nmt/v1/translation") { Content = body };
        request.Headers.TryAddWithoutValidation("X-NCP-APIGW-API-KEY-ID", id);
        request.Headers.TryAddWithoutValidation("X-NCP-APIGW-API-KEY", secret);
        using var response = await client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync(token);
        if (json.Length > 128000) return null;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("message", out var message) || !message.TryGetProperty("result", out var result)) return null;
        return result.TryGetProperty("translatedText", out var translated) ? translated.GetString()?.Trim() : null;
    }

    /// <summary>A missing or rejected key must not read as a network outage, so the text follows the engine.</summary>
    private string FailureMessage() => options.Engine switch
    {
        TranslationEngines.Free => "무료 온라인 번역에 연결하지 못했습니다. 인터넷 연결을 확인하고 다시 시도하세요.",
        _ when options.ApiKey.Length == 0 => "선택한 번역 엔진에는 API 키가 필요합니다. 설정에서 키를 입력하세요.",
        _ => "번역 서비스가 요청을 거부했습니다. API 키와 사용량 한도를 확인하세요.",
    };
    // MyMemory reports failures inside an HTTP 200 payload, so the payload's real responseStatus is required to be a
    // success; only the service's quota notices may additionally suppress a result. No other message is treated as an error.
    private static string? ParseMyMemory(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("responseStatus", out var status) || !IsSuccessfulStatus(status)) return null;
        if (!root.TryGetProperty("responseData", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        if (!data.TryGetProperty("translatedText", out var translated) || translated.ValueKind != JsonValueKind.String) return null;
        var text = translated.GetString()?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Contains("QUERY LENGTH LIMIT", StringComparison.OrdinalIgnoreCase)
            || text.Contains("YOU USED ALL AVAILABLE FREE TRANSLATIONS", StringComparison.OrdinalIgnoreCase)) return null;
        return text;
    }
    private static bool IsSuccessfulStatus(JsonElement status) => status.ValueKind switch
    {
        JsonValueKind.Number => status.TryGetInt32(out var code) && code == 200,
        JsonValueKind.String => status.GetString() is "200",
        _ => false
    };
    private void Remember(string id, TranslationResult result)
    {
        // Evict the oldest entry one at a time so a full cache costs a single result instead of all of them.
        while (cache.Count >= MaxCachedResults && cacheOrder.Count != 0) cache.Remove(cacheOrder.Dequeue());
        cache[id] = result;
        cacheOrder.Enqueue(id);
    }
    private static IReadOnlyList<DetectedTerm> Detect(string text)
    {
        string[] entries = ["API|응용 프로그램 인터페이스", "cache|캐시", "framework|프레임워크", "dependency|의존성", "package|패키지", "component|컴포넌트", "function|함수", "variable|변수", "server|서버", "client|클라이언트", "build|빌드", "deploy|배포", "async|비동기", "database|데이터베이스", "repository|저장소", "runtime|실행 환경", "exception|예외", "callback|콜백"];
        return entries.Select(x => x.Split('|')).Where(x => Regex.IsMatch(text, @"\b" + Regex.Escape(x[0]) + @"\b", RegexOptions.IgnoreCase)).Take(12).Select(x => new DetectedTerm(x[0], x[1])).ToArray();
    }
    /// <summary>
    /// Retires the client without racing an in-flight request. The gate is held for the whole of
    /// TranslateAsync, so waiting on it guarantees the old HttpClient is idle before it is disposed;
    /// disposing right after Cancel() could otherwise tear the client out from under a running call
    /// and surface as a generic "translation failed" message.
    /// </summary>
    public async Task RetireAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { client.Dispose(); }
        finally { gate.Release(); }
    }
    public void Dispose() { client.Dispose(); }
}
