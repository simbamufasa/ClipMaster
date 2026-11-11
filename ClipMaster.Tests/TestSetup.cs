using System.IO;
using System.Runtime.CompilerServices;
using ClipMaster;

namespace ClipMaster.Tests;

internal static class TestSetup
{
    // TraceLog writes to %USERPROFILE%\.clipmaster\debug.log by default — the real data directory
    // of the person running the tests. Tests must never write there.
    [ModuleInitializer]
    internal static void RedirectTraceLog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clipmaster-tests");
        Directory.CreateDirectory(dir);
        TraceLog.LogFile = Path.Combine(dir, "debug.log");
    }
}

/// <summary>Builds clipboard payloads the way Notes does (cf_html() in its clipboard.rs).</summary>
internal static class NotesPayload
{
    public const string StartMark = "<!--StartFragment-->";
    public const string EndMark   = "<!--EndFragment-->";

    /// <summary>The "HTML Format" bytes for a fragment, without the terminating NUL.</summary>
    public static byte[] CfHtml(string fragment)
    {
        var utf8   = new System.Text.UTF8Encoding(false);
        var prefix = utf8.GetBytes($"<html>\r\n<body>\r\n{StartMark}");
        var body   = utf8.GetBytes(fragment);
        var suffix = utf8.GetBytes($"{EndMark}\r\n</body>\r\n</html>");

        static string Header(int startHtml, int endHtml, int startFragment, int endFragment) =>
            $"Version:0.9\r\nStartHTML:{startHtml:D10}\r\nEndHTML:{endHtml:D10}\r\n" +
            $"StartFragment:{startFragment:D10}\r\nEndFragment:{endFragment:D10}\r\n";

        var headerLength  = Header(0, 0, 0, 0).Length;
        var startFragment = headerLength + prefix.Length;
        var endFragment   = startFragment + body.Length;
        var endHtml       = endFragment + suffix.Length;

        var header = utf8.GetBytes(Header(headerLength, endHtml, startFragment, endFragment));
        return [.. header, .. prefix, .. body, .. suffix];
    }

    /// <summary>An image tag holding roughly <paramref name="bytes"/> bytes of base64 data.</summary>
    public static string Image(int bytes, string alt = "shot") =>
        $"<img src=\"data:image/png;base64,{new string('Q', Math.Max(4, bytes / 4 * 4))}\" alt=\"{alt}\">";

    public static RichPayload Payload(string fragment, string text)
    {
        var html = CfHtml(fragment);
        return new RichPayload(html, text, RichClipboard.CountImages(html));
    }
}
