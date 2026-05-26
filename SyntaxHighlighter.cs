using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

using WpfApp     = System.Windows.Application;
using WpfBrush   = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

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
            string? lc = null;
            foreach (var prefix in spec.LineComments)
                if (Matches(s, i, prefix)) { lc = prefix; break; }
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

    private static WpfBrush BrushFor(TokenKind kind)
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
        return WpfApp.Current?.TryFindResource(key) as WpfBrush ?? WpfBrushes.Gainsboro;
    }
}
