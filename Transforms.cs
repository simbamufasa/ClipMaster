using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClipMaster;

/// <summary>A named, on-demand text transform. Apply may throw on invalid input.</summary>
public sealed record Transform(string Id, string Label, string Group, Func<string, string> Apply);

public static class Transforms
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static IReadOnlyList<Transform> All { get; } =
    [
        new("json-pretty",    "Pretty-print",         "JSON",       JsonPretty),
        new("json-minify",    "Minify",               "JSON",       JsonMinify),
        new("json-escape",    "Escape",               "JSON",       s => JsonSerializer.Serialize(s)),
        new("json-unescape",  "Unescape",             "JSON",       JsonUnescape),
        new("b64-encode",     "Base64 encode",        "Encode",     s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))),
        new("b64-decode",     "Base64 decode",        "Encode",     s => Encoding.UTF8.GetString(Convert.FromBase64String(s.Trim()))),
        new("url-encode",     "URL encode",           "Encode",     Uri.EscapeDataString),
        new("url-decode",     "URL decode",           "Encode",     Uri.UnescapeDataString),
        new("lower",          "lowercase",            "Case",       s => s.ToLowerInvariant()),
        new("upper",          "UPPERCASE",            "Case",       s => s.ToUpperInvariant()),
        new("title",          "Title Case",           "Case",       TitleCase),
        new("trim-lines",     "Trim lines",           "Whitespace", TrimLines),
        new("collapse-blank", "Collapse blank lines", "Whitespace", CollapseBlank),
    ];

    private static string JsonPretty(string s)
    {
        using var doc = JsonDocument.Parse(s);
        return JsonSerializer.Serialize(doc.RootElement, Pretty);
    }

    private static string JsonMinify(string s)
    {
        using var doc = JsonDocument.Parse(s);
        return JsonSerializer.Serialize(doc.RootElement);
    }

    private static string JsonUnescape(string s) =>
        JsonSerializer.Deserialize<string>(s) ?? throw new FormatException("Not a JSON string literal");

    private static string TitleCase(string s) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

    private static string TrimLines(string s) =>
        Regex.Replace(s, @"[ \t]+(?=\r?\n)|[ \t]+$", "", RegexOptions.None, RegexTimeout);

    private static string CollapseBlank(string s) =>
        Regex.Replace(s, @"(\r?\n){3,}", "\n\n", RegexOptions.None, RegexTimeout);
}
