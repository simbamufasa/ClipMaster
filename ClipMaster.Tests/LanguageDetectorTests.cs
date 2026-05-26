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
