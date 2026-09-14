using InstantMeshPlus.Core;

namespace InstantMeshPlus.Core.Tests;

public sealed class WorkerLocatorTests
{
    [Theory]
    [InlineData(true, "InstantMeshPlus.Worker.exe")]
    [InlineData(false, "InstantMeshPlus.Worker")]
    public void SelectsTheWorkerNameForTheHostPlatform(bool isWindows, string expected)
    {
        Assert.Equal(expected, WorkerLocator.ExecutableNameFor(isWindows));
    }

    [Fact]
    public void ExplicitWorkerPathHasPriority()
    {
        var path = Path.GetTempFileName();
        try
        {
            Assert.Equal(Path.GetFullPath(path), WorkerLocator.Resolve(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
