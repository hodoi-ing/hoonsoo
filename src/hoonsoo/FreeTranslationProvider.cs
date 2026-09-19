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
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, TranslationResult> cache = new();
    private readonly Queue<string> cacheOrder = new();
    public FreeTranslationProvider(HttpMessageHandler? handler = null)
    {
        client = handler is null ? new() : new(handler);
        client.Timeout = TimeSpan.FromSeconds(20);
    }
    public async Task<TranslationResult> TranslateAsync(string text, string method, bool terms, bool protect, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (text.Length is 0 or > 4000) return new(text, "", [], method, "번역할 텍스트 길이가 올바르지 않습니다.");
            var id = $"{terms}:{protect}:{text}";
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
        catch { return new(text, "", [], method, "무료 온라인 번역에 연결하지 못했습니다. 인터넷 연결을 확인하고 다시 시도하세요."); }
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
            if (!Regex.IsMatch(span, "[a-zA-Z]")) { results[i] = span; continue; }
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
    private async Task<string> TranslateSpan(string text, CancellationToken token)
    {
        if (!Regex.IsMatch(text, "[a-zA-Z]")) return text;
        token.ThrowIfCancellationRequested();
        string? result = null;
        if (!IsGoogleRateLimited)
        {
            try
            {
                using var response = await client.GetAsync("https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=ko&dt=t&q=" + Uri.EscapeDataString(text.Trim()), token);
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
            using var response = await client.GetAsync("https://api.mymemory.translated.net/get?langpair=en|ko&q=" + Uri.EscapeDataString(text.Trim()), token);
            response.EnsureSuccessStatusCode();
            result = ParseMyMemory(await response.Content.ReadAsStringAsync(token));
        }

        if (string.IsNullOrWhiteSpace(result)) throw new FormatException();
        return text[..(text.Length - text.TrimStart().Length)] + result + text[text.TrimEnd().Length..];
    }
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
