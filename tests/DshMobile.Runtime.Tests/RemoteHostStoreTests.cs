using System.Text.Json;
using DshMobile.Runtime.Remote;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// The C# write side of the remote host list (ADR-0016): the legacy RemoteHosts.save / trust / remove semantics on top of
/// the vector-locked read side, and a stored form the legacy parser (and ours) reads back unchanged.
/// </summary>
public sealed class RemoteHostStoreTests
{
    private const string Fp = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Fp2 = "SHA256:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private static JsonElement Req(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly RemoteHostProfile Pc = new("id-1", "pc", "10.0.2.2", 2222, "me", 3091, Fp);

    [Fact]
    public void Save_NewHost_AppendsWithFreshIdAndNoPin()
    {
        var (all, saved) = RemoteHostStore.Save([Pc], Req("""{"host":"example.org","user":"u","port":22,"dshPort":3091}"""), () => "id-2");
        Assert.Equal("id-2", saved.Id);
        Assert.Equal("u@example.org", saved.Label);
        Assert.Null(saved.PinnedFingerprint);
        Assert.Equal(["id-1", "id-2"], all.Select(p => p.Id));
    }

    [Fact]
    public void Save_SamePeer_KeepsPin_ChangedPort_DropsPin_MovesLast()
    {
        var other = Pc with { Id = "id-0", PinnedFingerprint = null };
        var (all, kept) = RemoteHostStore.Save([Pc, other], Req("""{"id":"id-1","label":"renamed","host":"10.0.2.2","user":"me","port":2222,"dshPort":3092}"""), () => "x");
        Assert.Equal(Fp, kept.PinnedFingerprint);
        Assert.Equal(["id-0", "id-1"], all.Select(p => p.Id)); // legacy: filterNot + appended
        var (_, moved) = RemoteHostStore.Save([Pc], Req("""{"id":"id-1","host":"10.0.2.2","user":"me","port":22,"dshPort":3091}"""), () => "x");
        Assert.Null(moved.PinnedFingerprint); // a new address must be confirmed again
    }

    [Fact]
    public void Save_UnknownId_And_InvalidProfile_AreRefusedWithLegacyMessages()
    {
        var unknown = Assert.Throws<RemoteProfileException>(() => RemoteHostStore.Save([Pc], Req("""{"id":"nope","host":"h","user":"u"}"""), () => "x"));
        Assert.Equal(RemoteHostStore.UnknownHostId, unknown.Message);
        var bad = Assert.Throws<RemoteProfileException>(() => RemoteHostStore.Save([], Req("""{"host":"h; rm -rf /","user":"u"}"""), () => "x"));
        Assert.Equal("host must be a host name or IP address", bad.Message);
    }

    [Fact]
    public void Trust_OnlyTheKeyLastPresentedForThatHost()
    {
        var unpinned = Pc with { PinnedFingerprint = null };
        var pinned = RemoteHostStore.Trust([unpinned], "id-1", Fp, ("id-1", Fp));
        Assert.Equal(Fp, Assert.Single(pinned).PinnedFingerprint);
        foreach (var seen in new (string, string)?[] { null, ("id-1", Fp2), ("id-9", Fp) })
        {
            var e = Assert.Throws<RemoteProfileException>(() => RemoteHostStore.Trust([unpinned], "id-1", Fp, seen));
            Assert.Equal(HostKeyPin.TrustRejected, e.Message);
        }
        Assert.Throws<RemoteProfileException>(() => RemoteHostStore.Trust([unpinned], "id-1", "SHA256:short", ("id-1", "SHA256:short")));
    }

    [Fact]
    public void Remove_DropsOnlyThatHost()
    {
        Assert.Empty(RemoteHostStore.Remove([Pc], "id-1"));
        Assert.Single(RemoteHostStore.Remove([Pc], "other"));
    }

    [Fact]
    public void Render_RoundTripsThroughTheLegacyParser_InLegacyKeyOrder()
    {
        var all = new[] { Pc, Pc with { Id = "id-2", Label = "引号 \"x\"", PinnedFingerprint = null } };
        var text = RemoteHostStore.Render(all);
        Assert.Equal(all, RemoteHostProfile.ParseStored(text));
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(["id", "label", "host", "port", "user", "dshPort", "pinnedFingerprint"],
            doc.RootElement[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, doc.RootElement[1].GetProperty("pinnedFingerprint").ValueKind);
        Assert.Equal("[]", RemoteHostStore.Render([]));
    }

    [Fact]
    public void Render_ReadsBackTheLegacyEscapedForm()
    {
        // org.json writes "/" as "\/": what legacy stored and what C# stores parse to the same profile.
        var legacy = """[{"id":"id-1","label":"pc","host":"10.0.2.2","port":2222,"user":"me","dshPort":3091,"pinnedFingerprint":"SHA256:AAAA\/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}]""";
        var parsed = Assert.Single(RemoteHostProfile.ParseStored(legacy));
        Assert.Equal("SHA256:AAAA/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", parsed.PinnedFingerprint);
        Assert.Equal(parsed, Assert.Single(RemoteHostProfile.ParseStored(RemoteHostStore.Render([parsed]))));
    }
}
