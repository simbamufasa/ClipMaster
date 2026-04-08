using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClipMaster;

public class DataService
{
    private static readonly string DataDir  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clipmaster");
    private static readonly string DataFile = Path.Combine(DataDir, "data.json");

    private static readonly Regex[] PasswordPatterns =
    [
        new(@"^(?=.*[A-Z])(?=.*[a-z])(?=.*\d)(?=.*[!@#$%^&*()_+\-=\[\]{};':""\\|,.<>\/?]).{8,}$"),
        new(@"^[A-Za-z0-9+/]{20,}={0,2}$"),
        new(@"(?:password|passwd|pwd|secret|token|key|auth)[\s:=]+\S+", RegexOptions.IgnoreCase),
        new(@"^[0-9a-f]{32,}$", RegexOptions.IgnoreCase),
    ];

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppData Load()
    {
        Directory.CreateDirectory(DataDir);
        if (!File.Exists(DataFile)) return new AppData();
        try
        {
            var json = File.ReadAllText(DataFile);
            return JsonSerializer.Deserialize<AppData>(json, JsonOpts) ?? new AppData();
        }
        catch { return new AppData(); }
    }

    public void Save(AppData data)
    {
        try { File.WriteAllText(DataFile, JsonSerializer.Serialize(data, JsonOpts)); }
        catch { /* disk full or locked */ }
    }

    public bool LooksLikePassword(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 8 || text.Length > 512) return false;
        if (text.Contains('\n') || text.Contains(' ')) return false;
        return PasswordPatterns.Any(p => p.IsMatch(text.Trim()));
    }

    public (string Text, List<string> Applied) ApplyAutoRules(string text, List<Rule> rules)
    {
        var applied = new List<string>();
        var result  = text;
        foreach (var rule in rules.Where(r => r.Enabled && r.Mode == "auto"))
        {
            try
            {
                var opts = RegexOptions.None;
                if (rule.Flags.Contains('i')) opts |= RegexOptions.IgnoreCase;
                if (rule.Flags.Contains('m')) opts |= RegexOptions.Multiline;
                var rx   = new Regex(rule.Pattern, opts);
                var next = rx.Replace(result, rule.Replacement ?? "");
                if (next != result) { applied.Add(rule.Name); result = next; }
            }
            catch { /* invalid regex — skip */ }
        }
        return (result, applied);
    }

    public string ApplyRule(string text, Rule rule)
    {
        var opts = RegexOptions.None;
        if (rule.Flags.Contains('i')) opts |= RegexOptions.IgnoreCase;
        if (rule.Flags.Contains('m')) opts |= RegexOptions.Multiline;
        return new Regex(rule.Pattern, opts).Replace(text, rule.Replacement ?? "");
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
