# Developer Power-Paste Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add on-demand text transforms, heuristic language detection, and zero-dependency syntax highlighting (in card previews and a new full-clip detail view) to ClipMaster.

**Architecture:** Three new pure-logic static classes (`Transforms`, `LanguageDetector`, `SyntaxHighlighter`) are unit-tested in isolation. Detection runs in `DataService.AddClip` and is persisted on `ClipEntry.Language`. The WPF layer (card preview, an in-window detail overlay, context-menu transforms, a settings toggle) consumes those classes; UI is verified by build + manual check, consistent with this project.

**Tech Stack:** C# / .NET 8 / WPF, `System.Text.Json` (already referenced), xUnit. No new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-05-26-developer-power-paste-design.md`

**Conventions for every task:**
- Build the app: `dotnet build ClipMaster.csproj --nologo`
- Run a test subset: `dotnet test ClipMaster.Tests/ClipMaster.Tests.csproj --filter "FullyQualifiedName~<Class>" --nologo`
- Commit messages: plain Conventional Commits, **no `Co-Authored-By` line** (repo is going public).
- Branch: work on `feat/developer-power-paste` (already created and holding the spec commit).

---

## File Structure

| File | Responsibility |
|---|---|
| `Models.cs` (modify) | `ClipEntry.Language`, `AppSettings.HighlightCode` |
| `Transforms.cs` (new) | Registry of on-demand transform functions (pure) |
| `LanguageDetector.cs` (new) | Heuristic `text → language id` (pure) |
| `SyntaxHighlighter.cs` (new) | `Tokenize` (pure) + WPF `AppendTo` renderer |
| `DataService.cs` (modify) | Set `ClipEntry.Language` in `AddClip` |
| `Themes/Dark.xaml` (modify) | Code-token brushes |
| `MainWindow.xaml` (modify) | Detail-view overlay |
| `MainWindow.xaml.cs` (modify) | Highlighted previews, badge, Expand, detail view, transform menus, settings toggle, Esc/Ctrl+Enter |
| `ClipMaster.Tests/*` (new) | Tests for transforms, detector, highlighter, detection wiring |

---

## Task 1: Data model fields

**Files:**
- Modify: `Models.cs`

- [ ] **Step 1: Add `Language` to `ClipEntry`**

In `Models.cs`, inside `class ClipEntry`, after the `ImageBytes` property line, add:

```csharp
    [JsonPropertyName("language")]     public string       Language     { get; set; } = "";
```

- [ ] **Step 2: Add `HighlightCode` to `AppSettings`**

In `Models.cs`, inside `class AppSettings`, after the `MaxImageHistory` property line, add:

```csharp
    [JsonPropertyName("highlightCode")] public bool   HighlightCode   { get; set; } = true;
```

- [ ] **Step 3: Build**

Run: `dotnet build ClipMaster.csproj --nologo`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add Models.cs
git commit -m "feat(model): add ClipEntry.Language and AppSettings.HighlightCode"
```

---

## Task 2: Transforms registry (TDD)

**Files:**
- Create: `Transforms.cs`
- Create: `ClipMaster.Tests/TransformsTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `ClipMaster.Tests/TransformsTests.cs`:

```csharp
using System.Linq;
using ClipMaster;
using Xunit;

namespace ClipMaster.Tests;

public class TransformsTests
{
    private static string Apply(string id, string input) =>
        Transforms.All.First(t => t.Id == id).Apply(input);

    [Fact]
    public void Json_PrettyThenMinify_RoundTrips()
    {
        const string min = "{\"b\":2,\"a\":1}";
        var pretty = Apply("json-pretty", min);
        Assert.Contains("\n", pretty);                 // indented
        var back = Apply("json-minify", pretty);
        Assert.DoesNotContain("\n", back);
        Assert.Contains("\"a\":1", back);
        Assert.Contains("\"b\":2", back);
    }

    [Fact]
    public void Json_Pretty_OnInvalid_Throws()
    {
        Assert.ThrowsAny<System.Exception>(() => Apply("json-pretty", "not json"));
    }

    [Fact]
    public void Base64_EncodeDecode_RoundTrips()
    {
        var enc = Apply("b64-encode", "hello world");
        Assert.Equal("aGVsbG8gd29ybGQ=", enc);
        Assert.Equal("hello world", Apply("b64-decode", enc));
    }

    [Fact]
    public void Base64_Decode_OnInvalid_Throws()
    {
        Assert.ThrowsAny<System.Exception>(() => Apply("b64-decode", "!!!not base64!!!"));
    }

    [Fact]
    public void Url_EncodeDecode_RoundTrips()
    {
        var enc = Apply("url-encode", "a b&c=d");
        Assert.DoesNotContain(" ", enc);
        Assert.Equal("a b&c=d", Apply("url-decode", enc));
    }

    [Theory]
    [InlineData("lower", "Hello WORLD", "hello world")]
    [InlineData("upper", "Hello world", "HELLO WORLD")]
    [InlineData("title", "hello WORLD", "Hello World")]
    public void Case_Transforms(string id, string input, string expected)
        => Assert.Equal(expected, Apply(id, input));

    [Fact]
    public void TrimLines_StripsTrailingWhitespace()
        => Assert.Equal("a\nb", Apply("trim-lines", "a   \nb\t"));

    [Fact]
    public void CollapseBlank_ReducesRunsOfBlankLines()
        => Assert.Equal("a\n\nb", Apply("collapse-blank", "a\n\n\n\nb"));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ClipMaster.Tests/ClipMaster.Tests.csproj --filter "FullyQualifiedName~TransformsTests" --nologo`
Expected: FAIL — `Transforms` does not exist (compile error).

- [ ] **Step 3: Implement `Transforms.cs`**

Create `Transforms.cs`:

```csharp
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
        string.Join('\n', s.Split('\n').Select(line => line.TrimEnd()));

    private static string CollapseBlank(string s) =>
        Regex.Replace(s, @"(\r?\n){3,}", "\n\n", RegexOptions.None, RegexTimeout);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ClipMaster.Tests/ClipMaster.Tests.csproj --filter "FullyQualifiedName~TransformsTests" --nologo`
Expected: PASS — all TransformsTests green.

- [ ] **Step 5: Commit**

```bash
git add Transforms.cs ClipMaster.Tests/TransformsTests.cs
git commit -m "feat(transforms): add on-demand transform registry with tests"
```

---

## Task 3: Language detector (TDD)

**Files:**
- Create: `LanguageDetector.cs`
- Create: `ClipMaster.Tests/LanguageDetectorTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `ClipMaster.Tests/LanguageDetectorTests.cs`:

```csharp
using ClipMaster;
using Xunit;

namespace ClipMaster.Tests;

public class LanguageDetectorTests
{
    [Theory]
    [InlineData("{\"a\": 1, \"b\": [2,3]}", "json")]
    [InlineData("[1, 2, 3]", "json")]
    [InlineData("SELECT id, name FROM users WHERE id = 1;", "sql")]
    [InlineData("def main():\n    print('hi')", "python")]
    [InlineData("package main\nimport \"fmt\"\nfunc main() {}", "go")]
    [InlineData("const x = 1;\nconst f = () => x;", "ts")]
    [InlineData("FROM node:20\nRUN npm ci", "dockerfile")]
    public void Detect_KnownSamples(string text, string expected)
        => Assert.Equal(expected, LanguageDetector.Detect(text));

    [Fact]
    public void Detect_Yaml()
        => Assert.Equal("yaml", LanguageDetector.Detect("name: clipmaster\nversion: 1.2.0\nenabled: true"));

    [Fact]
    public void Detect_InvalidJson_IsNotJson()
        => Assert.NotEqual("json", LanguageDetector.Detect("{ this is not json }"));

    [Fact]
    public void Detect_Prose_IsPlain()
        => Assert.Equal("", LanguageDetector.Detect("Just a normal sentence copied from a document."));

    [Fact]
    public void Detect_EmptyOrWhitespace_IsPlain()
    {
        Assert.Equal("", LanguageDetector.Detect(""));
        Assert.Equal("", LanguageDetector.Detect("   \n  "));
        Assert.Equal("", LanguageDetector.Detect(null));
    }

    [Fact]
    public void Detect_VeryLargeInput_DoesNotThrow()
    {
        var big = new string('x', 2_000_000);
        var ex = Record.Exception(() => LanguageDetector.Detect(big));
        Assert.Null(ex);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ClipMaster.Tests/ClipMaster.Tests.csproj --filter "FullyQualifiedName~LanguageDetectorTests" --nologo`
Expected: FAIL — `LanguageDetector` does not exist.

- [ ] **Step 3: Implement `LanguageDetector.cs`**

Create `LanguageDetector.cs`:

```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ClipMaster.Tests/ClipMaster.Tests.csproj --filter "FullyQualifiedName~LanguageDetectorTests" --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add LanguageDetector.cs ClipMaster.Tests/LanguageDetectorTests.cs
git commit -m "feat(detect): add heuristic language detector with tests"
```

---

## Task 4: Syntax highlighter (TDD for tokenizer)

**Files:**
- Create: `SyntaxHighlighter.cs`
- Create: `ClipMaster.Tests/SyntaxHighlighterTests.cs`

The pure `Tokenize` method is unit-tested. The WPF `AppendTo` renderer is thin and verified manually in later UI tasks.

- [ ] **Step 1: Write the failing tests**

Create `ClipMaster.Tests/SyntaxHighlighterTests.cs`:

```csharp
using System.Linq;
using ClipMaster;
using Xunit;

namespace ClipMaster.Tests;

public class SyntaxHighlighterTests
{
    private static TokenKind KindOf(string text, string language, string fragment)
        => SyntaxHighlighter.Tokenize(text, language).First(t => t.Text == fragment).Kind;

    [Fact]
    public void Json_DistinguishesKeyFromStringValue()
    {
        const string json = "{\"name\": \"clip\"}";
        Assert.Equal(TokenKind.Property, KindOf(json, "json", "\"name\""));
        Assert.Equal(TokenKind.String,   KindOf(json, "json", "\"clip\""));
    }

    [Fact]
    public void Json_NumbersAndLiterals()
    {
        const string json = "{\"a\": 12, \"b\": true, \"c\": null}";
        Assert.Equal(TokenKind.Number,  KindOf(json, "json", "12"));
        Assert.Equal(TokenKind.Literal, KindOf(json, "json", "true"));
        Assert.Equal(TokenKind.Literal, KindOf(json, "json", "null"));
    }

    [Fact]
    public void Generic_KeywordVsIdentifier()
    {
        const string code = "const total = 5;";
        Assert.Equal(TokenKind.Keyword, KindOf(code, "ts", "const"));
        Assert.Equal(TokenKind.Plain,   KindOf(code, "ts", "total"));
        Assert.Equal(TokenKind.Number,  KindOf(code, "ts", "5"));
    }

    [Fact]
    public void Generic_LineComment()
    {
        var tokens = SyntaxHighlighter.Tokenize("x = 1 // note", "ts");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Comment && t.Text.Contains("note"));
    }

    [Fact]
    public void Generic_StringLiteral()
    {
        var tokens = SyntaxHighlighter.Tokenize("name = \"hi\"", "python");
        Assert.Contains(tokens, t => t.Kind == TokenKind.String && t.Text == "\"hi\"");
    }

    [Fact]
    public void Reassembling_Tokens_YieldsOriginalText()
    {
        const string code = "const a = \"x\"; // c\nfn();";
        var rebuilt = string.Concat(SyntaxHighlighter.Tokenize(code, "ts").Select(t => t.Text));
        Assert.Equal(code, rebuilt);
    }

    [Fact]
    public void OverCap_ReturnsSinglePlainToken()
    {
        var big = new string('a', SyntaxHighlighter.MaxHighlightChars + 1);
        var tokens = SyntaxHighlighter.Tokenize(big, "ts");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.Plain, tokens[0].Kind);
    }

    [Fact]
    public void UnknownLanguage_ReturnsSinglePlainToken()
    {
        var tokens = SyntaxHighlighter.Tokenize("anything", "");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.Plain, tokens[0].Kind);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ClipMaster.Tests/ClipMaster.Tests.csproj --filter "FullyQualifiedName~SyntaxHighlighterTests" --nologo`
Expected: FAIL — `SyntaxHighlighter` / `TokenKind` do not exist.

- [ ] **Step 3: Implement `SyntaxHighlighter.cs`**

Create `SyntaxHighlighter.cs`. The `Tokenize`/token types are pure; `AppendTo`/`BrushFor` use WPF but are not exercised by tests.

```csharp
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClipMaster;

public enum TokenKind { Plain, Keyword, String, Number, Comment, Punctuation, Property, Literal }

public readonly record struct Token(string Text, TokenKind Kind);

public sealed record LanguageSpec(
    HashSet<string> Keywords,
    string[] LineComments,
    (string Open, string Close)? BlockComment,
    char[] StringDelims);

public static class SyntaxHighlighter
{
    public const int MaxHighlightChars = 100_000;

    // ── Public API ──────────────────────────────────────────────────────────

    public static IReadOnlyList<Token> Tokenize(string? text, string language)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxHighlightChars || string.IsNullOrEmpty(language))
            return [new Token(text ?? "", TokenKind.Plain)];

        return language == "json" ? TokenizeJson(text) : TokenizeGeneric(text, SpecFor(language));
    }

    /// <summary>WPF renderer: append colored runs for the given text to an inline collection.</summary>
    public static void AppendTo(InlineCollection inlines, string text, string language)
    {
        foreach (var tok in Tokenize(text, language))
            inlines.Add(new Run(tok.Text) { Foreground = BrushFor(tok.Kind) });
    }

    // ── JSON tokenizer ──────────────────────────────────────────────────────

    private static List<Token> TokenizeJson(string s)
    {
        var toks = new List<Token>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c))
            {
                int j = i; while (j < n && char.IsWhiteSpace(s[j])) j++;
                toks.Add(new(s[i..j], TokenKind.Plain)); i = j;
            }
            else if (c == '"')
            {
                int j = i + 1;
                while (j < n) { if (s[j] == '\\') { j = Math.Min(j + 2, n); continue; } if (s[j] == '"') { j++; break; } j++; }
                int k = j; while (k < n && char.IsWhiteSpace(s[k])) k++;
                var kind = (k < n && s[k] == ':') ? TokenKind.Property : TokenKind.String;
                toks.Add(new(s[i..j], kind)); i = j;
            }
            else if (c == '-' || char.IsDigit(c))
            {
                int j = i + 1; while (j < n && (char.IsDigit(s[j]) || s[j] is '.' or 'e' or 'E' or '+' or '-')) j++;
                toks.Add(new(s[i..j], TokenKind.Number)); i = j;
            }
            else if (char.IsLetter(c))
            {
                int j = i; while (j < n && char.IsLetter(s[j])) j++;
                var w = s[i..j];
                toks.Add(new(w, w is "true" or "false" or "null" ? TokenKind.Literal : TokenKind.Plain)); i = j;
            }
            else { toks.Add(new(s[i..(i + 1)], TokenKind.Punctuation)); i++; }
        }
        return toks;
    }

    // ── Generic tokenizer ─────────────────────────────────────────────────────

    private static List<Token> TokenizeGeneric(string s, LanguageSpec spec)
    {
        var toks = new List<Token>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];

            // line comment
            var lc = spec.LineComments.FirstOrDefault(p => Matches(s, i, p));
            if (lc != null)
            {
                int j = i; while (j < n && s[j] != '\n') j++;
                toks.Add(new(s[i..j], TokenKind.Comment)); i = j; continue;
            }

            // block comment
            if (spec.BlockComment is { } bc && Matches(s, i, bc.Open))
            {
                int j = s.IndexOf(bc.Close, i + bc.Open.Length, StringComparison.Ordinal);
                j = j < 0 ? n : j + bc.Close.Length;
                toks.Add(new(s[i..j], TokenKind.Comment)); i = j; continue;
            }

            // string
            if (Array.IndexOf(spec.StringDelims, c) >= 0)
            {
                int j = i + 1;
                while (j < n) { if (s[j] == '\\') { j = Math.Min(j + 2, n); continue; } if (s[j] == c) { j++; break; } if (s[j] == '\n') break; j++; }
                toks.Add(new(s[i..Math.Min(j, n)], TokenKind.String)); i = Math.Min(j, n); continue;
            }

            // number
            if (char.IsDigit(c))
            {
                int j = i + 1; while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] is '.' or '_')) j++;
                toks.Add(new(s[i..j], TokenKind.Number)); i = j; continue;
            }

            // identifier / keyword
            if (char.IsLetter(c) || c == '_')
            {
                int j = i; while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                var w = s[i..j];
                toks.Add(new(w, spec.Keywords.Contains(w) ? TokenKind.Keyword : TokenKind.Plain)); i = j; continue;
            }

            // whitespace run
            if (char.IsWhiteSpace(c))
            {
                int j = i; while (j < n && char.IsWhiteSpace(s[j])) j++;
                toks.Add(new(s[i..j], TokenKind.Plain)); i = j; continue;
            }

            // punctuation / operator
            toks.Add(new(s[i..(i + 1)], TokenKind.Punctuation)); i++;
        }
        return toks;
    }

    private static bool Matches(string s, int i, string token) =>
        i + token.Length <= s.Length && string.CompareOrdinal(s, i, token, 0, token.Length) == 0;

    // ── Language specs ────────────────────────────────────────────────────────

    private static readonly char[] QuoteDelims = ['"', '\'', '`'];

    private static LanguageSpec SpecFor(string language) => language switch
    {
        "ts" or "js" => new(new(["const","let","var","function","return","if","else","for","while","import","export","from","class","new","async","await","interface","type","extends","implements","public","private","this","null","true","false","void"]), ["//"], ("/*","*/"), QuoteDelims),
        "go"         => new(new(["package","import","func","return","if","else","for","range","var","const","type","struct","interface","map","chan","go","defer","select","switch","case","nil","true","false"]), ["//"], ("/*","*/"), ['"','`']),
        "rust"       => new(new(["fn","let","mut","return","if","else","for","while","loop","match","struct","enum","impl","trait","pub","use","mod","crate","self","Self","async","await","move","ref","true","false"]), ["//"], ("/*","*/"), ['"']),
        "python"     => new(new(["def","class","return","if","elif","else","for","while","import","from","as","with","try","except","finally","raise","lambda","yield","pass","break","continue","None","True","False","and","or","not","in","is"]), ["#"], null, ['"','\'']),
        "php"        => new(new(["function","return","if","else","elseif","foreach","for","while","class","public","private","protected","new","echo","use","namespace","null","true","false"]), ["//","#"], ("/*","*/"), ['"','\'']),
        "sql"        => new(new(["SELECT","FROM","WHERE","INSERT","INTO","UPDATE","SET","DELETE","CREATE","TABLE","ALTER","DROP","JOIN","LEFT","RIGHT","INNER","OUTER","ON","GROUP","BY","ORDER","HAVING","LIMIT","VALUES","AND","OR","NOT","NULL","AS","DISTINCT","COUNT","select","from","where","insert","into","update","set","delete","create","table","join","and","or","null"]), ["--"], ("/*","*/"), ['\'']),
        "css"        => new(new([]), [], ("/*","*/"), ['"','\'']),
        "yaml"       => new(new(["true","false","null","yes","no"]), ["#"], null, ['"','\'']),
        "shell"      => new(new(["if","then","else","fi","for","do","done","while","case","esac","function","export","return","sudo","cd","echo"]), ["#"], null, ['"','\'']),
        "xml"        => new(new([]), [], ("<!--","-->"), ['"','\'']),
        _            => new(new([]), ["//","#"], ("/*","*/"), QuoteDelims),
    };

    // ── WPF brush mapping ──────────────────────────────────────────────────────

    private static Brush BrushFor(TokenKind kind)
    {
        var key = kind switch
        {
            TokenKind.Keyword     => "CodeKeywordBrush",
            TokenKind.String      => "CodeStringBrush",
            TokenKind.Number      => "CodeNumberBrush",
            TokenKind.Comment     => "CodeCommentBrush",
            TokenKind.Punctuation => "CodePunctuationBrush",
            TokenKind.Property    => "CodePropertyBrush",
            TokenKind.Literal     => "CodeLiteralBrush",
            _                     => "Text1Brush",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gainsboro;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ClipMaster.Tests/ClipMaster.Tests.csproj --filter "FullyQualifiedName~SyntaxHighlighterTests" --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add SyntaxHighlighter.cs ClipMaster.Tests/SyntaxHighlighterTests.cs
git commit -m "feat(highlight): add tokenizer + WPF renderer with tests"
```

---

## Task 5: Theme code-token brushes

**Files:**
- Modify: `Themes/Dark.xaml`

- [ ] **Step 1: Add brushes**

In `Themes/Dark.xaml`, immediately after the `DangerBrush` line (`<SolidColorBrush x:Key="DangerBrush" .../>`), add:

```xml
  <!-- ── Code token colors ─────────────────────────────────────────── -->
  <SolidColorBrush x:Key="CodeKeywordBrush"     Color="#c08cff"/>
  <SolidColorBrush x:Key="CodeStringBrush"      Color="#9ece6a"/>
  <SolidColorBrush x:Key="CodeNumberBrush"      Color="#e0af68"/>
  <SolidColorBrush x:Key="CodeCommentBrush"     Color="#6a6a6a"/>
  <SolidColorBrush x:Key="CodePunctuationBrush" Color="#9aa0aa"/>
  <SolidColorBrush x:Key="CodePropertyBrush"    Color="#7dcfff"/>
  <SolidColorBrush x:Key="CodeLiteralBrush"     Color="#ff9e64"/>
```

- [ ] **Step 2: Build**

Run: `dotnet build ClipMaster.csproj --nologo`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add Themes/Dark.xaml
git commit -m "feat(theme): add code-token brushes"
```

---

## Task 6: Wire detection into AddClip (TDD)

**Files:**
- Modify: `DataService.cs` (in `AddClip`)
- Create: `ClipMaster.Tests/DetectionWiringTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `ClipMaster.Tests/DetectionWiringTests.cs`:

```csharp
using System.IO;
using System.Linq;
using ClipMaster;
using Xunit;

namespace ClipMaster.Tests;

public class DetectionWiringTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    public DetectionWiringTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose()          => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void AddClip_DetectsLanguage_WhenEnabled()
    {
        var svc  = new DataService(_tempDir);
        var data = new AppData();   // HighlightCode defaults to true
        svc.AddClip(data, "{\"a\": 1}");
        Assert.Equal("json", data.Clips[0].Language);
    }

    [Fact]
    public void AddClip_SkipsDetection_WhenDisabled()
    {
        var svc  = new DataService(_tempDir);
        var data = new AppData();
        data.Settings.HighlightCode = false;
        svc.AddClip(data, "{\"a\": 1}");
        Assert.Equal("", data.Clips[0].Language);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ClipMaster.Tests/ClipMaster.Tests.csproj --filter "FullyQualifiedName~DetectionWiringTests" --nologo`
Expected: FAIL — `Language` stays `""` in the enabled case.

- [ ] **Step 3: Implement**

In `DataService.cs`, in `AddClip`, the `else` branch currently inserts a new `ClipEntry`. Replace the new-entry insertion block:

```csharp
        else
        {
            data.Clips.Insert(0, new ClipEntry
            {
                Id           = $"clip_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Random.Shared.Next(10000, 99999)}",
                Raw          = rawText,
                Text         = finalText,
                CopiedAt     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CopyCount    = 1,
                IsSensitive  = isSensitive,
                AppliedRules = applied,
            });
        }
```

with:

```csharp
        else
        {
            data.Clips.Insert(0, new ClipEntry
            {
                Id           = $"clip_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Random.Shared.Next(10000, 99999)}",
                Raw          = rawText,
                Text         = finalText,
                CopiedAt     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CopyCount    = 1,
                IsSensitive  = isSensitive,
                AppliedRules = applied,
                Language     = data.Settings.HighlightCode ? LanguageDetector.Detect(finalText) : "",
            });
        }
```

Also update the dedup (existing-clip) branch so re-copying refreshes the language. In `AddClip`, the `if (existing >= 0)` block sets `clip.Text = finalText;`. Immediately after that line add:

```csharp
            clip.Language = data.Settings.HighlightCode ? LanguageDetector.Detect(finalText) : "";
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ClipMaster.Tests/ClipMaster.Tests.csproj --filter "FullyQualifiedName~DetectionWiringTests" --nologo`
Expected: PASS.

- [ ] **Step 5: Run the full suite (no regressions)**

Run: `dotnet test ClipMaster.Tests/ClipMaster.Tests.csproj --nologo`
Expected: PASS — all tests (original 9 + new).

- [ ] **Step 6: Commit**

```bash
git add DataService.cs ClipMaster.Tests/DetectionWiringTests.cs
git commit -m "feat(detect): set ClipEntry.Language in AddClip with tests"
```

---

## Task 7: Highlighted card preview + language badge + Expand button

**Files:**
- Modify: `MainWindow.xaml.cs` (in `BuildClipCard`)

This is UI; verified by build + manual check (no unit test).

- [ ] **Step 1: Add the language badge**

In `BuildClipCard`, find the badges block that ends with the `transformed` badge:

```csharp
        if (clip.AppliedRules?.Count > 0) badgesPanel.Children.Add(MakeBadge("transformed", "#1a3a2a", "#4caf82"));
```

Immediately after that line add:

```csharp
        if (_db.Settings.HighlightCode && clip.Language.Length > 0)
            badgesPanel.Children.Add(MakeBadge(clip.Language.ToUpperInvariant(), "#22202e", "#9b8cff"));
```

- [ ] **Step 2: Highlight the preview text**

In `BuildClipCard`, the preview `TextBlock` is currently created with `Text = clip.IsSensitive ? "••••••••" : displayText`. Replace this construction:

```csharp
        var clipTextTb  = new TextBlock
        {
            Text         = clip.IsSensitive ? "••••••••" : displayText,
            FontSize     = 12,
            FontFamily   = AppFont,
            Foreground   = (WpfBrush)FindResource("Text1Brush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight    = 72,
            Opacity      = opacity,
            Margin       = new Thickness(0, 2, 0, 8),
            TextWrapping = TextWrapping.Wrap,
            LineHeight   = 18,
        };
```

with this version (note: no `Text` set up front; we branch below):

```csharp
        var isCode = !clip.IsSensitive && _db.Settings.HighlightCode && clip.Language.Length > 0;
        var clipTextTb  = new TextBlock
        {
            FontSize     = 12,
            FontFamily   = isCode ? MonoFont : AppFont,
            Foreground   = (WpfBrush)FindResource("Text1Brush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight    = 72,
            Opacity      = opacity,
            Margin       = new Thickness(0, 2, 0, 8),
            TextWrapping = TextWrapping.Wrap,
            LineHeight   = 18,
        };
        if (clip.IsSensitive)
            clipTextTb.Text = "••••••••";
        else if (isCode)
        {
            // Tokenize only the slice the card can show (cap protects scroll/realize cost).
            var slice = displayText.Length > 600 ? displayText[..600] : displayText;
            SyntaxHighlighter.AppendTo(clipTextTb.Inlines, slice, clip.Language);
        }
        else
            clipTextTb.Text = displayText;
```

The existing sensitive-hover handler right below (`if (clip.IsSensitive) { clipTextTb.MouseEnter ... }`) stays unchanged.

- [ ] **Step 3: Add the Expand button**

In `BuildClipCard`, the action buttons are created here:

```csharp
        var pasteBtn = CardBtn("Paste", "PasteBtn");
        var copyBtn  = CardBtn("Copy",  "ActionBtn");
        var pinBtn   = CardBtn(clip.Pinned ? "Unpin" : "Pin", "ActionBtn");
        var tagBtn   = CardBtn("Tag",   "ActionBtn");
        var delBtn   = CardBtn("✕",     "DangerBtn");
```

After the `tagBtn` line add:

```csharp
        var expandBtn = CardBtn("⤢", "ActionBtn");
```

Then where the buttons are added to `actions` (after `actions.Children.Add(tagBtn);`), add:

```csharp
        expandBtn.Click += (_, _) => OpenDetail(clip);
        actions.Children.Add(expandBtn);
```

(`OpenDetail` is implemented in Task 8. To build this task in isolation, add a temporary stub at the bottom of the class: `private void OpenDetail(ClipEntry clip) { }` — Task 8 replaces it. If executing tasks in order, you may instead do Step 4 of Task 8 first; either works.)

- [ ] **Step 4: Build**

Run: `dotnet build ClipMaster.csproj --nologo`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 5: Manual verification**

Stop any running instance, then run `bin\Debug\net8.0-windows\ClipMaster.exe`. Copy a JSON snippet and a sentence. Open the window (Ctrl+`):
- JSON clip shows a `JSON` badge, monospaced + colored preview.
- Sentence shows no badge, normal font.
- `⤢` button present on each card.

- [ ] **Step 6: Commit**

```bash
git add MainWindow.xaml.cs
git commit -m "feat(ui): highlighted card previews, language badge, expand button"
```

---

## Task 8: Detail view overlay

**Files:**
- Modify: `MainWindow.xaml` (add overlay)
- Modify: `MainWindow.xaml.cs` (open/close/render + handlers + Esc/Ctrl+Enter + close-on-hide)

- [ ] **Step 1: Add the overlay to XAML**

In `MainWindow.xaml`, find the `<!-- Toast overlay -->` comment and insert this block **immediately before** it (so the toast stays on top):

```xml
      <!-- Detail overlay -->
      <Border x:Name="DetailOverlay"
              Grid.RowSpan="6"
              Background="{StaticResource BgBrush}"
              Visibility="Collapsed">
        <DockPanel Margin="12">
          <Grid DockPanel.Dock="Top" Margin="0,0,0,8">
            <StackPanel x:Name="DetailBadges" Orientation="Horizontal" HorizontalAlignment="Left"/>
            <Button Content="Back" HorizontalAlignment="Right"
                    Style="{StaticResource ActionBtn}" Click="DetailBack_Click"/>
          </Grid>
          <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,8,0,0">
            <Button Content="Copy"  Style="{StaticResource ActionBtn}" Margin="0,0,6,0" Click="DetailCopy_Click"/>
            <Button Content="Paste" Style="{StaticResource PasteBtn}"  Margin="0,0,6,0" Click="DetailPaste_Click"/>
            <Button x:Name="DetailTransformBtn" Content="Transform ▾" Style="{StaticResource ActionBtn}" Click="DetailTransform_Click"/>
          </StackPanel>
          <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Auto">
            <TextBlock x:Name="DetailText"
                       FontFamily="Cascadia Code, Consolas, monospace"
                       FontSize="12"
                       Foreground="{StaticResource Text1Brush}"
                       TextWrapping="NoWrap"/>
          </ScrollViewer>
        </DockPanel>
      </Border>
```

- [ ] **Step 2: Add field + open/close/render in code-behind**

In `MainWindow.xaml.cs`, add a field next to the other private fields (near `_renderedClips`):

```csharp
    private string? _detailClipId;
```

If you added a temporary `OpenDetail` stub in Task 7, delete it now. Add these methods (near `ShowToast`):

```csharp
    private void OpenDetail(ClipEntry clip)
    {
        _detailClipId = clip.Id;
        RenderDetail();
        DetailOverlay.Visibility = Visibility.Visible;
    }

    private void CloseDetail()
    {
        _detailClipId = null;
        DetailOverlay.Visibility = Visibility.Collapsed;
    }

    private void RenderDetail()
    {
        var clip = _db.Clips.FirstOrDefault(c => c.Id == _detailClipId);
        if (clip == null) { CloseDetail(); return; }
        var text = !string.IsNullOrEmpty(clip.Text) ? clip.Text : clip.Raw;

        DetailBadges.Children.Clear();
        if (clip.Language.Length > 0)
            DetailBadges.Children.Add(MakeBadge(clip.Language.ToUpperInvariant(), "#22202e", "#9b8cff"));

        DetailText.Inlines.Clear();
        if (_db.Settings.HighlightCode && clip.Language.Length > 0)
            SyntaxHighlighter.AppendTo(DetailText.Inlines, text, clip.Language);
        else
            DetailText.Inlines.Add(new Run(text));
    }

    private void DetailBack_Click(object sender, RoutedEventArgs e) => CloseDetail();

    private void DetailCopy_Click(object sender, RoutedEventArgs e)
    {
        var clip = _db.Clips.FirstOrDefault(c => c.Id == _detailClipId);
        if (clip == null) return;
        System.Windows.Clipboard.SetText(!string.IsNullOrEmpty(clip.Text) ? clip.Text : clip.Raw);
        ShowToast("Copied");
    }

    private void DetailPaste_Click(object sender, RoutedEventArgs e)
    {
        if (_detailClipId == null) return;
        var id = _detailClipId;
        CloseDetail();
        ((App)WpfApp.Current).OnPasteClip(id);
    }

    private void DetailTransform_Click(object sender, RoutedEventArgs e)
    {
        var clip = _db.Clips.FirstOrDefault(c => c.Id == _detailClipId);
        if (clip != null) ShowTransformMenu(clip, (UIElement)sender);
    }
```

`Run` requires `using System.Windows.Documents;` — add it to the using block at the top of `MainWindow.xaml.cs` if not already present. `ShowTransformMenu` is implemented in Task 9; to build this task in isolation add a temporary stub `private void ShowTransformMenu(ClipEntry clip, UIElement anchor) { }` and remove it in Task 9.

- [ ] **Step 3: Esc closes detail first; Ctrl+Enter opens detail**

In `Window_KeyDown`, the method currently starts:

```csharp
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; return; }
```

Replace that line with:

```csharp
        if (e.Key == Key.Escape)
        {
            if (DetailOverlay.Visibility == Visibility.Visible) { CloseDetail(); e.Handled = true; return; }
            Hide(); e.Handled = true; return;
        }
```

Then, inside the `if (_activeTab == "stack")` block, as the first statement add:

```csharp
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !SearchBox.IsFocused)
            {
                var top = GetFilteredClips().FirstOrDefault();
                if (top != null) OpenDetail(top);
                e.Handled = true; return;
            }
```

- [ ] **Step 4: Close detail when the window hides**

In `Window_Deactivated`, replace:

```csharp
        if (IsVisible && !_dialogOpen) Hide();
```

with:

```csharp
        if (IsVisible && !_dialogOpen) { CloseDetail(); Hide(); }
```

- [ ] **Step 5: Build**

Run: `dotnet build ClipMaster.csproj --nologo`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 6: Manual verification**

Run the Debug exe. Copy a JSON snippet, open the window, click `⤢` (or press `Ctrl+Enter`):
- Overlay shows the full clip, highlighted, scrollable, with `[Copy] [Paste] [Transform ▾] [Back]`.
- `Back` and `Esc` close the overlay (Esc with overlay closed still hides the window).
- `Paste` closes overlay and pastes into the previously focused app.

- [ ] **Step 7: Commit**

```bash
git add MainWindow.xaml MainWindow.xaml.cs
git commit -m "feat(ui): add full-clip detail overlay with highlighting"
```

---

## Task 9: Transforms in the UI (context menu + detail view)

**Files:**
- Modify: `MainWindow.xaml.cs` (add `ShowTransformMenu`, `ApplyTransform`, `AddTransformSubmenu`; wire into the text-card context menu)

- [ ] **Step 1: Add the transform helpers**

In `MainWindow.xaml.cs`, add these methods (near the detail-view methods). If you added a `ShowTransformMenu` stub in Task 8, replace it.

```csharp
    private void ApplyTransform(ClipEntry clip, Transform t)
    {
        try
        {
            var src = !string.IsNullOrEmpty(clip.Text) ? clip.Text : clip.Raw;
            clip.Text = t.Apply(src);
            clip.AppliedRules ??= [];
            if (!clip.AppliedRules.Contains(t.Label)) clip.AppliedRules.Add(t.Label);
            _svc.QueueSave(_db);
            if (_detailClipId == clip.Id) RenderDetail();
            RenderStack();
            ShowToast($"{t.Label} applied");
        }
        catch (Exception ex) { ShowToast($"{t.Label}: {ex.Message}"); }
    }

    // Adds a "Transform ▸" parent item (with grouped children) to an existing context menu.
    private void AddTransformSubmenu(ContextMenu menu, ClipEntry clip)
    {
        var parent = new MenuItem { Header = "Transform" };
        foreach (var grp in Transforms.All.GroupBy(t => t.Group))
        {
            var group = new MenuItem { Header = grp.Key };
            foreach (var tr in grp)
            {
                var t  = tr;
                var mi = new MenuItem { Header = t.Label };
                mi.Click += (_, _) => ApplyTransform(clip, t);
                group.Items.Add(mi);
            }
            parent.Items.Add(group);
        }
        menu.Items.Add(parent);
    }

    // Standalone grouped transform menu, anchored to a button (used by the detail view).
    private void ShowTransformMenu(ClipEntry clip, UIElement anchor)
    {
        var menu = new ContextMenu();
        foreach (var grp in Transforms.All.GroupBy(t => t.Group))
        {
            var group = new MenuItem { Header = grp.Key };
            foreach (var tr in grp)
            {
                var t  = tr;
                var mi = new MenuItem { Header = t.Label };
                mi.Click += (_, _) => ApplyTransform(clip, t);
                group.Items.Add(mi);
            }
            menu.Items.Add(group);
        }
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }
```

- [ ] **Step 2: Add "Transform ▸" to the text-card context menu**

In `BuildClipCard`, the right-click menu is built in `card.MouseRightButtonUp`. Find:

```csharp
            menu.Items.Add(MI("Manage tags…", () => OpenTagDialog(clip)));
            menu.Items.Add(new Separator());
```

Insert between those two lines so it reads:

```csharp
            menu.Items.Add(MI("Manage tags…", () => OpenTagDialog(clip)));
            AddTransformSubmenu(menu, clip);
            menu.Items.Add(new Separator());
```

- [ ] **Step 3: Build**

Run: `dotnet build ClipMaster.csproj --nologo`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Manual verification**

Run the Debug exe. Copy `{"b":2,"a":1}`:
- Right-click the card → **Transform ▸ JSON ▸ Pretty-print** → card preview re-renders pretty-printed, `transformed` badge appears.
- Open detail (`⤢`) → **Transform ▾ ▸ Encode ▸ Base64 encode** → detail re-renders encoded.
- Right-click a non-JSON clip → **Transform ▸ JSON ▸ Pretty-print** → toast shows an error, clip unchanged.

- [ ] **Step 5: Commit**

```bash
git add MainWindow.xaml.cs
git commit -m "feat(ui): on-demand transforms via context menu and detail view"
```

---

## Task 10: Settings toggle

**Files:**
- Modify: `MainWindow.xaml.cs` (in `RenderSettings`)

- [ ] **Step 1: Add the "Code" section**

In `RenderSettings`, find the `// Privacy` section:

```csharp
        // Privacy
        SectionHeader("PRIVACY");

        var maskCb = new WpfCheckBox { IsChecked = s.MaskPasswords, Foreground = (WpfBrush)FindResource("Text1Brush") };
        SettingRow("Mask sensitive content", "Blur passwords & tokens", maskCb);
```

Immediately after that `SettingRow(...)` line add:

```csharp
        // Code
        SectionHeader("CODE");

        var highlightCb = new WpfCheckBox { IsChecked = s.HighlightCode, Foreground = (WpfBrush)FindResource("Text1Brush") };
        SettingRow("Highlight & detect code", "Syntax-highlight code clips and show a language badge", highlightCb);
```

- [ ] **Step 2: Persist the toggle**

In `RenderSettings`, the `saveBtn.Click` handler assigns settings. Find:

```csharp
            s.MaskPasswords   = maskCb.IsChecked == true;
```

Immediately after that line add:

```csharp
            s.HighlightCode   = highlightCb.IsChecked == true;
```

- [ ] **Step 3: Build**

Run: `dotnet build ClipMaster.csproj --nologo`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Manual verification**

Run the Debug exe → Settings tab → **Code** section shows the toggle. Turn it off, Save, switch to Stack: existing code clips render as plain text with no badge. Turn back on, Save: highlighting returns for newly copied clips (existing clips backfill language on next copy).

- [ ] **Step 5: Commit**

```bash
git add MainWindow.xaml.cs
git commit -m "feat(settings): add Highlight & detect code toggle"
```

---

## Task 11: Final verification

- [ ] **Step 1: Full build (Debug + Release)**

Run: `dotnet build ClipMaster.csproj --nologo`
Run: `dotnet build ClipMaster.csproj -c Release --nologo`
Expected: both `Build succeeded. 0 Error(s)`

- [ ] **Step 2: Full test suite**

Run: `dotnet test ClipMaster.Tests/ClipMaster.Tests.csproj --nologo`
Expected: PASS — original 9 tests plus all new tests (transforms, detector, highlighter, detection wiring).

- [ ] **Step 3: End-to-end manual smoke**

Stop any running instance, launch the Release exe, and confirm: detection badges, card highlighting, detail view (open/close/scroll), transforms (context menu + detail, including an error toast on invalid input), the settings toggle, and that the perf behavior is unchanged (snappy typing/scroll, no freeze on copy). Quit from tray, relaunch, confirm `Language` persisted on existing clips.

---

## Self-Review

**Spec coverage:**
- Transform library (JSON/encode/case/whitespace, on-demand, mutate `Text`/preserve `Raw`) → Tasks 2, 9 ✓
- Language detection on capped prefix, persisted on `ClipEntry.Language` → Tasks 1, 3, 6 ✓
- Hand-rolled tokenizer + themed runs, card-preview slice + 100 KB detail cap → Tasks 4, 5, 7, 8 ✓
- Card badge + monospace preview + Expand affordance → Task 7 ✓
- Detail overlay with Copy/Paste/Transform/Back, Esc/Ctrl+Enter, close-on-hide → Task 8 ✓
- "Transform ▸" context submenu + detail Transform menu, invalid-input toast → Task 9 ✓
- `HighlightCode` settings toggle → Tasks 1, 10 ✓
- Tests for detector, transforms, highlighter → Tasks 2, 3, 4, 6 ✓
- Dropped paste-as-plain-text; whitespace lives in the transform library ✓
- Plain `Enter` unchanged (expand uses `Ctrl+Enter`) → Task 8 ✓

**Placeholder scan:** No TBD/TODO; the two intentional temporary stubs (`OpenDetail`, `ShowTransformMenu`) are explicitly removed in their owning tasks and exist only to let tasks build in isolation.

**Type consistency:** `Transform(Id,Label,Group,Apply)`, `Token(Text,Kind)`, `TokenKind`, `LanguageSpec`, `SyntaxHighlighter.Tokenize/AppendTo/MaxHighlightChars`, `LanguageDetector.Detect`, `ClipEntry.Language`, `AppSettings.HighlightCode`, `OpenDetail/CloseDetail/RenderDetail/ApplyTransform/ShowTransformMenu/AddTransformSubmenu`, `_detailClipId` — used consistently across tasks.
