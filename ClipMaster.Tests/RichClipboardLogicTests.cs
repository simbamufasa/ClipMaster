using System.Text;
using System.Text.RegularExpressions;
using ClipMaster;
using Xunit;

namespace ClipMaster.Tests;

public class RichClipboardLogicTests
{
    private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void TheOldCaptureCheck_NeverMatchedRealHtml()
    {
        // The first preview used this pattern. In a verbatim string it means "<img", a literal
        // backslash, then "b" — so no real image tag ever matched and rich capture never fired.
        const string oldPattern = @"<img\\b";
        const string html = "<p>x</p><img src=\"data:image/png;base64,AAAA\"><IMG alt=\"y\">";

        Assert.False(Regex.IsMatch(html, oldPattern, RegexOptions.IgnoreCase));
        Assert.True(Regex.IsMatch(@"<img\b", oldPattern, RegexOptions.IgnoreCase));

        Assert.True(RichClipboard.IsRichCandidate(html));
        Assert.Equal(2, RichClipboard.CountImages(U(html)));
    }

    [Theory]
    [InlineData("<img src=\"a.png\">", 1)]
    [InlineData("<IMG SRC=\"a.png\">", 1)]
    [InlineData("<Img\tsrc=a>", 1)]
    [InlineData("<img/>", 1)]
    [InlineData("<img>", 1)]
    [InlineData("<p>a</p><img src=1><p>b</p><img src=2><img src=3>", 3)]
    [InlineData("<img", 1)]                                   // same as the regex <img\b at end of input
    [InlineData("<p>no pictures here</p>", 0)]
    [InlineData("<imgx src=a>", 0)]                           // a different tag name
    [InlineData("<img_ src=a>", 0)]
    [InlineData("<image href=a>", 0)]
    [InlineData("<pre><code>&lt;img src=a&gt;</code></pre>", 0)]   // escaped markup is text
    [InlineData("< img src=a>", 0)]
    [InlineData("<im", 0)]
    [InlineData("<", 0)]
    [InlineData("", 0)]
    public void CountImages_MatchesImageTagsOnly(string html, int expected)
    {
        Assert.Equal(expected, RichClipboard.CountImages(U(html)));
        Assert.Equal(expected > 0, RichClipboard.IsRichCandidate(html));
        // The byte scanner agrees with the regex the capture check was meant to be.
        Assert.Equal(expected, Regex.Matches(html, @"<img\b", RegexOptions.IgnoreCase).Count);
    }

    [Fact]
    public void CountImages_IsNotConfusedByNonAsciiText()
    {
        var html = U("<h1>Review — café 😀 日本語</h1><img src=\"data:image/png;base64,AAAA\"><p>终</p>");
        Assert.Equal(1, RichClipboard.CountImages(html));
    }

    [Fact]
    public void IsRichCandidate_RejectsMissingHtml()
    {
        Assert.False(RichClipboard.IsRichCandidate((string?)null));
        Assert.False(RichClipboard.IsRichCandidate(""));
    }

    [Fact]
    public void ContentHash_IdentifiesTheHtmlAndTextPair()
    {
        var html = U("<p>a</p><img src=1>");
        var same = RichClipboard.ContentHash(html, "a");

        Assert.Equal(same, RichClipboard.ContentHash(U("<p>a</p><img src=1>"), "a"));
        Assert.NotEqual(same, RichClipboard.ContentHash(U("<p>a</p><img src=2>"), "a"));
        Assert.NotEqual(same, RichClipboard.ContentHash(html, "b"));
        Assert.NotEqual(same, RichClipboard.ContentHash(html, ""));
        Assert.Equal(RichClipboard.ContentHash(html, ""), RichClipboard.ContentHash(html, null));
        Assert.Matches("^[0-9a-f]{64}$", same);
    }

    [Theory]
    [InlineData(0, "Rich")]
    [InlineData(1, "Rich · 1 image")]
    [InlineData(2, "Rich · 2 images")]
    [InlineData(14, "Rich · 14 images")]
    public void Label_NamesTheImageCount(int images, string expected) =>
        Assert.Equal(expected, RichClipboard.Label(images));

    [Fact]
    public void PreviewText_IsThePlainText_OrADescriptionWhenThereIsNone()
    {
        var withText = new ClipEntry { IsRich = true, Raw = "Review notes", Text = "Review notes", RichImages = 2, RichBytes = 5_000_000 };
        Assert.Equal("Review notes", RichClipboard.PreviewText(withText));

        var imagesOnly = new ClipEntry { IsRich = true, RichImages = 2, RichBytes = 5 * 1_048_576 };
        Assert.Equal("(2 images, no text · 5.0 MB)", RichClipboard.PreviewText(imagesOnly));

        var oneSmall = new ClipEntry { IsRich = true, Raw = "  \r\n", Text = "  \r\n", RichImages = 1, RichBytes = 900 };
        Assert.Equal("(1 image, no text · 1 KB)", RichClipboard.PreviewText(oneSmall));
    }

    [Fact]
    public void TheLimitIsAboveWhatNotesCanCopy()
    {
        // Notes refuses to copy more than 96 MB of HTML (MAX_HTML_BYTES in its clipboard.rs).
        Assert.True(RichClipboard.MaxHtmlBytes > 96 * 1024 * 1024 + 1024);
    }

    [Fact]
    public void NotesPayloadHelper_WritesByteOffsets()
    {
        // Guards the test helper itself: offsets must be byte offsets, as in Notes.
        const string fragment = "<p>café 😀 日本</p>";
        var bytes = NotesPayload.CfHtml(fragment);
        var text  = Encoding.UTF8.GetString(bytes);
        int Number(string key) => int.Parse(text.Substring(text.IndexOf(key, StringComparison.Ordinal) + key.Length, 10));

        Assert.StartsWith("Version:0.9\r\nStartHTML:", text);
        Assert.Equal(U(fragment), bytes[Number("StartFragment:")..Number("EndFragment:")]);
        Assert.Equal(bytes.Length, Number("EndHTML:"));
    }
}
