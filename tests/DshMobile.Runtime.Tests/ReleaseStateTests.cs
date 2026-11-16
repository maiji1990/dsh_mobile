using DshMobile.Runtime.Layers;
using DshMobile.Runtime.Updates;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// vNext PR-02: fixed channel endpoints, the per-channel anti-replay floor (ADR-0022 decision 4) and the installed
/// versions the catalog compares against.
/// </summary>
public sealed class ReleaseStateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-relstate-").FullName;

    public void Dispose() => LayerInstaller.DeleteTree(_dir);

    private RuntimeLayout Layout() => new(Path.Combine(_dir, "files"), null, "/lib");

    private static readonly DateTimeOffset T1 = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Endpoints_AreFixedPerChannel_OnThisPublicRepository()
    {
        Assert.Equal(new Uri("https://github.com/maiji1990/dsh_mobile/releases/download/channel-stable/release-manifest.json"), ReleaseEndpoints.ManifestUrl("stable"));
        Assert.Equal(new Uri("https://github.com/maiji1990/dsh_mobile/releases/download/channel-preview/release-manifest.json"), ReleaseEndpoints.ManifestUrl("preview"));
        Assert.Throws<ArgumentException>(() => ReleaseEndpoints.ManifestUrl("latest"));
        Assert.Throws<ArgumentException>(() => ReleaseEndpoints.ManifestUrl("../stable"));
        // The URL a check uses is accepted by the source (https, no credentials).
        using var http = new HttpClient();
        _ = new HttpReleaseSource(http, ReleaseEndpoints.ManifestUrl("stable"));
    }

    [Fact]
    public void Floor_StartsEmpty_OnlyMovesForward_AndSurvivesARestart()
    {
        var store = new ReleaseAcceptanceStore(Layout());
        Assert.Equal(new ReleaseFloor(true, "OK", null, null), store.Read("stable"));

        Assert.Equal(T1, store.Accept("stable", T1).GeneratedAt);
        Assert.Equal(T1, store.Accept("stable", T1.AddDays(-1)).GeneratedAt); // never lowered
        Assert.Equal(T1.AddHours(1), store.Accept("stable", T1.AddHours(1)).GeneratedAt);
        Assert.Null(store.Read("preview").GeneratedAt); // channels are independent

        var reopened = new ReleaseAcceptanceStore(Layout());
        Assert.Equal(T1.AddHours(1), reopened.Read("stable").GeneratedAt);
        Assert.False(File.Exists(reopened.FilePath + ".tmp"));
        Assert.StartsWith(Layout().HostStateDir, reopened.FilePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"schema\":2,\"channels\":{}}")]
    [InlineData("{\"schema\":1,\"channels\":{\"stable\":{\"generatedAt\":\"yesterday\"}}}")]
    [InlineData("{\"schema\":1,\"channels\":{\"stable\":{}}}")]
    public void CorruptState_IsAnError_NeverAnEmptyFloor(string content)
    {
        var store = new ReleaseAcceptanceStore(Layout());
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, content);
        var r = store.Read("stable");
        Assert.False(r.Ok);
        Assert.Equal(UpdateErrorCodes.StateUnreadable, r.Code);
        Assert.Equal(UpdateErrorCodes.StateUnreadable, store.Accept("stable", T1).Code); // and it is not overwritten
        Assert.Equal(content, File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void WriteFailure_IsReported()
    {
        var layout = Layout();
        Directory.CreateDirectory(layout.FilesDir);
        File.WriteAllText(layout.HostStateDir, "a file where the state directory should be");
        var r = new ReleaseAcceptanceStore(layout).Accept("stable", T1);
        Assert.False(r.Ok);
        Assert.Equal(UpdateErrorCodes.StateWriteFailed, r.Code);
    }

    [Fact]
    public void UnknownChannel_IsRefused()
    {
        var store = new ReleaseAcceptanceStore(Layout());
        Assert.Throws<ArgumentException>(() => store.Read("nightly"));
        Assert.Throws<ArgumentException>(() => store.Accept("nightly", T1));
    }

    [Fact]
    public void InstalledVersions_FromTheProfileAndTheDshLayer()
    {
        var layout = Layout();
        Assert.Equal(new InstalledVersions("0.1.0", null, null, null), InstalledVersionsReader.Read(layout, "0.1.0"));

        Directory.CreateDirectory(layout.RuntimeCurrentDir);
        File.WriteAllText(Path.Combine(layout.RuntimeCurrentDir, "runtime-profile.json"),
            """{"bundle":{"kind":"runtime","name":"dshmobile-runtime-debian-arm64","version":"0.2.0"},"layers":[{"name":"base","version":"0.2.0"},{"name":"node","version":"24.19.0"}]}""");
        var dshState = Path.Combine(layout.LayerDir("dsh"), ".dshmobile");
        Directory.CreateDirectory(dshState);
        File.WriteAllText(Path.Combine(dshState, "version"), "0.2.1-rc.5");
        Assert.Equal(new InstalledVersions("0.1.0", "0.2.0", "24.19.0", "0.2.1-rc.5"), InstalledVersionsReader.Read(layout, "0.1.0"));

        // An unreadable profile is "unknown", not a crash and not "up to date".
        File.WriteAllText(Path.Combine(layout.RuntimeCurrentDir, "runtime-profile.json"), "{");
        Assert.Equal(new InstalledVersions("0.1.0", null, null, "0.2.1-rc.5"), InstalledVersionsReader.Read(layout, "0.1.0"));
    }
}
