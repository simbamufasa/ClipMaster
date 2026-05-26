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
