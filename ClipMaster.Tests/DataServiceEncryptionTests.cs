using System.IO;
using ClipMaster;
using Xunit;

namespace ClipMaster.Tests;

public class DataServiceEncryptionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public DataServiceEncryptionTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose()              => Directory.Delete(_tempDir, recursive: true);

    private DataService Svc() => new(_tempDir);

    [Fact]
    public void Load_ReturnsEmpty_WhenNeitherFileExists()
    {
        var result = Svc().Load();
        Assert.Empty(result.Clips);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsData()
    {
        var svc  = Svc();
        var data = new AppData();
        data.Clips.Add(new ClipEntry { Id = "c1", Raw = "hello world", Text = "hello world" });

        svc.Save(data);

        Assert.True(File.Exists(Path.Combine(_tempDir, "data.bin")), "data.bin should exist");
        Assert.False(File.Exists(Path.Combine(_tempDir, "data.json")), "data.json should not exist");

        var loaded = svc.Load();
        Assert.Single(loaded.Clips);
        Assert.Equal("hello world", loaded.Clips[0].Raw);
    }

    [Fact]
    public void Load_MigratesLegacyJson_ToEncryptedBin()
    {
        var jsonPath = Path.Combine(_tempDir, "data.json");
        var binPath  = Path.Combine(_tempDir, "data.bin");
        var original = new AppData();
        original.Clips.Add(new ClipEntry { Id = "c2", Raw = "migrated", Text = "migrated" });
        File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(original,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var loaded = Svc().Load();

        Assert.Equal("migrated", loaded.Clips[0].Raw);
        Assert.True(File.Exists(binPath),   "data.bin should have been created");
        Assert.False(File.Exists(jsonPath), "data.json should have been deleted");
    }

    [Fact]
    public void Load_CleansStaleTemp_AndLoadsFromBin()
    {
        var svc     = Svc();
        var binPath = Path.Combine(_tempDir, "data.bin");
        var tmpPath = Path.Combine(_tempDir, "data.bin.tmp");

        var data = new AppData();
        data.Clips.Add(new ClipEntry { Id = "c3", Raw = "stable", Text = "stable" });
        svc.Save(data);

        File.WriteAllBytes(tmpPath, new byte[] { 0xFF, 0xFE });

        var loaded = svc.Load();

        Assert.Equal("stable", loaded.Clips[0].Raw);
        Assert.False(File.Exists(tmpPath), "stale .tmp should have been deleted");
    }

    [Fact]
    public void Load_ReturnsEmpty_WhenOnlyStaleTempExists()
    {
        var tmpPath = Path.Combine(_tempDir, "data.bin.tmp");
        File.WriteAllBytes(tmpPath, new byte[] { 0xFF });

        var loaded = Svc().Load();

        Assert.Empty(loaded.Clips);
        Assert.False(File.Exists(tmpPath), "orphaned .tmp should have been deleted");
    }
}
