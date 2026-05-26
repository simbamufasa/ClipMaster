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
