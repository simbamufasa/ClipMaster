using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClipMaster;

public static class TraceLog
{
    private static readonly string LogFile = Path.Combine(
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

public class DataService
{
    private readonly string _dataDir;
    private readonly string _dataFile;
    private readonly string _binFile;
    private readonly string _binTmp;
    private readonly string _imagesDir;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public DataService(string? dataDir = null)
    {
        _dataDir   = dataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clipmaster");
        _dataFile  = Path.Combine(_dataDir, "data.json");
        _binFile   = Path.Combine(_dataDir, "data.bin");
        _binTmp    = Path.Combine(_dataDir, "data.bin.tmp");
        _imagesDir = Path.Combine(_dataDir, "images");
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

    public void Save(AppData data)
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            var json   = JsonSerializer.Serialize(data, JsonOpts);
            var plain  = System.Text.Encoding.UTF8.GetBytes(json);
            var cipher = System.Security.Cryptography.ProtectedData.Protect(
                plain, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_binTmp, cipher);
            File.Move(_binTmp, _binFile, overwrite: true);
        }
        catch (Exception ex)
        {
            TraceLog.Write($"Save FAILED: {ex.GetType().Name}: {ex.Message}");
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

    public ClipEntry? AddImageClip(AppData data, byte[] pngBytes, int width, int height)
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

            var entry = new ClipEntry
            {
                Id          = id,
                IsImage     = true,
                ImagePath   = relPath,
                ImageWidth  = width,
                ImageHeight = height,
                ImageBytes  = pngBytes.LongLength,
                CopiedAt    = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CopyCount   = 1,
            };
            data.Clips.Insert(0, entry);

            // Prune image clips independently from text clips
            PruneImageHistory(data);

            Save(data);
            return entry;
        }
        catch (Exception ex)
        {
            TraceLog.Write($"AddImageClip FAILED: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
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

        var existing = data.Clips.FindIndex(c => c.Raw == rawText);
        if (existing >= 0)
        {
            var clip = data.Clips[existing];
            data.Clips.RemoveAt(existing);
            clip.CopiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            clip.CopyCount++;
            clip.Text = finalText;
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
            });
        }

        var max      = data.Settings.MaxHistory;
        var pinned   = data.Clips.Where(c => c.Pinned).ToList();
        var unpinned = data.Clips.Where(c => !c.Pinned).Take(max).ToList();
        data.Clips   = [.. pinned, .. unpinned];

        Save(data);
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
        Save(data);
        return data.Clips;
    }
}
