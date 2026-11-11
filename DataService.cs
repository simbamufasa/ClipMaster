using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClipMaster;

public static class TraceLog
{
    /// <summary>Where the log goes. Tests point this away from the user's data directory.</summary>
    public static string LogFile { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clipmaster", "debug.log");

    public static void Write(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n";
            File.AppendAllText(LogFile, line);
        }
        catch { /* logging must never crash the app */ }
    }
}

public enum RichAddResult
{
    /// <summary>A new rich entry was stored.</summary>
    Added,
    /// <summary>The same content was already in history; that entry was moved to the top.</summary>
    Promoted,
    /// <summary>The HTML could not be stored. Nothing changed; capture the plain text instead.</summary>
    Failed,
}

public class DataService
{
    private readonly string _dataDir;
    private readonly string _dataFile;
    private readonly string _binFile;
    private readonly string _binTmp;
    private readonly string _imagesDir;
    private readonly string _richDir;

    private const string RichFolder = "rich";
    private readonly object _richLock = new();   // one rich capture at a time (hash → dedupe → write)

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // Background save queue. Serialization (which reads the in-memory model) happens on the
    // caller's thread; encryption + disk I/O happen on a single background worker. Rapid saves
    // coalesce — only the most recent snapshot is ever written.
    private readonly object _saveLock  = new();   // guards _pendingPlain / _writerTask
    private readonly object _writeLock = new();   // serializes physical file writes
    private byte[]? _pendingPlain;
    private Task?   _writerTask;

    public DataService(string? dataDir = null)
    {
        _dataDir   = dataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clipmaster");
        _dataFile  = Path.Combine(_dataDir, "data.json");
        _binFile   = Path.Combine(_dataDir, "data.bin");
        _binTmp    = Path.Combine(_dataDir, "data.bin.tmp");
        _imagesDir = Path.Combine(_dataDir, "images");
        _richDir   = Path.Combine(_dataDir, RichFolder);
    }

    public AppData Load()
    {
        Directory.CreateDirectory(_dataDir);

        // Clean up a stale .tmp from a previously interrupted save
        if (File.Exists(_binTmp))
        {
            try { File.Delete(_binTmp); }
            catch { /* best-effort */ }
        }

        // Normal path: encrypted binary exists
        if (File.Exists(_binFile))
        {
            try
            {
                var cipher = File.ReadAllBytes(_binFile);
                var plain  = System.Security.Cryptography.ProtectedData.Unprotect(
                    cipher, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                var json   = System.Text.Encoding.UTF8.GetString(plain);
                var data   = JsonSerializer.Deserialize<AppData>(json, JsonOpts) ?? new AppData();
                // Deferred cleanup: remove orphaned plaintext file if migration was interrupted
                if (File.Exists(_dataFile))
                {
                    try { File.Delete(_dataFile); }
                    catch { /* best-effort */ }
                }
                if (MigrateInlineRich(data)) Save(data);
                // Only after a successful load: an unreadable database must never cost the files.
                DeleteOrphanRichFiles(data);
                return data;
            }
            catch (Exception ex)
            {
                TraceLog.Write($"Load (bin) FAILED: {ex.GetType().Name}: {ex.Message}");
                return new AppData();
            }
        }

        // Migration path: legacy plaintext JSON exists
        if (File.Exists(_dataFile))
        {
            try
            {
                var json = File.ReadAllText(_dataFile);
                var data = JsonSerializer.Deserialize<AppData>(json, JsonOpts) ?? new AppData();
                MigrateInlineRich(data);
                Save(data);
                File.Delete(_dataFile);
                return data;
            }
            catch (Exception ex)
            {
                TraceLog.Write($"Load (migration) FAILED: {ex.GetType().Name}: {ex.Message}");
                return new AppData();
            }
        }

        return new AppData();
    }

    /// <summary>
    /// Synchronously serialize, encrypt, and write the model to disk. Blocks the caller for the
    /// full cost (serialize + DPAPI + I/O). Prefer <see cref="QueueSave"/> on hot paths; this is
    /// kept for startup migration, shutdown flush, and tests.
    /// </summary>
    public void Save(AppData data)
    {
        try
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(data, JsonOpts);
            WriteCipher(plain);
        }
        catch (Exception ex)
        {
            TraceLog.Write($"Save FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Serialize the model on the calling thread (cheap — reads the in-memory model once), then
    /// hand the bytes to a background worker that encrypts and writes. Rapid calls coalesce, so
    /// a burst of copies results in a single disk write of the latest state. The UI thread never
    /// pays for encryption or I/O.
    /// </summary>
    public void QueueSave(AppData data)
    {
        byte[] plain;
        try { plain = JsonSerializer.SerializeToUtf8Bytes(data, JsonOpts); }
        catch (Exception ex)
        {
            TraceLog.Write($"QueueSave serialize FAILED: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        lock (_saveLock)
        {
            _pendingPlain = plain;                       // coalesce — newest snapshot wins
            _writerTask ??= Task.Run(DrainWrites);       // start a worker only if none is running
        }
    }

    private void DrainWrites()
    {
        while (true)
        {
            byte[] plain;
            lock (_saveLock)
            {
                if (_pendingPlain == null) { _writerTask = null; return; }
                plain = _pendingPlain;
                _pendingPlain = null;
            }
            WriteCipher(plain);
        }
    }

    /// <summary>
    /// Block until any queued save has been written. Call on shutdown so the latest state is
    /// durable before the process exits.
    /// </summary>
    public void Flush()
    {
        Task? t;
        lock (_saveLock) { t = _writerTask; }
        try { t?.Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { TraceLog.Write($"Flush wait FAILED: {ex.GetType().Name}: {ex.Message}"); }

        // A save may have been queued after the worker exited; write it synchronously.
        byte[]? leftover;
        lock (_saveLock) { leftover = _pendingPlain; _pendingPlain = null; }
        if (leftover != null) WriteCipher(leftover);
    }

    private void WriteCipher(byte[] plain)
    {
        lock (_writeLock)   // one physical write at a time, regardless of caller
        {
            try
            {
                Directory.CreateDirectory(_dataDir);
                var cipher = System.Security.Cryptography.ProtectedData.Protect(
                    plain, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_binTmp, cipher);
                File.Move(_binTmp, _binFile, overwrite: true);
            }
            catch (Exception ex)
            {
                TraceLog.Write($"WriteCipher FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public void DeleteImageFile(string? relPath)
    {
        if (string.IsNullOrEmpty(relPath)) return;
        try
        {
            var abs = Path.Combine(_dataDir, relPath);
            if (File.Exists(abs)) File.Delete(abs);
        }
        catch (Exception ex) { TraceLog.Write($"DeleteImageFile failed: {ex.Message}"); }
    }

    // ── Rich clips ──────────────────────────────────────────────────────────
    //
    // A rich clip is a normal entry (plain text in Raw/Text, so search, masking and the list
    // work as for text) whose HTML lives in its own encrypted file, rich/<id>.bin. The HTML
    // can be tens of megabytes; keeping it out of the database keeps every save small.
    // The file is read only when the clip is pasted or copied, and deleted with the entry.
    //
    // Protection is the same as data.bin — DPAPI for the current Windows user — applied as an
    // envelope: DPAPI protects a random AES-256 key, and AES-GCM with that key encrypts (and
    // authenticates) the HTML. DPAPI alone handles about 15 MB/s (measured: 1.3 s for 20 MB,
    // 19 s for 128 MB, each way), which would freeze every paste of a large clip.
    //
    //   "CMR1" | int32 wrapped-key length | DPAPI(key) | 12-byte nonce | 16-byte tag | ciphertext

    private static readonly byte[] RichMagic = "CMR1"u8.ToArray();
    private const int RichNonceBytes = 12;
    private const int RichTagBytes   = 16;

    private static byte[] ProtectRich(byte[] html)
    {
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        try
        {
            var wrapped = System.Security.Cryptography.ProtectedData.Protect(
                key, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            var prefix = RichMagic.Length + sizeof(int) + wrapped.Length;
            var header = prefix + RichNonceBytes + RichTagBytes;
            var output = new byte[header + html.Length];

            RichMagic.CopyTo(output, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(RichMagic.Length), wrapped.Length);
            wrapped.CopyTo(output, RichMagic.Length + sizeof(int));
            var nonce = output.AsSpan(prefix, RichNonceBytes);
            System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);

            using var aes = new System.Security.Cryptography.AesGcm(key, RichTagBytes);
            aes.Encrypt(nonce, html, output.AsSpan(header), output.AsSpan(prefix + RichNonceBytes, RichTagBytes),
                        associatedData: output.AsSpan(0, prefix));
            return output;
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
    }

    // Throws when the file is not one of ours, was written for another user, or was altered.
    private static byte[] UnprotectRich(byte[] file)
    {
        var fixedPart = RichMagic.Length + sizeof(int);
        if (file.Length < fixedPart || !file.AsSpan(0, RichMagic.Length).SequenceEqual(RichMagic))
            throw new InvalidDataException("not a rich clip file");
        var wrappedLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(RichMagic.Length));
        var prefix = fixedPart + wrappedLength;
        var header = prefix + RichNonceBytes + RichTagBytes;
        if (wrappedLength <= 0 || wrappedLength > 4096 || file.Length < header)
            throw new InvalidDataException("damaged rich clip file");

        var key = System.Security.Cryptography.ProtectedData.Unprotect(
            file.AsSpan(fixedPart, wrappedLength).ToArray(), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        try
        {
            var html = new byte[file.Length - header];
            using var aes = new System.Security.Cryptography.AesGcm(key, RichTagBytes);
            aes.Decrypt(file.AsSpan(prefix, RichNonceBytes), file.AsSpan(header), file.AsSpan(prefix + RichNonceBytes, RichTagBytes),
                        html, associatedData: file.AsSpan(0, prefix));
            return html;
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
    }

    private string? ResolveRichPath(string? relPath)
    {
        if (string.IsNullOrEmpty(relPath)) return null;
        try
        {
            var root = Path.GetFullPath(_richDir) + Path.DirectorySeparatorChar;
            var abs  = Path.GetFullPath(Path.Combine(_dataDir, relPath));
            return abs.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? abs : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Background-thread safe: encrypts the HTML bytes for the current Windows user and writes
    /// them atomically to rich/&lt;id&gt;.bin. Does NOT touch the in-memory model. Returns null on failure.
    /// </summary>
    public (string Id, string RelPath, long Bytes)? WriteRichFile(byte[] html)
    {
        try
        {
            Directory.CreateDirectory(_richDir);

            var id      = $"rich_{Guid.NewGuid():N}";
            var relPath = $"{RichFolder}/{id}.bin";
            var absPath = Path.Combine(_richDir, $"{id}.bin");
            var tmpPath = absPath + ".tmp";

            var cipher = ProtectRich(html);
            try
            {
                File.WriteAllBytes(tmpPath, cipher);
                File.Move(tmpPath, absPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmpPath); } catch { /* best-effort */ }
                throw;
            }
            return (id, relPath, html.LongLength);
        }
        catch (Exception ex)
        {
            TraceLog.Write($"WriteRichFile FAILED: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The HTML bytes of a rich clip, read and decrypted on demand. Null when the clip has no
    /// stored HTML or it cannot be read; the caller then falls back to the clip's plain text.
    /// </summary>
    public byte[]? ReadRichHtml(ClipEntry clip)
    {
        if (!clip.IsRich) return null;
        try
        {
            var abs = ResolveRichPath(clip.RichPath);
            if (abs != null && File.Exists(abs))
            {
                return UnprotectRich(File.ReadAllBytes(abs));
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write($"ReadRichHtml FAILED: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        // An entry from the first preview builds whose migration to a side file failed.
        return clip.RichHtml != null ? System.Text.Encoding.UTF8.GetBytes(clip.RichHtml) : null;
    }

    public void DeleteRichFile(string? relPath)
    {
        try
        {
            var abs = ResolveRichPath(relPath);
            if (abs != null && File.Exists(abs)) File.Delete(abs);
        }
        catch (Exception ex) { TraceLog.Write($"DeleteRichFile failed: {ex.Message}"); }
    }

    /// <summary>Deletes whatever files an entry owns. Call when the entry leaves the history.</summary>
    public void DeleteClipFiles(ClipEntry clip)
    {
        if (clip.IsImage) DeleteImageFile(clip.ImagePath);
        if (clip.IsRich)  DeleteRichFile(clip.RichPath);
    }

    /// <summary>
    /// Keeps every pinned rich clip and the newest MaxRichHistory unpinned ones, like
    /// <see cref="PruneImageHistory"/> does for images. Rich clips are large, so they get their
    /// own (smaller) limit on top of the general history limit.
    /// </summary>
    public void PruneRichHistory(AppData data)
    {
        var max  = Math.Max(1, data.Settings.MaxRichHistory);
        var kept = data.Clips.Where(c => c.IsRich && !c.Pinned).Take(max).Select(c => c.Id).ToHashSet();

        var toDelete = data.Clips.Where(c => c.IsRich && !c.Pinned && !kept.Contains(c.Id)).ToList();
        foreach (var old in toDelete)
        {
            data.Clips.Remove(old);
            DeleteRichFile(old.RichPath);
        }
    }

    /// <summary>
    /// The general history limit: every pinned clip, then the newest MaxHistory unpinned ones.
    /// Rich clips that fall off the end lose their side file.
    /// </summary>
    private void TrimHistory(AppData data)
    {
        var max      = data.Settings.MaxHistory;
        var pinned   = data.Clips.Where(c => c.Pinned).ToList();
        var unpinned = data.Clips.Where(c => !c.Pinned).ToList();
        foreach (var dropped in unpinned.Skip(Math.Max(0, max)).Where(c => c.IsRich))
            DeleteRichFile(dropped.RichPath);
        data.Clips = [.. pinned, .. unpinned.Take(max)];
    }

    /// <summary>
    /// Stores a captured rich item. The same content (HTML bytes and text) already in history is
    /// promoted instead: moved to the top with its copy count incremented, like a repeated text
    /// copy. Auto-transform rules are never applied; the plain text only decides masking.
    ///
    /// Hashing, encryption and the file write run on the calling thread, which may be a
    /// background thread; every read or change of the model goes through <paramref name="onModel"/>
    /// (pass a dispatcher invoke when calling from a background thread).
    /// </summary>
    public RichAddResult AddRichClip(AppData data, RichPayload payload, Action<Action>? onModel = null)
    {
        onModel ??= action => action();
        lock (_richLock)
        {
            var hash = RichClipboard.ContentHash(payload.Html, payload.Text);

            var promoted = false;
            onModel(() => promoted = PromoteRichByHash(data, hash));
            if (promoted) return RichAddResult.Promoted;

            var written = WriteRichFile(payload.Html);
            if (written == null) return RichAddResult.Failed;

            onModel(() =>
            {
                data.Clips.Insert(0, new ClipEntry
                {
                    Id          = written.Value.Id,
                    Raw         = payload.Text,
                    Text        = payload.Text,
                    IsRich      = true,
                    RichPath    = written.Value.RelPath,
                    RichBytes   = written.Value.Bytes,
                    RichImages  = payload.ImageCount,
                    RichHash    = hash,
                    CopiedAt    = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    CopyCount   = 1,
                    IsSensitive = data.Settings.MaskPasswords && LooksLikePassword(payload.Text),
                });
                PruneRichHistory(data);
                TrimHistory(data);
                QueueSave(data);
            });
            return RichAddResult.Added;
        }
    }

    private bool PromoteRichByHash(AppData data, string hash)
    {
        var idx = data.Clips.FindIndex(c => c.IsRich && c.RichHash == hash);
        if (idx < 0) return false;
        var clip = data.Clips[idx];
        // The entry is only as good as its file. If the file is gone, store the content again.
        var abs = ResolveRichPath(clip.RichPath);
        if (abs == null || !File.Exists(abs)) return false;

        data.Clips.RemoveAt(idx);
        clip.CopiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        clip.CopyCount++;
        data.Clips.Insert(0, clip);
        QueueSave(data);
        return true;
    }

    /// <summary>
    /// Moves HTML that the first preview builds stored inline in the database into side files.
    /// Returns true when the model changed. An entry whose file cannot be written keeps its
    /// inline HTML and still pastes.
    /// </summary>
    private bool MigrateInlineRich(AppData data)
    {
        var changed = false;
        foreach (var clip in data.Clips.Where(c => c.RichHtml != null))
        {
            if (!clip.IsRich) { clip.RichHtml = null; changed = true; continue; }
            // WPF decoded the clipboard bytes as UTF-8 up to the NUL; encoding gives them back.
            var html    = System.Text.Encoding.UTF8.GetBytes(clip.RichHtml!);
            var written = WriteRichFile(html);
            if (written == null) continue;
            clip.RichPath   = written.Value.RelPath;
            clip.RichBytes  = written.Value.Bytes;
            clip.RichImages = RichClipboard.CountImages(html);
            clip.RichHash   = RichClipboard.ContentHash(html, clip.Raw);
            clip.RichHtml   = null;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Removes rich files no entry refers to: left by a crash between writing the file and saving
    /// the database, or by an older ClipMaster that rewrote the database without the rich fields.
    /// </summary>
    private void DeleteOrphanRichFiles(AppData data)
    {
        try
        {
            if (!Directory.Exists(_richDir)) return;
            var referenced = data.Clips
                .Select(c => ResolveRichPath(c.RichPath))
                .Where(p => p != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(_richDir, "rich_*"))
            {
                var ext = Path.GetExtension(file);
                if (ext != ".bin" && ext != ".tmp") continue;
                if (referenced.Contains(Path.GetFullPath(file))) continue;
                try { File.Delete(file); }
                catch { /* best-effort */ }
            }
        }
        catch (Exception ex) { TraceLog.Write($"DeleteOrphanRichFiles failed: {ex.Message}"); }
    }

    public void PruneImageHistory(AppData data)
    {
        var max          = data.Settings.MaxImageHistory;
        var pinnedImgs   = data.Clips.Where(c => c.IsImage && c.Pinned).ToList();
        var unpinnedImgs = data.Clips.Where(c => c.IsImage && !c.Pinned).Take(max).ToList();
        var kept         = pinnedImgs.Concat(unpinnedImgs).Select(c => c.Id).ToHashSet();

        var toDelete = data.Clips.Where(c => c.IsImage && !kept.Contains(c.Id)).ToList();
        foreach (var old in toDelete)
        {
            data.Clips.Remove(old);
            DeleteImageFile(old.ImagePath);
        }
    }

    /// <summary>
    /// Background-thread safe: atomically writes the encoded PNG to disk. Does NOT touch the
    /// in-memory model. Returns the clip id, the path relative to the data dir, and the byte
    /// size, or null on failure. Pair with <see cref="AddImageEntry"/> on the UI thread.
    /// </summary>
    public (string Id, string RelPath, long Bytes)? WriteImageFile(byte[] pngBytes)
    {
        try
        {
            Directory.CreateDirectory(_imagesDir);

            var id      = $"img_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Random.Shared.Next(10000, 99999)}";
            var relPath = $"images/{id}.png";
            var absPath = Path.Combine(_imagesDir, $"{id}.png");
            var tmpPath = absPath + ".tmp";

            // Atomic write: write to .tmp then rename
            File.WriteAllBytes(tmpPath, pngBytes);
            File.Move(tmpPath, absPath, overwrite: true);

            return (id, relPath, pngBytes.LongLength);
        }
        catch (Exception ex)
        {
            TraceLog.Write($"WriteImageFile FAILED: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// UI-thread only: inserts an already-written image file into the model, prunes image
    /// history, and queues a save. Keeping model mutation on the UI thread avoids racing the
    /// list against the renderer.
    /// </summary>
    public ClipEntry AddImageEntry(AppData data, string id, string relPath, long bytes, int width, int height)
    {
        var entry = new ClipEntry
        {
            Id          = id,
            IsImage     = true,
            ImagePath   = relPath,
            ImageWidth  = width,
            ImageHeight = height,
            ImageBytes  = bytes,
            CopiedAt    = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CopyCount   = 1,
        };
        data.Clips.Insert(0, entry);

        // Prune image clips independently from text clips
        PruneImageHistory(data);

        QueueSave(data);
        return entry;
    }

    public static bool LooksLikePassword(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 8 || text.Length > 512) return false;
        if (text.Contains('\n') || text.Contains(' ')) return false;
        var t = text.Trim();
        bool hasUpper = false, hasLower = false, hasDigit = false, hasSymbol = false;
        foreach (var c in t)
        {
            if (char.IsUpper(c))      hasUpper = true;
            else if (char.IsLower(c)) hasLower = true;
            else if (char.IsDigit(c)) hasDigit = true;
            else                      hasSymbol = true;
        }
        if (t.Length >= 8 && hasUpper && hasLower && hasDigit && hasSymbol) return true;
        if (Regex.IsMatch(t, @"^[A-Za-z0-9+/]{20,}={0,2}$", RegexOptions.None, RegexTimeout)) return true;
        if (Regex.IsMatch(t, @"(?:password|passwd|pwd|secret|token|key|auth)[\s:=]+\S+", RegexOptions.IgnoreCase, RegexTimeout)) return true;
        if (Regex.IsMatch(t, @"^[0-9a-f]{32,}$", RegexOptions.IgnoreCase, RegexTimeout)) return true;
        return false;
    }

    private static RegexOptions ParseFlags(string flags)
    {
        var opts = RegexOptions.None;
        if (flags.Contains('i')) opts |= RegexOptions.IgnoreCase;
        if (flags.Contains('m')) opts |= RegexOptions.Multiline;
        return opts;
    }

    public (string Text, List<string> Applied) ApplyAutoRules(string text, List<Rule> rules)
    {
        var applied = new List<string>();
        var result  = text;
        foreach (var rule in rules.Where(r => r.Enabled && r.Mode == "auto"))
        {
            try
            {
                var rx   = new Regex(rule.Pattern, ParseFlags(rule.Flags), RegexTimeout);
                var count = rule.Flags.Contains('g') ? -1 : 1;
                var next = count == -1
                    ? rx.Replace(result, rule.Replacement ?? "")
                    : rx.Replace(result, rule.Replacement ?? "", count);
                if (next != result) { applied.Add(rule.Name); result = next; }
            }
            catch (Exception ex)
            {
                TraceLog.Write($"Rule '{rule.Name}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return (result, applied);
    }

    public string ApplyRule(string text, Rule rule)
    {
        var rx    = new Regex(rule.Pattern, ParseFlags(rule.Flags), RegexTimeout);
        var count = rule.Flags.Contains('g') ? -1 : 1;
        return count == -1
            ? rx.Replace(text, rule.Replacement ?? "")
            : rx.Replace(text, rule.Replacement ?? "", count);
    }

    public (string FinalText, bool WasTransformed) AddClip(AppData data, string rawText)
    {
        var isSensitive = data.Settings.MaskPasswords && LooksLikePassword(rawText);
        var finalText   = rawText;
        var applied     = new List<string>();

        if (!isSensitive && data.Settings.AutoApplyRules)
            (finalText, applied) = ApplyAutoRules(rawText, data.Rules);

        // A rich clip with the same plain text is a different item: never merge text into it.
        var existing = data.Clips.FindIndex(c => !c.IsRich && c.Raw == rawText);
        if (existing >= 0)
        {
            var clip = data.Clips[existing];
            data.Clips.RemoveAt(existing);
            clip.CopiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            clip.CopyCount++;
            clip.Text = finalText;
            clip.Language = (!isSensitive && data.Settings.HighlightCode) ? LanguageDetector.Detect(finalText) : "";
            if (applied.Count > 0) clip.AppliedRules = applied;
            data.Clips.Insert(0, clip);
        }
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
                Language     = (!isSensitive && data.Settings.HighlightCode) ? LanguageDetector.Detect(finalText) : "",
            });
        }

        TrimHistory(data);

        QueueSave(data);
        return (finalText, finalText != rawText);
    }

    public List<ClipEntry> PromoteClip(AppData data, string clipId)
    {
        var idx = data.Clips.FindIndex(c => c.Id == clipId);
        if (idx < 0) return data.Clips;
        var clip = data.Clips[idx];
        data.Clips.RemoveAt(idx);
        clip.CopiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        clip.CopyCount++;
        data.Clips.Insert(0, clip);
        QueueSave(data);
        return data.Clips;
    }
}
