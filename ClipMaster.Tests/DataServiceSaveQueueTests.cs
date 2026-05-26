using System.IO;
using ClipMaster;
using Xunit;

namespace ClipMaster.Tests;

public class DataServiceSaveQueueTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public DataServiceSaveQueueTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose()              => Directory.Delete(_tempDir, recursive: true);

    private DataService Svc() => new(_tempDir);

    [Fact]
    public void QueueSave_ThenFlush_RoundTripsData()
    {
        var svc  = Svc();
        var data = new AppData();
        data.Clips.Add(new ClipEntry { Id = "q1", Raw = "queued", Text = "queued" });

        svc.QueueSave(data);
        svc.Flush();

        Assert.True(File.Exists(Path.Combine(_tempDir, "data.bin")), "data.bin should exist after flush");

        var loaded = Svc().Load();
        Assert.Single(loaded.Clips);
        Assert.Equal("queued", loaded.Clips[0].Raw);
    }

    [Fact]
    public void QueueSave_Coalesces_LatestSnapshotWins()
    {
        var svc = Svc();

        // Fire a rapid burst. Coalescing may skip intermediate snapshots, but the LAST one
        // queued must always be the state that ends up on disk.
        for (int i = 0; i < 200; i++)
        {
            var data = new AppData();
            data.Clips.Add(new ClipEntry { Id = "c", Raw = i.ToString(), Text = i.ToString() });
            svc.QueueSave(data);
        }
        svc.Flush();

        var loaded = Svc().Load();
        Assert.Single(loaded.Clips);
        Assert.Equal("199", loaded.Clips[0].Raw);
    }

    [Fact]
    public void Flush_WithNothingQueued_DoesNotThrow()
    {
        var svc = Svc();
        svc.Flush();   // no save ever queued
        Assert.False(File.Exists(Path.Combine(_tempDir, "data.bin")));
    }

    [Fact]
    public void QueueSave_WritesEncryptedBin_NotPlaintextJson()
    {
        var svc  = Svc();
        var data = new AppData();
        data.Clips.Add(new ClipEntry { Id = "s1", Raw = "secret-marker-text", Text = "secret-marker-text" });

        svc.QueueSave(data);
        svc.Flush();

        var bytes = File.ReadAllBytes(Path.Combine(_tempDir, "data.bin"));
        var asText = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("secret-marker-text", asText);   // DPAPI-encrypted, not plaintext
        Assert.False(File.Exists(Path.Combine(_tempDir, "data.json")));
    }
}
