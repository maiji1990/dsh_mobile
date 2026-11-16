using DshMobile.Runtime.Hosting;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// Free space is read for the path itself (statvfs on Unix), not for a mount picked by path prefix: on a real Android 17
/// device the prefix match landed on a tmpfs and showed 7.6 GB for a partition with 261 GB free.
/// </summary>
public sealed class RuntimeFreeBytesTests
{
    [Fact]
    public void FreeBytes_MatchesTheDriveOfThePath()
    {
        var dir = Directory.CreateTempSubdirectory("dshmobile-free-").FullName;
        try
        {
            var free = RuntimeStatusReader.FreeBytes(dir);
            Assert.True(free > 0);
            Assert.Equal(new DriveInfo(dir).AvailableFreeSpace, free, tolerance: 64L * 1024 * 1024);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void FreeBytes_IsUnknownForAnInvalidPath() => Assert.Equal(-1, RuntimeStatusReader.FreeBytes("\0"));
}
