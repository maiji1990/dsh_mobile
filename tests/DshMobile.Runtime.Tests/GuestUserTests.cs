using DshMobile.Runtime.Guest;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// The C# port of GuestUser.kt. The first three are native-bridge GuestUserTest.kt transcribed case by case (that file
/// tests the pure <c>withEntry</c>); the rest pin the file handling.
/// </summary>
public sealed class GuestUserTests : IDisposable
{
    private const string Base = "root:x:0:0:root:/root:/bin/bash\ndaemon:x:1:1:daemon:/usr/sbin:/usr/sbin/nologin\n";
    private readonly string _root = Directory.CreateTempSubdirectory("dshm-user-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AppendsEntryForAppUid() =>
        Assert.Equal(Base + "dshmobile:x:10267:10267::/root:/bin/bash\n", GuestUser.WithEntry(Base, 10267, 10267));

    [Fact]
    public void IsIdempotent_AndReplacesStaleUid()
    {
        var once = GuestUser.WithEntry(Base, 10267, 10267);
        Assert.Equal(once, GuestUser.WithEntry(once, 10267, 10267));
        var moved = GuestUser.WithEntry(once, 10999, 10999);
        Assert.DoesNotContain(":10267:", moved, StringComparison.Ordinal);
        Assert.Single(moved.Split('\n'), l => l.StartsWith("dshmobile:", StringComparison.Ordinal));
    }

    [Fact]
    public void KeepsOtherUsersUntouched()
    {
        var output = GuestUser.WithEntry(Base + "\n\nalice:x:1000:1000::/home/alice:/bin/sh", 5, 5);
        Assert.Equal(["root", "daemon", "alice", "dshmobile"], output.Split('\n').Where(l => l.Length > 0).Select(l => l.Split(':')[0]));
    }

    [Fact]
    public void CrLfLines_AreSplitLikeKotlinLineSequence()
    {
        var output = GuestUser.WithEntry("root:x:0:0::/root:/bin/bash\r\ndshmobile:x:1:1::/root:/bin/bash\r\n", 7, 7);
        Assert.Equal("root:x:0:0::/root:/bin/bash\ndshmobile:x:7:7::/root:/bin/bash\n", output);
    }

    [Fact]
    public void Provision_WritesOnce_ThenAlreadyPresent()
    {
        var etc = Directory.CreateDirectory(Path.Combine(_root, "base", "etc")).FullName;
        File.WriteAllText(Path.Combine(etc, "passwd"), Base);
        Assert.Equal(GuestUser.Outcome.Updated, GuestUser.Provision(Path.Combine(_root, "base"), 10267, 10267));
        Assert.EndsWith("dshmobile:x:10267:10267::/root:/bin/bash\n", File.ReadAllText(Path.Combine(etc, "passwd")), StringComparison.Ordinal);
        Assert.Equal(GuestUser.Outcome.AlreadyPresent, GuestUser.Provision(Path.Combine(_root, "base"), 10267, 10267));
        Assert.False(File.Exists(Path.Combine(etc, "passwd.dshmobile.tmp")));
    }

    [Fact]
    public void Provision_SkipsWithoutRootfs_WithoutRootEntry_OrWithoutUid()
    {
        Assert.Equal(GuestUser.Outcome.Skipped, GuestUser.Provision(Path.Combine(_root, "missing"), 1, 1));
        var etc = Directory.CreateDirectory(Path.Combine(_root, "base", "etc")).FullName;
        Assert.Equal(GuestUser.Outcome.Skipped, GuestUser.Provision(Path.Combine(_root, "base"), 1, 1)); // no passwd
        File.WriteAllText(Path.Combine(etc, "passwd"), "daemon:x:1:1::/:/bin/false\n");
        Assert.Equal(GuestUser.Outcome.Skipped, GuestUser.Provision(Path.Combine(_root, "base"), 1, 1)); // no root:
        File.WriteAllText(Path.Combine(etc, "passwd"), Base);
        Assert.Equal(GuestUser.Outcome.Skipped, GuestUser.Provision(Path.Combine(_root, "base"), -1, 1)); // uid unknown
        Assert.Equal(Base, File.ReadAllText(Path.Combine(etc, "passwd")));
    }
}
