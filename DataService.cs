using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClipMaster;

public class DataService
{
    private static readonly string DataDir  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clipmaster");
    private static readonly string DataFile = Path.Combine(DataDir, "data.json");

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

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
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = DataFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOpts));
            File.Move(tmp, DataFile, overwrite: true);
        }
        catch { /* disk full or locked */ }
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
        if (Regex.IsMatch(t, @"^[A-Za-z0-9+/]{20,}={0,2}$")) return true;
        if (Regex.IsMatch(t, @"(?:password|passwd|pwd|secret|token|key|auth)[\s:=]+\S+", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(t, @"^[0-9a-f]{32,}$", RegexOptions.IgnoreCase)) return true;
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
            catch { /* invalid regex or timeout — skip */ }
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
