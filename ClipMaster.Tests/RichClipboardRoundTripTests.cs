using System.IO;
using System.Text;
using ClipMaster;
using Xunit;
using Xunit.Abstractions;

namespace ClipMaster.Tests;

/// <summary>
/// The real thing, end to end, on a real (but private) Windows clipboard: another program puts a
/// Notes-style item there with raw Win32, ClipMaster's own capture code reads it, the clip is
/// stored and reloaded as after a restart, ClipMaster's own restore code writes it back, and the
/// raw bytes are compared with what was copied.
///
/// See <see cref="IsolatedClipboard"/> for how the user's clipboard is kept out of it. When the
/// private window station cannot be set up, every test here returns without doing anything.
/// </summary>
[Collection(IsolatedClipboardCollection.Name)]
public sealed class RichClipboardRoundTripTests : IDisposable
{
    private readonly IsolatedClipboard _clipboard;
    private readonly ITestOutputHelper _output;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly List<DataService> _services = [];

    public RichClipboardRoundTripTests(IsolatedClipboard clipboard, ITestOutputHelper output)
    {
        _clipboard = clipboard;
        _output    = output;
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var svc in _services) svc.Flush();
        Directory.Delete(_tempDir, recursive: true);
    }

    private DataService Svc()
    {
        var svc = new DataService(_tempDir);
        _services.Add(svc);
        return svc;
    }

    /// <summary>False (after saying so) when there is no isolated clipboard to test on.</summary>
    private bool Ready()
    {
        if (_clipboard.Isolated)
        {
            _output.WriteLine($"clipboard tests are running on window station {_clipboard.StationName}");
            return true;
        }
        _output.WriteLine($"SKIPPED: no private window station ({_clipboard.SkipReason}); the real clipboard was not touched");
        return false;
    }

    /// <summary>
    /// Copy in "Notes", capture, store, restart, restore, read back raw. Returns the stored entry.
    /// Runs on the isolated clipboard thread.
    /// </summary>
    private ClipEntry RoundTrip(byte[] cfHtml, string text, int expectedImages)
    {
        // 1. Another program copies: HTML Format (NUL-terminated) + CF_UNICODETEXT, raw Win32.
        IsolatedClipboard.SetNotesItem(cfHtml, text);

        // 2. ClipMaster's capture code decides what this is and reads it.
        var capture = new ClipboardMonitor().Capture();
        Assert.Equal(ClipboardCaptureKind.Rich, capture.Kind);
        Assert.Equal(expectedImages, capture.Rich!.ImageCount);
        Assert.Equal(text, capture.Rich.Text);
        Assert.True(cfHtml.AsSpan().SequenceEqual(capture.Rich.Html), "captured HTML bytes differ from the copied ones");

        // 3. Stored through DataService.
        var first = Svc();
        var data  = first.Load();
        Assert.Equal(RichAddResult.Added, first.AddRichClip(data, capture.Rich));
        first.Flush();

        // 4. Something else is copied meanwhile, so the restore has to do all the work.
        IsolatedClipboard.SetRaw((IsolatedClipboard.UnicodeText, IsolatedClipboard.Utf16("something else")));
        Assert.Null(IsolatedClipboard.GetRaw(IsolatedClipboard.HtmlFormat));

        // 5. ClipMaster restarts: a new DataService on the same folder.
        var second = Svc();
        var loaded = second.Load();
        var clip   = Assert.Single(loaded.Clips);
        Assert.True(clip.IsRich);
        Assert.Equal(expectedImages, clip.RichImages);
        Assert.Equal(cfHtml.LongLength, clip.RichBytes);

        // 6. ClipMaster's restore code puts the clip back.
        var sequence = RichClipboard.Restore(second, clip);
        Assert.NotNull(sequence);

        // 7. Read back raw: the HTML Format bytes up to the NUL are the copied bytes, exactly.
        var raw = IsolatedClipboard.GetRaw(IsolatedClipboard.HtmlFormat);
        Assert.NotNull(raw);
        Assert.True(raw!.Length > cfHtml.Length, "the restored HTML must be NUL-terminated");
        Assert.Equal(0, raw[cfHtml.Length]);
        Assert.True(raw.AsSpan(0, cfHtml.Length).SequenceEqual(cfHtml), "restored HTML bytes differ from the copied ones");
        Assert.Equal(-1, raw.AsSpan(0, cfHtml.Length).IndexOf((byte)0));
        Assert.Equal(text, IsolatedClipboard.GetText());

        // 8. ClipMaster does not capture its own restore again…
        var monitor = new ClipboardMonitor { IgnoredSequence = sequence };
        Assert.Equal(ClipboardCaptureKind.None, monitor.Capture().Kind);
        // …but a monitor that did not make the write sees the same rich item (so the target
        // application does too), and storing it again promotes the entry instead of adding one.
        var again = new ClipboardMonitor().Capture();
        Assert.Equal(ClipboardCaptureKind.Rich, again.Kind);
        Assert.Equal(RichAddResult.Promoted, second.AddRichClip(loaded, again.Rich!));
        Assert.Single(loaded.Clips);
        Assert.Equal(2, loaded.Clips[0].CopyCount);
        // A copy made by someone else afterwards is captured normally.
        IsolatedClipboard.SetRaw((IsolatedClipboard.UnicodeText, IsolatedClipboard.Utf16("copied later")));
        Assert.Equal(ClipboardCaptureKind.Text, monitor.Capture().Kind);

        return clip;
    }

    [Fact]
    public void TheTestsRunAwayFromTheUsersClipboard()
    {
        if (!Ready()) return;
        Assert.False(_clipboard.StationName.Equals("WinSta0", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual("", _clipboard.StationName);
        _clipboard.Run(IsolatedClipboard.AssertIsolated);
        // Off the clipboard thread the guard refuses: this thread is not on the test desktop.
        Assert.Throws<InvalidOperationException>(IsolatedClipboard.AssertIsolated);
    }

    [Fact]
    public void TwoImagesWithTextBetweenThem_RoundTripByteForByte()
    {
        if (!Ready()) return;
        var fragment = $"<h1>Login review</h1><p>before</p>{NotesPayload.Image(200_000, "first")}" +
                       $"<p>between</p>{NotesPayload.Image(120_000, "second")}<p>after</p>";
        var html = NotesPayload.CfHtml(fragment);

        _clipboard.Run(() =>
        {
            var clip = RoundTrip(html, "Login review\r\n\r\nbefore\r\n\r\nbetween\r\n\r\nafter", expectedImages: 2);
            Assert.Equal("Rich · 2 images", RichClipboard.Label(clip.RichImages));
            Assert.False(clip.IsSensitive);
        });
    }

    [Fact]
    public void NonAsciiText_RoundTripsByteForByte()
    {
        if (!Ready()) return;
        // Em dash, accents, emoji (astral plane) and CJK: every one is more bytes than characters,
        // which is exactly what breaks when offsets or encodings are mishandled.
        const string text = "Review — café 😀 日本語 «naïve» ✓";
        var fragment = $"<h1>{text}</h1><ul><li>Zoë — 東京</li></ul><pre><code>let s = \"é😀\";</code></pre>" +
                       $"<table><tr><td>ß</td><td>Ω</td></tr></table>{NotesPayload.Image(5_000)}<p>fin — 终</p>";
        var html = NotesPayload.CfHtml(fragment);
        Assert.True(html.Length > Encoding.UTF8.GetString(html).Length, "the payload must contain multi-byte text");

        _clipboard.Run(() =>
        {
            var clip = RoundTrip(html, text, expectedImages: 1);
            Assert.Equal(text, clip.Raw);
        });
    }

    [Fact]
    public void ATwentyMegabytePayload_RoundTripsByteForByte()
    {
        if (!Ready()) return;
        var fragment = new StringBuilder("<p>start — début</p>");
        for (var i = 0; i < 4; i++) fragment.Append(NotesPayload.Image(5 * 1024 * 1024, $"shot {i}")).Append($"<p>step {i} — 日本</p>");
        var html = NotesPayload.CfHtml(fragment.ToString());
        Assert.InRange(html.Length, 20 * 1024 * 1024, 21 * 1024 * 1024);

        _clipboard.Run(() =>
        {
            RoundTrip(html, "start — début", expectedImages: 4);
            // The 20 MB stayed out of the database.
            Assert.True(new FileInfo(Path.Combine(_tempDir, "data.bin")).Length < 20_000);
        });
    }

    /// <summary>
    /// The largest note Notes will copy (96 MB of HTML). Needs about a gigabyte of memory, so it
    /// only runs when asked for: set CLIPMASTER_LARGE_TESTS=1.
    /// </summary>
    [Fact]
    public void TheLargestNoteNotesCanCopy_RoundTripsByteForByte()
    {
        if (Environment.GetEnvironmentVariable("CLIPMASTER_LARGE_TESTS") != "1")
        {
            _output.WriteLine("not run: set CLIPMASTER_LARGE_TESTS=1 to include the 96 MB round trip");
            return;
        }
        if (!Ready()) return;
        const int notesLimit = 96 * 1024 * 1024;
        var html = NotesPayload.CfHtml($"<p>début</p>{NotesPayload.Image(notesLimit - 200)}<p>fin — 终</p>");
        Assert.InRange(html.Length, notesLimit - 1024, RichClipboard.MaxHtmlBytes);

        _clipboard.Run(() => RoundTrip(html, "début\r\n\r\nfin — 终", expectedImages: 1));
    }

    [Fact]
    public void AnImageOnlyNote_WithEmptyPlainText_RoundTrips()
    {
        if (!Ready()) return;
        var html = NotesPayload.CfHtml(NotesPayload.Image(50_000, "only"));

        _clipboard.Run(() =>
        {
            var clip = RoundTrip(html, "", expectedImages: 1);
            Assert.Equal("", clip.Raw);
            Assert.StartsWith("(1 image, no text", RichClipboard.PreviewText(clip));
        });
    }

    [Fact]
    public void AnItemWithNoTextFormatAtAll_IsStillCaptured()
    {
        if (!Ready()) return;
        var html = NotesPayload.CfHtml($"<p>x</p>{NotesPayload.Image(1_000)}");

        _clipboard.Run(() =>
        {
            IsolatedClipboard.SetNotesItem(html, text: null);
            var capture = new ClipboardMonitor().Capture();
            Assert.Equal(ClipboardCaptureKind.Rich, capture.Kind);
            Assert.Equal("", capture.Rich!.Text);
            Assert.True(html.AsSpan().SequenceEqual(capture.Rich.Html));
        });
    }

    [Fact]
    public void HtmlWithoutAnImage_GoesDownTheTextPath()
    {
        if (!Ready()) return;
        var html = NotesPayload.CfHtml("<h1>Title — café</h1><p>just words</p><pre><code>&lt;img src=x&gt;</code></pre>");
        const string text = "Title — café\r\n\r\njust words";

        _clipboard.Run(() =>
        {
            IsolatedClipboard.SetNotesItem(html, text);

            Assert.Equal(RichReadStatus.NotRich, RichClipboard.TryRead(out var payload));
            Assert.Null(payload);

            // The legacy path reads the text with WPF's Clipboard, on this private station.
            var capture = new ClipboardMonitor().Capture();
            Assert.Equal(ClipboardCaptureKind.Text, capture.Kind);
            Assert.Equal(text, capture.Text);

            var svc  = Svc();
            var data = svc.Load();
            svc.AddClip(data, capture.Text!);
            var clip = Assert.Single(data.Clips);
            Assert.False(clip.IsRich);
            Assert.Null(clip.RichPath);
            Assert.False(Directory.Exists(Path.Combine(_tempDir, "rich")));
        });
    }

    [Fact]
    public void HtmlOverTheLimit_IsCapturedAsItsPlainText()
    {
        if (!Ready()) return;
        var html = NotesPayload.CfHtml($"<p>big</p>{NotesPayload.Image(300_000)}");
        const string text = "big note — plain text";

        _clipboard.Run(() =>
        {
            IsolatedClipboard.SetNotesItem(html, text);

            Assert.Equal(RichReadStatus.TooLarge, RichClipboard.TryRead(out var payload, maxHtmlBytes: 100_000));
            Assert.Null(payload);
            // Exactly at the limit is still rich.
            Assert.Equal(RichReadStatus.Rich, RichClipboard.TryRead(out _, maxHtmlBytes: html.Length));
            Assert.Equal(RichReadStatus.TooLarge, RichClipboard.TryRead(out _, maxHtmlBytes: html.Length - 1));

            var capture = new ClipboardMonitor { MaxRichHtmlBytes = 100_000 }.Capture();
            Assert.Equal(ClipboardCaptureKind.Text, capture.Kind);
            Assert.Equal(text, capture.Text);
        });
    }

    [Fact]
    public void ABitmapCopiedWithAnHtmlSnippetAndNoText_IsLeftToTheImagePath()
    {
        if (!Ready()) return;
        // What "Copy image" in a browser produces: the pixels, plus HTML pointing at the URL.
        var html = NotesPayload.CfHtml("<img src=\"https://example.invalid/picture.png\">");
        var dib  = new byte[40 + 4];                       // BITMAPINFOHEADER + one 32-bit pixel
        BitConverter.GetBytes(40).CopyTo(dib, 0);          // biSize
        BitConverter.GetBytes(1).CopyTo(dib, 4);           // biWidth
        BitConverter.GetBytes(1).CopyTo(dib, 8);           // biHeight
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);   // biPlanes
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);  // biBitCount
        var nulTerminated = new byte[html.Length + 1];
        html.CopyTo(nulTerminated, 0);

        _clipboard.Run(() =>
        {
            IsolatedClipboard.SetRaw((IsolatedClipboard.HtmlFormat, nulTerminated), (IsolatedClipboard.Dib, dib));
            Assert.Equal(RichReadStatus.NotRich, RichClipboard.TryRead(out _));

            // With text next to it, the same HTML is a rich item.
            IsolatedClipboard.SetRaw((IsolatedClipboard.HtmlFormat, nulTerminated), (IsolatedClipboard.Dib, dib),
                                     (IsolatedClipboard.UnicodeText, IsolatedClipboard.Utf16("caption")));
            Assert.Equal(RichReadStatus.Rich, RichClipboard.TryRead(out var payload));
            Assert.Equal("caption", payload!.Text);
        });
    }

    [Fact]
    public void AClipWhoseFileIsGone_RestoresNothing_AndLeavesTheClipboardAlone()
    {
        if (!Ready()) return;
        var html = NotesPayload.CfHtml($"<p>x</p>{NotesPayload.Image(1_000)}");

        _clipboard.Run(() =>
        {
            var svc  = Svc();
            var data = svc.Load();
            svc.AddRichClip(data, new RichPayload(html, "x", 1));
            File.Delete(Directory.GetFiles(Path.Combine(_tempDir, "rich")).Single());

            IsolatedClipboard.SetRaw((IsolatedClipboard.UnicodeText, IsolatedClipboard.Utf16("still here")));
            Assert.Null(RichClipboard.Restore(svc, data.Clips[0]));
            Assert.Equal("still here", IsolatedClipboard.GetText());
        });
    }
}
