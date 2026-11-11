using System.IO;
using System.Text;
using System.Text.Json;
using ClipMaster;
using Xunit;

namespace ClipMaster.Tests;

public class RichClipStorageTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public RichClipStorageTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose()
    {
        foreach (var svc in _services) svc.Flush();   // no background save may outlive the folder
        Directory.Delete(_tempDir, recursive: true);
    }

    private readonly List<DataService> _services = [];
    private DataService Svc()
    {
        var svc = new DataService(_tempDir);
        _services.Add(svc);
        return svc;
    }
    private string RichDir => Path.Combine(_tempDir, "rich");
    private string[] RichFiles() => Directory.Exists(RichDir) ? Directory.GetFiles(RichDir) : [];

    private static RichPayload Note(string marker, string text = "note text") =>
        NotesPayload.Payload($"<h1>{marker}</h1><p>one</p>{NotesPayload.Image(400)}<p>two</p>{NotesPayload.Image(400)}", text);

    // ── C: storage ──────────────────────────────────────────────────────────

    [Fact]
    public void AddRichClip_KeepsTheHtmlOutOfTheDatabase_InAnEncryptedSideFile()
    {
        var svc     = Svc();
        var data    = new AppData();
        var payload = NotesPayload.Payload($"<p>UNIQUE-MARKER-HTML</p>{NotesPayload.Image(200_000)}", "plain marker");

        Assert.Equal(RichAddResult.Added, svc.AddRichClip(data, payload));
        svc.Flush();

        var clip = Assert.Single(data.Clips);
        Assert.True(clip.IsRich);
        Assert.Null(clip.RichHtml);
        Assert.Equal($"rich/{clip.Id}.bin", clip.RichPath);
        Assert.Equal(payload.Html.LongLength, clip.RichBytes);
        Assert.Equal(1, clip.RichImages);
        Assert.Equal("plain marker", clip.Raw);
        Assert.Equal("plain marker", clip.Text);
        Assert.Equal(1, clip.CopyCount);

        // The database stays small: the HTML is not in it.
        var db = new FileInfo(Path.Combine(_tempDir, "data.bin"));
        Assert.True(db.Length < 20_000, $"data.bin is {db.Length} bytes");

        // The side file is encrypted for this Windows user, not readable HTML.
        var file  = Assert.Single(RichFiles());
        var bytes = File.ReadAllBytes(file);
        Assert.Equal(Path.Combine(RichDir, clip.Id + ".bin"), file);
        Assert.DoesNotContain("UNIQUE-MARKER-HTML", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("StartFragment", Encoding.UTF8.GetString(bytes));

        Assert.Equal(payload.Html, svc.ReadRichHtml(clip));
    }

    [Fact]
    public void RichClip_SurvivesARestart_ByteForByte()
    {
        var payload = NotesPayload.Payload($"<h1>Review — café 😀 日本語</h1>{NotesPayload.Image(1000)}", "Review — café 😀 日本語");
        var first   = Svc();
        var data    = first.Load();
        first.AddRichClip(data, payload);
        first.Flush();

        var second = Svc();
        var loaded = second.Load();

        var clip = Assert.Single(loaded.Clips);
        Assert.True(clip.IsRich);
        Assert.Equal("Review — café 😀 日本語", clip.Raw);
        Assert.Equal(payload.Html, second.ReadRichHtml(clip));
        Assert.Single(RichFiles());   // loading does not disturb referenced files
    }

    [Fact]
    public void ReadRichHtml_ReturnsNull_WhenTheFileIsGoneOrDamaged()
    {
        var svc  = Svc();
        var data = new AppData();
        svc.AddRichClip(data, Note("a"));
        var clip = data.Clips[0];
        var file = Assert.Single(RichFiles());

        Assert.NotNull(svc.ReadRichHtml(clip));

        // One flipped bit anywhere — header or content — is detected, never pasted.
        var good = File.ReadAllBytes(file);
        foreach (var at in new[] { 0, 6, 20, good.Length / 2, good.Length - 1 })
        {
            var bad = (byte[])good.Clone();
            bad[at] ^= 0x01;
            File.WriteAllBytes(file, bad);
            Assert.Null(svc.ReadRichHtml(clip));
        }
        File.WriteAllBytes(file, good[..^1]);
        Assert.Null(svc.ReadRichHtml(clip));
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        Assert.Null(svc.ReadRichHtml(clip));
        File.WriteAllBytes(file, good);
        Assert.NotNull(svc.ReadRichHtml(clip));

        File.Delete(file);
        Assert.Null(svc.ReadRichHtml(clip));

        Assert.Null(svc.ReadRichHtml(new ClipEntry { Id = "t", Raw = "text", Text = "text" }));
    }

    [Fact]
    public void RichPaths_OutsideTheRichFolder_AreNeverReadOrDeleted()
    {
        var svc     = Svc();
        var outside = Path.Combine(_tempDir, "data.bin");
        File.WriteAllBytes(outside, [9, 9, 9]);
        var forged = new ClipEntry { Id = "x", IsRich = true, RichPath = "rich/../data.bin" };

        Assert.Null(svc.ReadRichHtml(forged));
        svc.DeleteClipFiles(forged);
        svc.DeleteRichFile(outside);

        Assert.True(File.Exists(outside));
    }

    // ── C: lifecycle ────────────────────────────────────────────────────────

    [Fact]
    public void DeleteClipFiles_RemovesTheSideFile()
    {
        var svc  = Svc();
        var data = new AppData();
        svc.AddRichClip(data, Note("a"));
        svc.AddRichClip(data, Note("b"));
        Assert.Equal(2, RichFiles().Length);

        var gone = data.Clips[0];
        data.Clips.Remove(gone);
        svc.DeleteClipFiles(gone);

        var left = Assert.Single(RichFiles());
        Assert.Equal(data.Clips[0].Id + ".bin", Path.GetFileName(left));
    }

    [Fact]
    public void DeleteClipFiles_RemovesAnImageClipsFileToo()
    {
        var svc     = Svc();
        var data    = new AppData();
        var written = svc.WriteImageFile([1, 2, 3])!.Value;
        var entry   = svc.AddImageEntry(data, written.Id, written.RelPath, written.Bytes, 1, 1);

        svc.DeleteClipFiles(entry);

        Assert.Empty(Directory.GetFiles(Path.Combine(_tempDir, "images")));
    }

    [Fact]
    public void Load_MovesInlineHtmlFromTheFirstPreview_IntoASideFile()
    {
        // What the branch wrote before this change: the HTML inline in the database.
        var html = Encoding.UTF8.GetString(NotesPayload.CfHtml($"<p>café — 日本</p>{NotesPayload.Image(300)}{NotesPayload.Image(300)}"));
        var json = JsonSerializer.Serialize(new
        {
            clips = new object[]
            {
                new { id = "rich_legacy", raw = "café — 日本", text = "café — 日本", copiedAt = 5L, copyCount = 3, pinned = true, isRich = true, richHtml = html },
                new { id = "clip_1", raw = "plain", text = "plain", copiedAt = 4L, copyCount = 1, richHtml = (string?)null },
            },
        });
        var plain = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(Path.Combine(_tempDir, "data.bin"), System.Security.Cryptography.ProtectedData.Protect(
            plain, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));

        var svc  = Svc();
        var data = svc.Load();

        var clip = data.Clips.Single(c => c.Id == "rich_legacy");
        Assert.True(clip.IsRich);
        Assert.Null(clip.RichHtml);
        Assert.NotNull(clip.RichPath);
        Assert.Equal(2, clip.RichImages);
        Assert.Equal(3, clip.CopyCount);
        Assert.True(clip.Pinned);
        Assert.Equal(Encoding.UTF8.GetBytes(html), svc.ReadRichHtml(clip));
        Assert.Equal("plain", data.Clips.Single(c => c.Id == "clip_1").Raw);

        // The migration is saved: a second start finds the side file, not inline HTML.
        var again     = Svc();
        var reloaded  = again.Load();
        var migrated  = reloaded.Clips.Single(c => c.Id == "rich_legacy");
        Assert.Null(migrated.RichHtml);
        Assert.Equal(clip.RichPath, migrated.RichPath);
        Assert.Equal(Encoding.UTF8.GetBytes(html), again.ReadRichHtml(migrated));
        Assert.Single(RichFiles());

        // And a new copy of the same content is recognised as the migrated entry.
        var samePayload = new RichPayload(Encoding.UTF8.GetBytes(html), "café — 日本", 2);
        Assert.Equal(RichAddResult.Promoted, again.AddRichClip(reloaded, samePayload));
    }

    [Fact]
    public void Load_RemovesRichFilesNoEntryRefersTo_ButOnlyThose()
    {
        var svc  = Svc();
        var data = new AppData();
        svc.AddRichClip(data, Note("kept"));
        svc.Flush();
        var kept = Assert.Single(RichFiles());

        var orphan    = Path.Combine(RichDir, "rich_0123456789abcdef0123456789abcdef.bin");
        var staleTmp  = Path.Combine(RichDir, "rich_0123456789abcdef0123456789abcdef.bin.tmp");
        var unrelated = Path.Combine(RichDir, "notes.txt");
        File.WriteAllBytes(orphan, [1]);
        File.WriteAllBytes(staleTmp, [1]);
        File.WriteAllBytes(unrelated, [1]);

        Svc().Load();

        Assert.True(File.Exists(kept));
        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(staleTmp));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void Load_KeepsEveryRichFile_WhenTheDatabaseCannotBeRead()
    {
        var svc  = Svc();
        var data = new AppData();
        svc.AddRichClip(data, Note("kept"));
        svc.Flush();
        var kept = Assert.Single(RichFiles());

        File.WriteAllBytes(Path.Combine(_tempDir, "data.bin"), [0xDE, 0xAD]);   // unreadable
        var loaded = Svc().Load();

        Assert.Empty(loaded.Clips);
        Assert.True(File.Exists(kept), "an unreadable database must not cost the rich files");
    }

    // ── D: de-duplication and trimming ──────────────────────────────────────

    [Fact]
    public void CopyingTheSameRichContentAgain_PromotesTheExistingEntry()
    {
        var svc  = Svc();
        var data = new AppData();
        svc.AddRichClip(data, Note("first"));
        var firstId = data.Clips[0].Id;
        svc.AddClip(data, "some text in between");
        svc.AddRichClip(data, Note("second"));

        Assert.Equal(RichAddResult.Promoted, svc.AddRichClip(data, Note("first")));

        Assert.Equal(3, data.Clips.Count);
        Assert.Equal(firstId, data.Clips[0].Id);
        Assert.Equal(2, data.Clips[0].CopyCount);
        Assert.Equal(2, RichFiles().Length);

        // Different text with the same HTML is a different item.
        Assert.Equal(RichAddResult.Added, svc.AddRichClip(data, Note("first", text: "other text")));
        Assert.Equal(4, data.Clips.Count);
    }

    [Fact]
    public void ARepeatedCopy_IsStoredAgain_WhenTheExistingEntryLostItsFile()
    {
        var svc  = Svc();
        var data = new AppData();
        svc.AddRichClip(data, Note("a"));
        File.Delete(Assert.Single(RichFiles()));

        Assert.Equal(RichAddResult.Added, svc.AddRichClip(data, Note("a")));

        Assert.NotNull(svc.ReadRichHtml(data.Clips[0]));
    }

    [Fact]
    public void RichHistory_KeepsPinnedClips_AndTheNewestUnpinnedOnes()
    {
        var svc  = Svc();
        var data = new AppData();
        data.Settings.MaxRichHistory = 2;

        svc.AddRichClip(data, Note("pinned"));
        data.Clips[0].Pinned = true;
        svc.AddRichClip(data, Note("old"));
        svc.AddRichClip(data, Note("mid"));
        svc.AddClip(data, "a text clip is not counted");
        svc.AddRichClip(data, Note("new"));

        var rich = data.Clips.Where(c => c.IsRich).ToList();
        Assert.Equal(3, rich.Count);                       // pinned + 2 newest
        Assert.Contains(rich, c => c.Pinned);
        Assert.Equal(3, RichFiles().Length);               // the trimmed clip's file is gone
        Assert.All(rich, c => Assert.NotNull(svc.ReadRichHtml(c)));
        Assert.Contains(data.Clips, c => !c.IsRich && c.Raw == "a text clip is not counted");

        // Lowering the limit later (settings) trims again.
        data.Settings.MaxRichHistory = 1;
        svc.PruneRichHistory(data);
        Assert.Equal(2, data.Clips.Count(c => c.IsRich));
        Assert.Equal(2, RichFiles().Length);
    }

    [Fact]
    public void TheGeneralHistoryLimit_AppliesToRichClips_AndDeletesTheirFiles()
    {
        var svc  = Svc();
        var data = new AppData();
        data.Settings.MaxHistory = 3;

        svc.AddRichClip(data, Note("pinned"));
        data.Clips[0].Pinned = true;
        svc.AddRichClip(data, Note("will fall off"));
        Assert.Equal(2, RichFiles().Length);

        // Text copies push the unpinned rich clip past the limit.
        svc.AddClip(data, "t1");
        svc.AddClip(data, "t2");
        svc.AddClip(data, "t3");

        Assert.Equal(4, data.Clips.Count);                 // pinned rich + 3 unpinned texts
        var rich = Assert.Single(data.Clips, c => c.IsRich);
        Assert.True(rich.Pinned);
        var file = Assert.Single(RichFiles());
        Assert.Equal(rich.Id + ".bin", Path.GetFileName(file));

        // And a rich copy trims old text the same way.
        svc.AddRichClip(data, Note("newest"));
        Assert.Equal(4, data.Clips.Count);
        Assert.Equal(3, data.Clips.Count(c => !c.Pinned));
        Assert.DoesNotContain(data.Clips, c => c.Raw == "t1");
    }

    // ── E: rules, masking ───────────────────────────────────────────────────

    [Fact]
    public void AutoRules_AreNeverAppliedToRichClips()
    {
        var svc  = Svc();
        var data = new AppData();
        data.Settings.AutoApplyRules = true;
        data.Rules.Add(new Rule { Id = "r", Name = "shout", Pattern = "note", Flags = "g", Replacement = "NOTE", Mode = "auto", Enabled = true });

        var payload = Note("note", text: "a note about notes");
        svc.AddRichClip(data, payload);

        var clip = data.Clips[0];
        Assert.Equal("a note about notes", clip.Text);
        Assert.Equal("a note about notes", clip.Raw);
        Assert.Empty(clip.AppliedRules);
        Assert.Equal(payload.Html, svc.ReadRichHtml(clip));

        // The same rule does rewrite an ordinary text copy.
        var (final, transformed) = svc.AddClip(data, "a note");
        Assert.True(transformed);
        Assert.Equal("a NOTE", final);
    }

    [Fact]
    public void ATextCopy_IsNeverMergedIntoARichClipWithTheSameText()
    {
        var svc  = Svc();
        var data = new AppData();
        data.Rules.Add(new Rule { Id = "r", Name = "shout", Pattern = "same", Flags = "g", Replacement = "SAME", Mode = "auto", Enabled = true });
        svc.AddRichClip(data, Note("x", text: "same words"));

        svc.AddClip(data, "same words");

        Assert.Equal(2, data.Clips.Count);
        var rich = data.Clips.Single(c => c.IsRich);
        Assert.Equal("same words", rich.Text);
        Assert.Equal(1, rich.CopyCount);
        Assert.Equal("SAME words", data.Clips.Single(c => !c.IsRich).Text);
    }

    [Fact]
    public void Masking_IsDecidedByThePlainText()
    {
        var svc  = Svc();
        var data = new AppData();

        svc.AddRichClip(data, Note("secret", text: "Tr0ub4dor&3xYz"));
        Assert.True(data.Clips[0].IsSensitive);

        svc.AddRichClip(data, Note("ordinary", text: "an ordinary sentence"));
        Assert.False(data.Clips[0].IsSensitive);

        svc.AddRichClip(data, Note("images only", text: ""));
        Assert.False(data.Clips[0].IsSensitive);

        data.Settings.MaskPasswords = false;
        svc.AddRichClip(data, Note("unmasked", text: "Tr0ub4dor&3xYz!"));
        Assert.False(data.Clips[0].IsSensitive);
    }

    // ── B: nothing is lost ──────────────────────────────────────────────────

    [Fact]
    public void WhenTheHtmlCannotBeStored_NothingChanges_AndTheCallerIsTold()
    {
        // A file where the rich folder should be makes every write fail.
        File.WriteAllBytes(RichDir, [0]);
        var svc  = Svc();
        var data = new AppData();
        svc.AddClip(data, "already here");

        var result = svc.AddRichClip(data, Note("x", text: "the plain text"));

        Assert.Equal(RichAddResult.Failed, result);
        Assert.Single(data.Clips);

        // The caller (App.OnNewClipRich) then captures the plain text the legacy way.
        svc.AddClip(data, "the plain text");
        Assert.Equal("the plain text", data.Clips[0].Raw);
        Assert.False(data.Clips[0].IsRich);
    }

    [Fact]
    public void ModelChanges_GoThroughTheSuppliedDispatcher()
    {
        var svc   = Svc();
        var data  = new AppData();
        var calls = 0;

        svc.AddRichClip(data, Note("a"), action => { calls++; action(); });
        Assert.Equal(2, calls);    // look for a duplicate, then insert

        calls = 0;
        svc.AddRichClip(data, Note("a"), action => { calls++; action(); });
        Assert.Equal(1, calls);    // promoted in the first step
    }

    [Fact]
    public void OlderDatabases_WithoutRichFields_StillLoad_AndNewOnesStaySmall()
    {
        var svc  = Svc();
        var data = new AppData();
        data.Clips.Add(new ClipEntry { Id = "c1", Raw = "hello", Text = "hello" });
        svc.Save(data);

        var loaded = Svc().Load();
        Assert.False(loaded.Clips[0].IsRich);
        Assert.Null(loaded.Clips[0].RichPath);
        Assert.Equal(50, loaded.Settings.MaxRichHistory);

        // Text entries do not carry empty rich fields around.
        var json = JsonSerializer.Serialize(loaded);
        Assert.DoesNotContain("richPath", json);
        Assert.DoesNotContain("richHtml", json);
        Assert.DoesNotContain("richHash", json);
    }
}
