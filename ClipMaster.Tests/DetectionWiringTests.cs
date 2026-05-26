using System.IO;
using System.Linq;
using ClipMaster;
using Xunit;

namespace ClipMaster.Tests;

public class DetectionWiringTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    public DetectionWiringTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose()          => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void AddClip_DetectsLanguage_WhenEnabled()
    {
        var svc  = new DataService(_tempDir);
        var data = new AppData();   // HighlightCode defaults to true
        svc.AddClip(data, "{\"a\": 1}");
        Assert.Equal("json", data.Clips[0].Language);
    }

    [Fact]
    public void AddClip_SkipsDetection_WhenDisabled()
    {
        var svc  = new DataService(_tempDir);
        var data = new AppData();
        data.Settings.HighlightCode = false;
        svc.AddClip(data, "{\"a\": 1}");
        Assert.Equal("", data.Clips[0].Language);
    }
}
