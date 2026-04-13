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
}
