using DshMobile.Native;

namespace DshMobile.Runtime.Tests;

public class NativeLayerTests
{
    private readonly NativeExecutablePolicy _policy = new("/data/app/~~x/org.example.dshmobile-1/lib/arm64");

    [Fact]
    public void ResolveExecutable_OnlyBareLibSoNames()
    {
        var ok = _policy.ResolveExecutable("libproot.so");
        Assert.True(ok.Ok);
        Assert.Equal("/data/app/~~x/org.example.dshmobile-1/lib/arm64/libproot.so", ok.Value);
        foreach (var bad in new[] { "", "proot", "libproot", "../libproot.so", "x/libproot.so", "lib\\a.so", "/data/data/x/files/libproot.so", "lib\0.so" })
        {
            var r = _policy.ResolveExecutable(bad);
            Assert.False(r.Ok, bad);
            Assert.Equal(NativeErrorCodes.ExecutableNotAllowed, r.Code);
        }
    }

    [Theory]
    [InlineData("/data/app/~~x/org.example.dshmobile-1/lib/arm64/libproot.so", true)]
    [InlineData("/data/app/~~x/org.example.dshmobile-1/lib/arm64/./libproot-loader.so", true)]
    [InlineData("/data/user/0/org.example.dshmobile/files/runtime/libproot.so", false)]
    [InlineData("/data/app/~~x/org.example.dshmobile-1/lib/arm64/sub/libx.so", false)]
    [InlineData("/data/app/~~x/org.example.dshmobile-1/lib/arm64/../../evil.so", false)]
    [InlineData("", false)]
    public void IsExecutableLocation_EnforcesWxRule(string path, bool expected) => Assert.Equal(expected, _policy.IsExecutableLocation(path));

    [Fact]
    public void Probe_WithoutLibrary_IsNativeUnavailableNotACrash()
    {
        var r = DshNativeLibrary.Probe();
        Assert.False(r.Ok);
        Assert.Equal(NativeErrorCodes.NativeUnavailable, r.Code);
    }
}
