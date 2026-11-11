using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ClipMaster;

/// <summary>
/// One rich clipboard item: the exact bytes of the Windows "HTML Format" (CF_HTML) payload up to
/// its terminating NUL, plus the plain-text fallback that was on the clipboard with it.
/// </summary>
/// <param name="Html">CF_HTML bytes without the terminating NUL. Never decoded or re-encoded.</param>
/// <param name="Text">CF_UNICODETEXT content; empty when the item had no text.</param>
/// <param name="ImageCount">Number of image tags found in <paramref name="Html"/>.</param>
/// <param name="Sequence">Clipboard sequence number the pair was read at (0 when unknown).</param>
public sealed record RichPayload(byte[] Html, string Text, int ImageCount, uint Sequence = 0);

public enum RichReadStatus
{
    /// <summary>No HTML with images on the clipboard: use the legacy text/image path.</summary>
    NotRich,
    /// <summary>A rich pair was read.</summary>
    Rich,
    /// <summary>The HTML is larger than the limit: use the legacy text path.</summary>
    TooLarge,
    /// <summary>Another program holds the clipboard open: retry shortly.</summary>
    Busy,
    /// <summary>Reading failed: use the legacy text path.</summary>
    Failed,
}

/// <summary>
/// Capture and restore of rich clips (HTML with images + plain text) as one clipboard item.
///
/// The CF_HTML header holds BYTE offsets into the UTF-8 payload, so a restored payload must be
/// byte-identical to the captured one. This class therefore moves the raw bytes with Win32 and
/// never turns the HTML into a .NET string (which would also triple the memory for a payload
/// made mostly of base64 image data).
/// </summary>
public static class RichClipboard
{
    /// <summary>
    /// Largest CF_HTML payload captured as a rich clip. Notes refuses to copy more than 96 MB of
    /// HTML; this leaves headroom for the CF_HTML envelope and for other sources. Anything larger
    /// is captured as plain text only.
    /// </summary>
    public const int MaxHtmlBytes = 128 * 1024 * 1024;

    public const string HtmlFormatName = "HTML Format";

    private const uint CF_BITMAP      = 2;
    private const uint CF_DIB         = 8;
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE  = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool   OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool   CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool   EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr handle);
    [DllImport("user32.dll")]                      private static extern bool   IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")]                      private static extern uint   GetClipboardSequenceNumber();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormatW(string name);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr  GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr  GlobalFree(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr  GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool    GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr handle);

    // ── Pure logic ──────────────────────────────────────────────────────────

    /// <summary>
    /// Counts image tags: "&lt;img" in any letter case, followed by something that ends the tag
    /// name (the same thing the regex <c>&lt;img\b</c> matches). Works on the raw bytes; UTF-8
    /// never produces these ASCII bytes inside another character.
    /// </summary>
    public static int CountImages(ReadOnlySpan<byte> html)
    {
        var count = 0;
        while (true)
        {
            var at = html.IndexOf((byte)'<');
            if (at < 0 || html.Length - at < 4) return count;
            html = html[(at + 1)..];
            if ((html[0] | 0x20) != 'i' || (html[1] | 0x20) != 'm' || (html[2] | 0x20) != 'g') continue;
            if (html.Length == 3 || !IsNameByte(html[3])) count++;
        }
    }

    private static bool IsNameByte(byte b) =>
        b >= 0x80 || b == '_' || (b >= '0' && b <= '9') || (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z');

    /// <summary>
    /// HTML is captured as a rich clip only when it contains at least one image. HTML without
    /// images stays on the legacy text path, exactly as before.
    /// </summary>
    public static bool IsRichCandidate(ReadOnlySpan<byte> html) => CountImages(html) > 0;

    public static bool IsRichCandidate(string? html) =>
        !string.IsNullOrEmpty(html) && IsRichCandidate(System.Text.Encoding.UTF8.GetBytes(html));

    /// <summary>
    /// Identity of a rich item for de-duplication: SHA-256 over the HTML bytes and the plain text.
    /// </summary>
    public static string ContentHash(ReadOnlySpan<byte> html, string? text)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(html);
        hash.AppendData(stackalloc byte[] { 0 });
        hash.AppendData(MemoryMarshal.AsBytes((text ?? "").AsSpan()));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>The small label shown on a rich clip, e.g. "Rich · 2 images".</summary>
    public static string Label(int imageCount) => imageCount switch
    {
        <= 0 => "Rich",
        1    => "Rich · 1 image",
        _    => $"Rich · {imageCount} images",
    };

    /// <summary>What the list shows for a rich clip: its plain text, or a description when it has none.</summary>
    public static string PreviewText(ClipEntry clip)
    {
        var text = !string.IsNullOrEmpty(clip.Text) ? clip.Text : clip.Raw;
        if (!string.IsNullOrWhiteSpace(text)) return text;
        var what = clip.RichImages switch
        {
            <= 0 => "Rich content",
            1    => "1 image",
            _    => $"{clip.RichImages} images",
        };
        return $"({what}, no text · {FormatSize(clip.RichBytes)})";
    }

    public static string FormatSize(long bytes) => bytes >= 1_048_576
        ? $"{bytes / 1_048_576.0:F1} MB"
        : $"{Math.Max(1, (bytes + 1023) / 1024)} KB";

    // ── Clipboard access ────────────────────────────────────────────────────

    // Not cached: the id is looked up in the window station the calling thread belongs to.
    private static uint HtmlFormat() => RegisterClipboardFormatW(HtmlFormatName);

    /// <summary>
    /// The clipboard sequence number of the window station this thread uses. It changes every
    /// time the clipboard contents change.
    /// </summary>
    public static uint CurrentSequence() => GetClipboardSequenceNumber();

    /// <summary>
    /// Reads the HTML + text pair when the clipboard holds a rich candidate. Never throws.
    /// Any status other than <see cref="RichReadStatus.Rich"/> and <see cref="RichReadStatus.Busy"/>
    /// means the caller should capture the clipboard the legacy way.
    /// </summary>
    public static unsafe RichReadStatus TryRead(out RichPayload? payload, int maxHtmlBytes = MaxHtmlBytes)
    {
        payload = null;
        try
        {
            var htmlFormat = HtmlFormat();
            if (htmlFormat == 0 || !IsClipboardFormatAvailable(htmlFormat)) return RichReadStatus.NotRich;

            // "Copy image" in a browser puts a bitmap plus a one-tag HTML snippet pointing at the
            // remote URL, and no text. The bitmap is the durable form: leave it to the image path.
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT) &&
                (IsClipboardFormatAvailable(CF_DIB) || IsClipboardFormatAvailable(CF_BITMAP) ||
                 IsClipboardFormatAvailable(RegisterClipboardFormatW("PNG"))))
                return RichReadStatus.NotRich;

            if (!OpenClipboard(IntPtr.Zero)) return RichReadStatus.Busy;
            try
            {
                var sequence = GetClipboardSequenceNumber();

                var handle = GetClipboardData(htmlFormat);
                if (handle == IntPtr.Zero) return RichReadStatus.NotRich;

                byte[] html;
                int    images;
                var size = GlobalSize(handle).ToUInt64();
                if (size == 0) return RichReadStatus.NotRich;
                if (size > int.MaxValue) return RichReadStatus.TooLarge;
                var source = GlobalLock(handle);
                if (source == IntPtr.Zero) return RichReadStatus.Failed;
                try
                {
                    // The payload is a NUL-terminated string; the block may be larger than it.
                    var block = new ReadOnlySpan<byte>((void*)source, (int)size);
                    var end   = block.IndexOf((byte)0);
                    if (end >= 0) block = block[..end];
                    if (block.Length > maxHtmlBytes) return RichReadStatus.TooLarge;
                    images = CountImages(block);
                    if (images == 0) return RichReadStatus.NotRich;
                    html = block.ToArray();
                }
                finally { GlobalUnlock(handle); }

                payload = new RichPayload(html, ReadUnicodeText(), images, sequence);
                return RichReadStatus.Rich;
            }
            finally { CloseClipboard(); }
        }
        catch (Exception ex)
        {
            payload = null;
            TraceLog.Write($"RichClipboard.TryRead FAILED: {ex.GetType().Name}: {ex.Message}");
            return RichReadStatus.Failed;
        }
    }

    // Requires the clipboard to be open on this thread.
    private static unsafe string ReadUnicodeText()
    {
        var handle = GetClipboardData(CF_UNICODETEXT);
        if (handle == IntPtr.Zero) return "";
        var chars = GlobalSize(handle).ToUInt64() / 2;
        if (chars == 0 || chars > int.MaxValue) return "";
        var source = GlobalLock(handle);
        if (source == IntPtr.Zero) return "";
        try
        {
            var block = new ReadOnlySpan<char>((void*)source, (int)chars);
            var end   = block.IndexOf('\0');
            return new string(end >= 0 ? block[..end] : block);
        }
        finally { GlobalUnlock(handle); }
    }

    /// <summary>
    /// Replaces the clipboard contents with one item holding the HTML bytes (as "HTML Format",
    /// NUL-terminated) and the text (as CF_UNICODETEXT). Returns the clipboard sequence number
    /// after the write, so the caller can recognise its own write when notified of it.
    /// Throws when the clipboard cannot be written; in that case it is left as it was unless the
    /// failure happened after it was emptied.
    /// </summary>
    public static unsafe uint Write(ReadOnlySpan<byte> html, string? text)
    {
        text ??= "";
        var htmlFormat = HtmlFormat();
        if (htmlFormat == 0) throw new InvalidOperationException("Windows refused the HTML clipboard format");

        // A NUL inside the payload would cut it short for every reader.
        var nul = html.IndexOf((byte)0);
        if (nul >= 0) html = html[..nul];

        // Allocate and fill both blocks first, so a failure leaves the clipboard untouched.
        var htmlBlock = IntPtr.Zero;
        var textBlock = IntPtr.Zero;
        try
        {
            htmlBlock = Allocate((ulong)html.Length + 1, out var htmlTarget);
            html.CopyTo(new Span<byte>((void*)htmlTarget, html.Length));
            ((byte*)htmlTarget)[html.Length] = 0;
            GlobalUnlock(htmlBlock);

            textBlock = Allocate(((ulong)text.Length + 1) * 2, out var textTarget);
            text.AsSpan().CopyTo(new Span<char>((void*)textTarget, text.Length));
            ((char*)textTarget)[text.Length] = '\0';
            GlobalUnlock(textBlock);

            if (!OpenWithRetry()) throw new InvalidOperationException("The clipboard is in use by another program");
            try
            {
                if (!EmptyClipboard())
                    throw new InvalidOperationException($"Could not clear the clipboard (error {Marshal.GetLastWin32Error()})");
                // On success the system owns the block; it must not be freed here.
                if (SetClipboardData(htmlFormat, htmlBlock) == IntPtr.Zero)
                    throw new InvalidOperationException($"Could not place HTML on the clipboard (error {Marshal.GetLastWin32Error()})");
                htmlBlock = IntPtr.Zero;
                if (SetClipboardData(CF_UNICODETEXT, textBlock) == IntPtr.Zero)
                    throw new InvalidOperationException($"Could not place text on the clipboard (error {Marshal.GetLastWin32Error()})");
                textBlock = IntPtr.Zero;
            }
            finally { CloseClipboard(); }

            // Closing the clipboard bumps the number again (synthesized text formats), so it is
            // read after the close.
            return GetClipboardSequenceNumber();
        }
        finally
        {
            if (htmlBlock != IntPtr.Zero) GlobalFree(htmlBlock);
            if (textBlock != IntPtr.Zero) GlobalFree(textBlock);
        }
    }

    /// <summary>
    /// Puts a stored rich clip back on the clipboard exactly as it was captured: its HTML bytes
    /// from the side file and the text captured with them. Returns the clipboard sequence number
    /// of the write, or null when the clip's HTML is no longer readable (nothing is written then,
    /// and the caller falls back to the plain text). Throws when the clipboard cannot be written.
    /// </summary>
    public static uint? Restore(DataService data, ClipEntry clip)
    {
        var html = data.ReadRichHtml(clip);
        if (html == null) return null;
        return Write(html, clip.Raw);
    }

    private static IntPtr Allocate(ulong bytes, out IntPtr locked)
    {
        var block = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
        if (block == IntPtr.Zero) throw new OutOfMemoryException("Not enough memory to place the clip on the clipboard");
        locked = GlobalLock(block);
        if (locked == IntPtr.Zero)
        {
            GlobalFree(block);
            throw new InvalidOperationException("Could not prepare the clipboard data");
        }
        return block;
    }

    // Another program (the paste target, another clipboard tool) may hold the clipboard briefly.
    private static bool OpenWithRetry()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(20);
        }
        return false;
    }
}
