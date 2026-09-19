using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Hoonsoo;

public sealed class ArBlock
{
    public string OriginalText { get; set; }
    public string CleanText { get; set; }
    public string TranslatedText { get; set; } = "";
    public Rectangle Bounds { get; set; }
    public IReadOnlyList<OcrLineInfo> Lines { get; set; }
    public ArBlock(string originalText, Rectangle bounds, IReadOnlyList<OcrLineInfo> lines)
        : this(originalText, originalText, bounds, lines)
    {
    }

    public ArBlock(string originalText, string cleanText, Rectangle bounds, IReadOnlyList<OcrLineInfo> lines)
    {
        OriginalText = originalText;
        CleanText = cleanText;
        Bounds = bounds;
        Lines = lines;
    }
}

public static class ArTranslationService
{
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> CacheOrder = new();
    private static readonly object CacheLock = new();
    private const int CacheCapacity = 2000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static HttpClient? injectedHttp;

    /// <summary>
    /// Replaces the outbound transport. Tests use it to exercise the batch, split and fallback paths
    /// without depending on a third-party endpoint that rate-limits by IP; the app never sets it.
    /// </summary>
    internal static HttpMessageHandler? Transport
    {
        set => injectedHttp = value is null ? null : new HttpClient(value) { Timeout = TimeSpan.FromSeconds(12) };
    }

    private static HttpClient Client => injectedHttp ?? Http;
    private static readonly SemaphoreSlim HttpGate = new(MaxHttpConcurrency, MaxHttpConcurrency);

    // Every outbound request (batch or single) passes through HttpGate, so the whole
    // service can never exceed this many concurrent HTTP calls.
    private const int MaxHttpConcurrency = 4;

    private const string Delimiter = "\n---\n";
    private static readonly int DelimiterEscapedLength = Uri.EscapeDataString(Delimiter).Length;

    // Cap both batch item count and the escaped query length so the request URL
    // stays within a safe size regardless of how short each block is. A source
    // longer than this limit is split into budget-bounded spans rather than
    // dropped (see SplitForTranslation).
    private const int MaxBatchItems = 20;
    private const int MaxEscapedQueryLength = 1800;
    private const string Endpoint = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=ko&dt=t&q=";

    private static readonly HashSet<string> CodeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "for", "let", "var", "const", "int", "str", "string", "bool", "def", "fn",
        "id", "in", "to", "is", "as", "true", "false", "null", "undefined", "return",
        "public", "private", "protected", "static", "void", "class", "new", "this", "self",
        "import", "export", "from", "while", "break", "continue", "switch", "case", "default",
        "else", "try", "catch", "finally", "throw", "async", "await", "yield", "sizeof", "typeof",
        "ok", "err", "error", "nil", "none", "val", "pkg", "mod", "use", "mut"
    };

    public static string StripLeadingSymbols(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string s = text.Trim();

        // Strip leading Markdown headers: #, ##, ###, ####, etc.
        s = Regex.Replace(s, @"^#{1,6}\s*", "");

        // Strip leading blockquotes and list bullets: >, -, *, +, •
        s = Regex.Replace(s, @"^[>\-*+•\u2022]\s*", "");

        // Strip leading code comment markers: //, /*, *, #, <!--
        s = Regex.Replace(s, @"^(\/\/|\/\*|\*|<!--|#)\s*", "");

        // Strip leading numbered list patterns: "1.", "2)", "(1)", "1.2.3."
        s = Regex.Replace(s, @"^(\d+[\.\)]|\(\d+\)|\d+(\.\d+)+\.?)\s*", "");

        // Strip backticks or quotes wrapper
        s = s.Trim('`', '"', '\'', ' ', '\t');

        return s.Trim();
    }

    public static bool IsMeaningfulForTranslation(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        string clean = StripLeadingSymbols(text);
        if (clean.Length < 3) return false;

        // Must contain at least one English letter
        if (!Regex.IsMatch(clean, @"[a-zA-Z]")) return false;

        // Reject if it's purely numbers, punctuation, symbols, or whitespace
        // e.g. "1.2.3", "###", "---", "=== ", "100%", "2026-09-11", "0x4F"
        if (Regex.IsMatch(clean, @"^[\d\s\.,\-_/\\:;%#\+=\(\)\[\]{}<>&\|\^~`!@\$?*'""]+$"))
            return false;

        // Must have at least 3 alphabet letters across the string
        int letterCount = clean.Count(char.IsLetter);
        if (letterCount < 3) return false;

        var words = clean.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return false;

        // If it's a single word:
        if (words.Length == 1)
        {
            string single = words[0].Trim();
            // Single words shorter than 4 characters are almost always variables or abbreviations (e.g. "id", "px", "ms", "app")
            if (single.Length < 4) return false;
            // Reject standard programming keywords
            if (CodeKeywords.Contains(single)) return false;
            // Reject common file extensions / versions
            if (Regex.IsMatch(single, @"^v?\d+(\.\d+)*$", RegexOptions.IgnoreCase)) return false;
            if (Regex.IsMatch(single, @"^\.[a-zA-Z0-9]+$")) return false;
        }

        return true;
    }

    public static List<ArBlock> MergeLinesToBlocks(IReadOnlyList<OcrLineInfo> lines)
    {
        // Filter lines using smart relevance gate
        var validLines = lines
            .Where(l => IsMeaningfulForTranslation(l.Text))
            .OrderBy(l => l.BoundingBox.Top)
            .ThenBy(l => l.BoundingBox.Left)
            .ToList();

        var blocks = new List<ArBlock>();
        if (validLines.Count == 0) return blocks;

        var currentLines = new List<OcrLineInfo> { validLines[0] };

        for (int i = 1; i < validLines.Count; i++)
        {
            var prev = currentLines[^1];
            var curr = validLines[i];

            int avgHeight = Math.Max(12, (prev.BoundingBox.Height + curr.BoundingBox.Height) / 2);
            int verticalGap = curr.BoundingBox.Top - prev.BoundingBox.Bottom;
            bool isVerticallyClose = verticalGap >= -avgHeight && verticalGap <= (int)(avgHeight * 1.3);

            bool isHorizontallyAligned = Math.Abs(curr.BoundingBox.Left - prev.BoundingBox.Left) < 80 ||
                (curr.BoundingBox.Left >= prev.BoundingBox.Left && curr.BoundingBox.Left <= prev.BoundingBox.Right);

            if (isVerticallyClose && isHorizontallyAligned)
            {
                currentLines.Add(curr);
            }
            else
            {
                var block = CreateBlock(currentLines);
                if (IsMeaningfulForTranslation(block.CleanText)) blocks.Add(block);
                currentLines = new List<OcrLineInfo> { curr };
            }
        }

        if (currentLines.Count > 0)
        {
            var block = CreateBlock(currentLines);
            if (IsMeaningfulForTranslation(block.CleanText)) blocks.Add(block);
        }

        return blocks;
    }

    private static ArBlock CreateBlock(List<OcrLineInfo> lines)
    {
        string rawText = string.Join(" ", lines.Select(l => l.Text.Trim()));
        string cleanText = string.Join(" ", lines.Select(l => StripLeadingSymbols(l.Text)).Where(s => s.Length > 0));

        int minX = lines.Min(l => l.BoundingBox.Left);
        int minY = lines.Min(l => l.BoundingBox.Top);
        int maxX = lines.Max(l => l.BoundingBox.Right);
        int maxY = lines.Max(l => l.BoundingBox.Bottom);

        return new ArBlock(
            rawText,
            string.IsNullOrWhiteSpace(cleanText) ? rawText : cleanText,
            new Rectangle(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY)),
            lines
        );
    }

    public static async Task TranslateBlocksAsync(List<ArBlock> blocks, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (blocks.Count == 0) return;

        // Group blocks by identical cleaned text so one successful translation is
        // shared across every duplicate instead of being requested again.
        var groups = new Dictionary<string, List<ArBlock>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.CleanText)) continue;
            string query = block.CleanText;
            string? cached = TryGetCached(query);
            if (cached is not null)
            {
                block.TranslatedText = cached;
                continue;
            }

            if (!groups.TryGetValue(query, out var list))
            {
                list = new List<ArBlock>();
                groups.Add(query, list);
                order.Add(query);
            }
            list.Add(block);
        }

        if (order.Count > 0)
        {
            token.ThrowIfCancellationRequested();

            var batches = new List<List<string>>();
            var individual = new List<string>();
            BuildBatches(order, batches, individual);

            var results = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            var failed = new ConcurrentQueue<string>();

            // Phase 1: batch requests. Each batch only reaches HTTP through HttpGate,
            // so parallelism is bounded no matter how many batches are queued.
            var batchTasks = new List<Task>(batches.Count);
            foreach (var batch in batches)
            {
                batchTasks.Add(TranslateBatchAsync(batch, results, failed, token));
            }
            if (batchTasks.Count > 0) await Task.WhenAll(batchTasks).ConfigureAwait(false);

            // Phase 2: per-item fallback for delimiter-collision sources, oversized
            // queries, and batches whose positional mapping could not be trusted.
            var fallback = new List<string>(individual.Count + failed.Count);
            fallback.AddRange(individual);
            foreach (var query in failed) fallback.Add(query);

            if (fallback.Count > 0)
            {
                await RunBoundedAsync(fallback, MaxHttpConcurrency, async query =>
                {
                    if (results.ContainsKey(query)) return;
                    string? translated = await TranslateSingleAsync(query, token).ConfigureAwait(false);
                    if (translated is not null) results[query] = translated;
                }, token).ConfigureAwait(false);
            }

            foreach (var query in order)
            {
                if (!results.TryGetValue(query, out var translated)) continue;
                foreach (var block in groups[query]) block.TranslatedText = translated;
            }
        }

        // Post-translation validation gate:
        // Suppress translations that have no Korean characters (identity/untranslated)
        // or where the translated text is identical to the original English text
        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.TranslatedText)) continue;

            bool hasKorean = Regex.IsMatch(block.TranslatedText, @"[\uac00-\ud7a3]");
            bool isIdentity = string.Equals(block.CleanText.Trim(), block.TranslatedText.Trim(), StringComparison.OrdinalIgnoreCase);

            if (!hasKorean || isIdentity)
            {
                block.TranslatedText = ""; // Suppress rendering this chip
            }
        }
    }

    // Splits unique queries into delimiter-joined batches, keeping the escaped query
    // size bounded. Sources that already contain the separator are never batched
    // because the positional split could not be trusted.
    private static void BuildBatches(List<string> queries, List<List<string>> batches, List<string> individual)
    {
        var current = new List<string>();
        int currentLength = 0;

        foreach (var query in queries)
        {
            if (!IsBatchSafe(query, out int escapedLength))
            {
                individual.Add(query);
                continue;
            }

            if (current.Count > 0 &&
                (current.Count >= MaxBatchItems ||
                 currentLength + DelimiterEscapedLength + escapedLength > MaxEscapedQueryLength))
            {
                batches.Add(current);
                current = new List<string>();
                currentLength = 0;
            }

            current.Add(query);
            currentLength += escapedLength + DelimiterEscapedLength;
        }

        if (current.Count > 0) batches.Add(current);
    }

    private static bool IsBatchSafe(string query, out int escapedLength)
    {
        escapedLength = 0;
        if (string.IsNullOrEmpty(query)) return false;
        if (query.Contains("---", StringComparison.Ordinal)) return false;

        try
        {
            escapedLength = Uri.EscapeDataString(query).Length;
        }
        catch
        {
            return false;
        }

        return escapedLength <= MaxEscapedQueryLength;
    }

    private static async Task TranslateBatchAsync(
        List<string> queries,
        ConcurrentDictionary<string, string> results,
        ConcurrentQueue<string> failed,
        CancellationToken token)
    {
        string combined = string.Join(Delimiter, queries);
        string escaped = Uri.EscapeDataString(combined);
        if (escaped.Length > MaxEscapedQueryLength)
        {
            foreach (var query in queries) failed.Enqueue(query);
            return;
        }

        string? translatedFull = await GetTranslationRawAsync(escaped, token).ConfigureAwait(false);
        if (translatedFull is null)
        {
            foreach (var query in queries) failed.Enqueue(query);
            return;
        }

        // The separator split is trusted only when it yields exactly one part per
        // source block. That count is a structural sanity check for a missing or
        // extra separator; it cannot tell whether the parts were reordered, so the
        // mapping is positional and order is never inferred from the count. Any
        // mismatch falls back to per-item requests.
        string[] parts = translatedFull.Split(new[] { "\n---\n", "\n--- \n", "---" }, StringSplitOptions.TrimEntries);
        if (parts.Length != queries.Count)
        {
            foreach (var query in queries) failed.Enqueue(query);
            return;
        }

        for (int i = 0; i < queries.Count; i++)
        {
            if (IsValidTranslation(queries[i], parts[i], out string normalized))
            {
                results[queries[i]] = normalized;
                CacheTranslation(queries[i], normalized);
            }
            else
            {
                failed.Enqueue(queries[i]);
            }
        }
    }

    private static async Task<string?> TranslateSingleAsync(string query, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        if (!TryEscape(query, out string escaped)) return null;

        if (escaped.Length <= MaxEscapedQueryLength)
        {
            string? translated = await GetTranslationRawAsync(escaped, token).ConfigureAwait(false);
            return IsValidTranslation(query, translated, out string normalized) ? normalized : null;
        }

        // A single source larger than the request budget used to be dropped here.
        // Instead, split it into size-bounded natural spans, translate the spans,
        // and rejoin the successful pieces so long valid input still renders.
        return await TranslateSplitAsync(query, token).ConfigureAwait(false);
    }

    // Translates one source that does not fit a single request by splitting it
    // into contiguous, budget-bounded spans and translating each span. Every span
    // request still passes through HttpGate, so the whole service never exceeds
    // MaxHttpConcurrency concurrent outbound calls.
    private static async Task<string?> TranslateSplitAsync(string query, CancellationToken token)
    {
        var fragments = SplitForTranslation(query, MaxEscapedQueryLength);
        if (fragments.Count == 0) return null;

        var pieces = new string?[fragments.Count];
        var tasks = new Task[fragments.Count];
        for (int i = 0; i < fragments.Count; i++)
        {
            int index = i;
            tasks[index] = Task.Run(async () =>
            {
                pieces[index] = await TranslateFragmentAsync(fragments[index], token).ConfigureAwait(false);
            }, token);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Rejoin in source order, but never cache a partially failed translation.
        var builder = new StringBuilder(query.Length);
        bool hasTranslatedSpan = false;
        for (int i = 0; i < fragments.Count; i++)
        {
            string fragment = fragments[i];
            if (pieces[i] is null && !string.IsNullOrWhiteSpace(fragment)) return null;
            int start = 0;
            while (start < fragment.Length && char.IsWhiteSpace(fragment[start])) start++;
            int end = fragment.Length;
            while (end > start && char.IsWhiteSpace(fragment[end - 1])) end--;

            builder.Append(fragment, 0, start);
            if (pieces[i] is { Length: > 0 } piece)
            {
                builder.Append(piece);
                hasTranslatedSpan = true;
            }
            else
            {
                builder.Append(fragment, start, end - start);
            }
            builder.Append(fragment, end, fragment.Length - end);
        }

        if (!hasTranslatedSpan) return null;

        string combined = builder.ToString().Trim();
        return IsValidTranslation(query, combined, out string normalized) ? normalized : null;
    }

    private static async Task<string?> TranslateFragmentAsync(string fragment, CancellationToken token)
    {
        string core = fragment.Trim();
        if (core.Length == 0) return null;
        if (!TryEscape(core, out string escaped)) return null;
        if (escaped.Length > MaxEscapedQueryLength) return null;

        string? translated = await GetTranslationRawAsync(escaped, token).ConfigureAwait(false);
        return IsValidTranslation(core, translated, out string normalized) ? normalized : null;
    }

    // Splits a source into contiguous spans whose escaped URL length stays within
    // the request budget. Cuts prefer sentence ends, then whitespace, then a hard
    // boundary inside an oversized token. Concatenating the spans reproduces the
    // source exactly, so splitting never drops, duplicates, or reorders text.
    private static List<string> SplitForTranslation(string text, int maxEscapedLength)
    {
        var fragments = new List<string>();
        if (string.IsNullOrEmpty(text)) return fragments;

        int index = 0;
        while (index < text.Length)
        {
            int end = FindSpanEnd(text, index, maxEscapedLength);
            if (end <= index) end = NextTextElementEnd(text, index);
            fragments.Add(text.Substring(index, end - index));
            index = end;
        }

        return fragments;
    }

    private static int FindSpanEnd(string text, int start, int maxEscapedLength)
    {
        int position = start;
        int escaped = 0;
        int lastSentenceCut = -1;
        int lastWhitespaceCut = -1;

        while (position < text.Length)
        {
            int codePoint = CodePointAt(text, position);
            int elementEscaped = EscapedCodePointLength(codePoint);
            if (escaped + elementEscaped > maxEscapedLength) break;

            escaped += elementEscaped;
            position = NextTextElementEnd(text, position);

            if (IsSentenceBoundary(codePoint))
            {
                lastSentenceCut = position;
            }
            else if (IsWhitespaceCodePoint(codePoint))
            {
                lastWhitespaceCut = position;
            }
        }

        if (position >= text.Length) return text.Length;
        if (lastSentenceCut > start) return lastSentenceCut;
        if (lastWhitespaceCut > start) return lastWhitespaceCut;
        return position; // No natural boundary fit; cut hard so progress is guaranteed.
    }

    private static bool IsSentenceBoundary(int codePoint)
    {
        switch (codePoint)
        {
            case '\n':
            case '.':
            case '!':
            case '?':
            case ';':
            case '\u3002': // 。
            case '\uFF01': // ！
            case '\uFF1F': // ？
            case '\u2026': // …
                return true;
            default:
                return false;
        }
    }

    private static bool IsWhitespaceCodePoint(int codePoint)
    {
        return codePoint <= char.MaxValue && char.IsWhiteSpace((char)codePoint);
    }

    private static int NextTextElementEnd(string text, int index)
    {
        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            return index + 2;
        }
        return index + 1;
    }

    private static int CodePointAt(string text, int index)
    {
        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            return char.ConvertToUtf32(text[index], text[index + 1]);
        }
        return text[index];
    }

    // Mirrors Uri.EscapeDataString for a single scalar: unreserved ASCII survives
    // unchanged while every other scalar costs 3 characters per UTF-8 byte. The
    // result is always >= the real escaped length, so budget checks never overflow.
    private static int EscapedCodePointLength(int codePoint)
    {
        if (codePoint < 0x80)
        {
            bool unreserved =
                (codePoint >= 'A' && codePoint <= 'Z') ||
                (codePoint >= 'a' && codePoint <= 'z') ||
                (codePoint >= '0' && codePoint <= '9') ||
                codePoint == '-' || codePoint == '.' || codePoint == '_' || codePoint == '~';
            return unreserved ? 1 : 3;
        }
        if (codePoint <= 0x7FF) return 6;
        if (codePoint <= 0xFFFF) return 9;
        return 12;
    }

    private static bool TryEscape(string text, out string escaped)
    {
        try
        {
            escaped = Uri.EscapeDataString(text);
            return true;
        }
        catch
        {
            escaped = "";
            return false;
        }
    }

    private static async Task<string?> GetTranslationRawAsync(string escapedQuery, CancellationToken token)
    {
        if (injectedHttp is null && FreeTranslationProvider.IsGoogleRateLimited) return null;
        string url = Endpoint + escapedQuery;

        await HttpGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            using var response = await Client.GetAsync(url, token).ConfigureAwait(false);
            if (injectedHttp is null && response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                FreeTranslationProvider.IsGoogleRateLimited = true;
                return null;
            }
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;

            var segments = doc.RootElement[0];
            if (segments.ValueKind != JsonValueKind.Array) return null;

            return string.Concat(segments.EnumerateArray().Select(x => x[0].GetString()));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Caller cancellation must propagate; it never becomes a fallback result.
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            HttpGate.Release();
        }
    }

    // Runs work items on a fixed number of workers. Cancellation stops workers from
    // pulling further items, so queued work is abandoned rather than started.
    private static async Task RunBoundedAsync(
        IReadOnlyList<string> items,
        int maxConcurrency,
        Func<string, Task> body,
        CancellationToken token)
    {
        if (items.Count == 0) return;

        object sync = new();
        int next = -1;
        int workers = Math.Min(maxConcurrency, items.Count);
        var tasks = new Task[workers];

        for (int w = 0; w < workers; w++)
        {
            tasks[w] = Task.Run(async () =>
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    int index;
                    lock (sync)
                    {
                        if (next + 1 >= items.Count) return;
                        index = ++next;
                    }

                    await body(items[index]).ConfigureAwait(false);
                }
            }, token);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static bool IsValidTranslation(string source, string? translated, out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(translated) ? "" : translated.Trim();
        if (normalized.Length == 0) return false;

        // Reject identity/English passthrough output; only Korean is worth rendering.
        if (!Regex.IsMatch(normalized, @"[\uac00-\ud7a3]")) return false;
        if (string.Equals((source ?? "").Trim(), normalized, StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    private static string? TryGetCached(string query)
    {
        lock (CacheLock)
        {
            return Cache.TryGetValue(query, out var found) ? found : null;
        }
    }

    private static void CacheTranslation(string query, string translated)
    {
        if (!IsValidTranslation(query, translated, out string normalized)) return;

        lock (CacheLock)
        {
            if (Cache.ContainsKey(query))
            {
                Cache[query] = normalized;
                return;
            }

            // Incremental eviction: drop the oldest insertions one at a time instead
            // of clearing the whole cache, keeping the bound at CacheCapacity entries.
            while (Cache.Count >= CacheCapacity && CacheOrder.Count > 0)
            {
                Cache.Remove(CacheOrder.Dequeue());
            }

            Cache[query] = normalized;
            CacheOrder.Enqueue(query);
        }
    }
}
