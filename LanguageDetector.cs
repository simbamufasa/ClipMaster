using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClipMaster;

/// <summary>Heuristic, best-effort language detection for display only. Never throws.</summary>
public static class LanguageDetector
{
    private const int MaxScan      = 4096;       // detect on a cheap prefix
    private const int MaxJsonParse = 1_000_000;  // only structurally validate JSON below this size
    private static readonly TimeSpan T = TimeSpan.FromSeconds(1);
    private const RegexOptions M  = RegexOptions.Multiline;
    private const RegexOptions IC = RegexOptions.IgnoreCase;

    public static string Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Length > MaxScan ? text[..MaxScan] : text;
        var t = s.TrimStart();

        try
        {
            if (t.StartsWith('{') || t.StartsWith('['))
            {
                if (text.Length > MaxJsonParse) return "json";   // too big to validate; trust structure
                if (TryJson(text)) return "json";
            }

            if (t.StartsWith('<') && Regex.IsMatch(t, @"<[a-zA-Z!/]", RegexOptions.None, T)) return "xml";
            if (Regex.IsMatch(s, @"^\s*FROM\s+\S+", M | IC, T)) return "dockerfile";
            if (s.Contains("<?php")) return "php";
            if (Regex.IsMatch(s, @"\b(SELECT|INSERT\s+INTO|UPDATE|DELETE\s+FROM|CREATE\s+(TABLE|DATABASE)|ALTER\s+TABLE|DROP\s+TABLE)\b", IC, T)) return "sql";
            if (Regex.IsMatch(s, @"\bpackage\s+\w+", RegexOptions.None, T) && Regex.IsMatch(s, @"\b(func|import)\b", RegexOptions.None, T)) return "go";
            if (Regex.IsMatch(s, @"\bfn\s+\w+\s*\(", RegexOptions.None, T) && Regex.IsMatch(s, @"(let\s+mut\b|->|::)", RegexOptions.None, T)) return "rust";
            if (Regex.IsMatch(s, @"^\s*(def|class|import|from)\s", M, T) && !Regex.IsMatch(s, @"[{};]\s*$", M, T)) return "python";
            if (Regex.IsMatch(s, @"\b(const|let|function)\b", RegexOptions.None, T) || Regex.IsMatch(s, @"=>", RegexOptions.None, T) || Regex.IsMatch(s, @"\bimport\b.*\bfrom\b", RegexOptions.None, T)) return "ts";
            if (Regex.IsMatch(s, @"^---\s*$", M, T) || Regex.Matches(s, @"^\s*[\w.-]+:\s", M, T).Count >= 2) return "yaml";
            if (Regex.IsMatch(s, @"[.#]?[\w-]+\s*\{[^}]*:[^}]*;", RegexOptions.Singleline, T)) return "css";
            if (t.StartsWith("#!") || Regex.IsMatch(s, @"^\s*(sudo|apt|git|docker|npm|pnpm|cd|export|echo)\b", M, T)) return "shell";
        }
        catch (RegexMatchTimeoutException) { return ""; }

        return "";
    }

    private static bool TryJson(string text)
    {
        try { using var _ = JsonDocument.Parse(text); return true; }
        catch { return false; }
    }
}
