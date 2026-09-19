using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Hoonsoo;
public sealed class CodeProtection
{
    // Longest syntactic units precede identifiers; every occurrence has a unique token.
    private static readonly Regex Pattern = new("""
        ```[\s\S]*?```|`[^`\r\n]+`|(?m:^\s*(?:(?:npm|npx|git|pip|pnpm|yarn|dotnet|docker|node|curl|sudo)\s+[^\r\n]+|(?:const|let|var|import|export|public|private|class|def|function|return)\s+[^\r\n]*[;{}=][^\r\n]*))|\b(?:npm|npx|git|pip|pnpm|yarn|dotnet|docker)\s+(?:install|run\s+[\w:.-]+|pull|push|status|commit|build|test|restore|add|remove|checkout)(?:\s+--?[\w=-]+)*|https?://[^\s<>"`]+|(?:[A-Za-z]:\\|\\\\)[^\s<>"`]+|(?<!\w)(?:\.?\.?/|~/)[\w./@~-]+|\b[\w@.-]+(?:/[\w@.~-]+)+/?|\blocalhost(?::\d+)?(?:/[^\s]*)?|(?<!\w)--?[A-Za-z][\w-]*(?:=[\w.-]+)?|\b[\w.-]+\.(?:json|tsx?|jsx?|cs|csproj|sln|py|md|txt|yaml|yml|toml|exe|dll|config|env|html|css|sh|ps1|zip)\b|\b[A-Za-z_$][\w.$]*(?:\([^()\r\n]*\))|\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+\b|\$\{?\w+\}?|%\w+%|\b[a-z]+(?:[A-Z][a-zA-Z0-9]*)+\b|\b\w+_\w+\b|\b(?:Node\.js|React|GitHub|JavaScript|TypeScript|Python|Windows|Linux|Docker|Kubernetes|PostgreSQL|Next\.js|Vue|Angular|NuGet|WPF|ASP\.NET)\b|\.NET\b
        """, RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace, TimeSpan.FromSeconds(1));
    public string Text { get; }
    public IReadOnlyDictionary<string, string> Tokens => tokens;
    private readonly Dictionary<string, string> tokens = new();
    private readonly string prefix;
    public CodeProtection(string original, bool enabled = true)
    {
        do { prefix = "__DL" + Guid.NewGuid().ToString("N")[..10] + "_"; } while (original.Contains(prefix, StringComparison.Ordinal));
        Text = enabled ? Pattern.Replace(original, m => { var key = prefix + tokens.Count + "__"; tokens.Add(key, m.Value); return key; }) : original;
    }
    public string Restore(string translated)
    {
        foreach (var (key, _) in tokens)
            if (Regex.Matches(translated, Regex.Escape(key)).Count != 1) throw new FormatException("코드 보호 토큰이 누락되거나 중복되었습니다. 원문을 확인하세요.");
        var result = translated;
        foreach (var (key, value) in tokens) result = result.Replace(key, value, StringComparison.Ordinal);
        if (result.Contains(prefix, StringComparison.Ordinal)) throw new FormatException("알 수 없는 코드 보호 토큰입니다.");
        return result;
    }
}
