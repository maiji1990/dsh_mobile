using System.Text.Json;
using DshMobile.Application.Shadow;
using DshMobile.Core.Bridge;
using DshMobile.Runtime.Remote;

namespace DshMobile.Application.Tests;

/// <summary>PR-09 first segment: the read-only remote host shadow (identity, profiles, trust, host-key decision).</summary>
public sealed class RemoteHostsShadowTests
{
    private const string Fp = "SHA256:/O0DopfcupCOPu5e6/7zGNQITxFKE51poNy7jxKvT5Y";
    private const string OtherFp = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private sealed class Sink : IShadowReportSink, IDiagnosticsLog
    {
        public readonly List<ShadowReport> Reports = [];
        public void Report(ShadowReport report) => Reports.Add(report);
        public void Note(string line) { }
    }

    private readonly Sink _sink = new();

    private RemoteHostsShadow Shadow(SliceMode mode = SliceMode.LegacyWithShadow) =>
        new(_sink, _sink, new MigrationOptions { RemoteHosts = mode }, TimeProvider.System);

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static string Stored(params RemoteHost[] hosts) =>
        JsonSerializer.Serialize(hosts, BridgeJsonContext.Default.RemoteHostArray);

    private static RemoteHost Host(string id, string host = "pc.lan", int port = 22, string? pin = null, string label = "pc") =>
        new(id, label, host, port, "u", 3091, pin);

    [Fact]
    public void Disabled_ReportsNothing()
    {
        var s = Shadow(SliceMode.Legacy);
        Assert.Null(s.Listed([], "[]"));
        Assert.Null(s.StatusObserved(BridgeMethods.RemoteConnect, Status("CONNECTED", RemoteCodes.Ok, "a"), "[]"));
        Assert.Empty(_sink.Reports);
    }

    [Fact]
    public void Identity_AgreesOnSameKey_DivergesOnOtherKeyOrFailedSelfTest()
    {
        var facts = new SshIdentityFacts(true, "ecdsa-sha2-nistp256 AAAA dsh-mobile", Fp, true, true, null);
        var legacy = new RemoteIdentityInfo("ecdsa-sha2-nistp256 AAAA dsh-mobile", Fp, "ecdsa-sha2-nistp256", true);
        Assert.Equal(ShadowVerdict.Agree, Shadow().Identity(legacy, () => facts)!.Verdict);
        Assert.Equal(ShadowVerdict.Diverge, RemoteHostsShadow.CompareIdentity(legacy with { Fingerprint = OtherFp }, facts, default).Verdict);
        Assert.Equal(ShadowVerdict.Diverge, RemoteHostsShadow.CompareIdentity(legacy, facts with { SelfTestVerified = false }, default).Verdict);
        Assert.Equal(ShadowVerdict.Diverge, RemoteHostsShadow.CompareIdentity(legacy, facts with { HardwareBacked = null }, default).Verdict);
        Assert.Equal(ShadowVerdict.Diverge, RemoteHostsShadow.CompareIdentity(legacy, new SshIdentityFacts(false, null, null, null, null, null), default).Verdict);
        Assert.Equal(ShadowVerdict.Inconclusive, RemoteHostsShadow.CompareIdentity(legacy, new SshIdentityFacts(false, null, null, null, null, "locked"), default).Verdict);
    }

    [Fact]
    public void Listed_ComparesWithStoredList()
    {
        var a = Host("a", pin: Fp);
        Assert.Equal(ShadowVerdict.Agree, Shadow().Listed([a], Stored(a))!.Verdict);
        Assert.Equal(ShadowVerdict.Diverge, Shadow().Listed([a], Stored(a with { PinnedFingerprint = null }))!.Verdict);
        // A corrupt list reads as empty on both sides.
        Assert.Equal(ShadowVerdict.Agree, Shadow().Listed([], "garbage")!.Verdict);
    }

    [Fact]
    public void Saved_NewHost_Update_AndRejection()
    {
        var s = Shadow();
        var newReq = Json("""{"id":null,"label":"","host":"pc.lan","port":22,"user":"u","dshPort":3091}""");
        var saved = new RemoteHost("uuid-1", "u@pc.lan", "pc.lan", 22, "u", 3091, null);
        Assert.Equal(ShadowVerdict.Agree, s.Saved(newReq, "[]", new(true, "OK", null, saved), Stored(saved))!.Verdict);

        var pinned = Host("a", pin: Fp);
        var sameHost = Json("""{"id":"a","label":"pc","host":"pc.lan","port":22,"user":"v","dshPort":3091}""");
        var kept = pinned with { User = "v" };
        Assert.Equal(ShadowVerdict.Agree, s.Saved(sameHost, Stored(pinned), new(true, "OK", null, kept), Stored(kept))!.Verdict);
        // Legacy keeping the pin after the host changed would be a divergence (and a security regression).
        var moved = Json("""{"id":"a","label":"pc","host":"pc2.lan","port":22,"user":"u","dshPort":3091}""");
        var wrongly = pinned with { Host = "pc2.lan" };
        var r = s.Saved(moved, Stored(pinned), new(true, "OK", null, wrongly), Stored(wrongly))!;
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        Assert.Contains("pin", r.Detail, StringComparison.Ordinal);

        var bad = Json("""{"id":null,"host":"a b","user":"u"}""");
        Assert.Equal(ShadowVerdict.Agree, s.Saved(bad, "[]", new(false, "BAD_REQUEST", "host must be a host name or IP address", null), "[]")!.Verdict);
        var unknown = Json("""{"id":"zz","host":"pc","user":"u"}""");
        Assert.Equal(ShadowVerdict.Agree, s.Saved(unknown, "[]", new(false, "BAD_REQUEST", "unknown host id", null), "[]")!.Verdict);
        Assert.Equal(ShadowVerdict.Diverge, s.Saved(bad, "[]", new(true, "OK", null, Host("x", "a b")), Stored(Host("x", "a b")))!.Verdict);
    }

    [Fact]
    public void Trust_OnlyTheKeyLastPresented_ThenForgotten()
    {
        var s = Shadow();
        var a = Host("a");
        var pinnedA = a with { PinnedFingerprint = Fp };
        // Nothing presented yet: legacy refuses, C# agrees.
        Assert.Equal(ShadowVerdict.Agree, s.Trusted("a", Fp, Stored(a), new(false, "BAD_REQUEST", HostKeyPin.TrustRejected, null), Stored(a))!.Verdict);
        // Connect presents the key.
        Assert.Equal(ShadowVerdict.Agree, s.StatusObserved(BridgeMethods.RemoteConnect, Status("DISCONNECTED", RemoteCodes.HostKeyUnknown, "a", Fp), Stored(a))!.Verdict);
        Assert.Equal(("a", Fp), s.LastUnknown);
        // Another fingerprint is still refused.
        Assert.Equal(ShadowVerdict.Agree, s.Trusted("a", OtherFp, Stored(a), new(false, "BAD_REQUEST", HostKeyPin.TrustRejected, null), Stored(a))!.Verdict);
        // The presented one pins.
        var r = s.Trusted("a", Fp, Stored(a), new(true, "OK", null, new RemoteHostList([pinnedA])), Stored(pinnedA))!;
        Assert.Equal(ShadowVerdict.Agree, r.Verdict);
        Assert.Null(s.LastUnknown);
        // Legacy pinning without a presented key would diverge.
        Assert.Equal(ShadowVerdict.Diverge, s.Trusted("a", Fp, Stored(a), new(true, "OK", null, new RemoteHostList([pinnedA])), Stored(pinnedA))!.Verdict);
    }

    [Fact]
    public void Removed_ComparesRemainingList()
    {
        var a = Host("a");
        var b = Host("b", "pc2.lan");
        Assert.Equal(ShadowVerdict.Agree, Shadow().Removed("a", Stored(a, b), new(true, "OK", null, new RemoteHostList([b])), Stored(b))!.Verdict);
        Assert.Equal(ShadowVerdict.Diverge, Shadow().Removed("a", Stored(a, b), new(true, "OK", null, new RemoteHostList([a, b])), Stored(a, b))!.Verdict);
    }

    [Fact]
    public void HostKey_DecisionFromStatus()
    {
        var s = Shadow();
        var unpinned = Stored(Host("a"));
        var pinned = Stored(Host("a", pin: Fp));
        Assert.Equal(ShadowVerdict.Agree, s.StatusObserved(BridgeMethods.RemoteConnect, Status("DISCONNECTED", RemoteCodes.HostKeyChanged, "a", OtherFp), pinned)!.Verdict);
        Assert.Equal(ShadowVerdict.Agree, s.StatusObserved(BridgeMethods.RemoteConnect, Status("CONNECTED", RemoteCodes.DshMissing, "a"), pinned)!.Verdict);
        Assert.Equal(ShadowVerdict.Agree, s.StatusObserved(BridgeMethods.RemoteConnect, Status("DISCONNECTED", RemoteCodes.AuthFailed, "a"), pinned)!.Verdict);
        // Connected without any pin: the legacy accepted an unconfirmed key.
        Assert.Equal(ShadowVerdict.Diverge, s.StatusObserved(BridgeMethods.RemoteConnect, Status("CONNECTED", RemoteCodes.Ok, "a"), unpinned)!.Verdict);
        // CHANGED reported although the key equals the pin.
        Assert.Equal(ShadowVerdict.Diverge, s.StatusObserved(BridgeMethods.RemoteConnect, Status("DISCONNECTED", RemoteCodes.HostKeyChanged, "a", Fp), pinned)!.Verdict);
        // No decision implied: unreachable, plain status polls.
        Assert.Null(s.StatusObserved(BridgeMethods.RemoteConnect, Status("DISCONNECTED", RemoteCodes.Unreachable, "a"), pinned));
        Assert.Null(s.StatusObserved(BridgeMethods.RemoteStatus, Status("CONNECTED", RemoteCodes.Ok, "a"), pinned));
        Assert.Equal(ShadowVerdict.Inconclusive, s.StatusObserved(BridgeMethods.RemoteConnect, Status("CONNECTED", RemoteCodes.Ok, "gone"), pinned)!.Verdict);
    }

    // ------------------------------------------------------------ PR-09 second segment: remote.connect

    private static readonly RemoteProbe Probe = new(true, "v26.13.0", true, "0.2.1-rc.5");
    private static readonly RemoteHostProfile Profile = new("a", "pc", "pc.lan", 22, "u", 3091, Fp);

    private static RemoteConnectAttempt Cs(string state, string code, string? message = null, string? fp = null, string? type = null,
        RemoteProbe? probe = null, bool timedOut = false) => new(state, code, message, fp, type, probe, timedOut, null);

    private static RemoteStatusInfo Legacy(string state, string code, string? message = null, string? fp = null, string? type = null, RemoteProbe? probe = null) =>
        new(state, code, message, "a", "pc", probe, null, 3091, false, fp, type);

    [Fact]
    public void CompareConnect_AgreesOnEachLegacyOutcome()
    {
        (RemoteStatusInfo L, RemoteConnectAttempt C)[] same =
        [
            (Legacy("CONNECTED", "OK", probe: Probe), Cs("CONNECTED", "OK", probe: Probe)),
            (Legacy("CONNECTED", "DSH_MISSING", RemoteConnectRules.DshMissingMessage, probe: Probe with { Dsh = false, DshVersion = "'dsh' is not recognized" }),
                Cs("CONNECTED", "DSH_MISSING", RemoteConnectRules.DshMissingMessage, probe: Probe with { Dsh = false, DshVersion = "'dsh' is not recognized" })),
            (Legacy("DISCONNECTED", "HOST_KEY_UNKNOWN", RemoteConnectRules.UnknownMessage, Fp, "ssh-ed25519"),
                Cs("DISCONNECTED", "HOST_KEY_UNKNOWN", RemoteConnectRules.UnknownMessage, Fp, "ssh-ed25519")),
            (Legacy("DISCONNECTED", "HOST_KEY_CHANGED", RemoteConnectRules.ChangedMessage, OtherFp), Cs("DISCONNECTED", "HOST_KEY_CHANGED", RemoteConnectRules.ChangedMessage, OtherFp)),
            (Legacy("DISCONNECTED", "AUTH_FAILED", RemoteConnectRules.AuthMessage), Cs("DISCONNECTED", "AUTH_FAILED", RemoteConnectRules.AuthMessage)),
            // Library-worded messages are not compared.
            (Legacy("DISCONNECTED", "UNREACHABLE", "java.net.ConnectException: Connection refused"), Cs("DISCONNECTED", "UNREACHABLE", "Connection refused")),
        ];
        foreach (var (l, c) in same)
        {
            var r = RemoteHostsShadow.CompareConnect(l, c, default);
            Assert.True(r.Verdict == ShadowVerdict.Agree, $"{l.Code}: {r.Detail}");
            Assert.Equal(RemoteHostsShadow.ConnectSlice, r.Slice);
        }
    }

    [Fact]
    public void CompareConnect_DivergesOnCodeFingerprintKeyTypeOrProbe_InconclusiveOnNoise()
    {
        var ok = Legacy("CONNECTED", "OK", probe: Probe);
        Assert.Equal(ShadowVerdict.Diverge, RemoteHostsShadow.CompareConnect(ok, Cs("DISCONNECTED", "HOST_KEY_CHANGED", RemoteConnectRules.ChangedMessage, OtherFp), default).Verdict);
        Assert.Equal(ShadowVerdict.Diverge, RemoteHostsShadow.CompareConnect(ok, Cs("CONNECTED", "OK", probe: Probe with { NodeVersion = "v25" }), default).Verdict);
        Assert.Equal(ShadowVerdict.Diverge, RemoteHostsShadow.CompareConnect(ok, Cs("DISCONNECTED", "UNREACHABLE", "refused"), default).Verdict);
        var unknown = Legacy("DISCONNECTED", "HOST_KEY_UNKNOWN", RemoteConnectRules.UnknownMessage, Fp, "ssh-ed25519");
        Assert.Equal(ShadowVerdict.Diverge, RemoteHostsShadow.CompareConnect(unknown, Cs("DISCONNECTED", "HOST_KEY_UNKNOWN", RemoteConnectRules.UnknownMessage, OtherFp, "ecdsa-sha2-nistp256"), default).Verdict);
        Assert.Equal(ShadowVerdict.Diverge, RemoteHostsShadow.CompareConnect(Legacy("DISCONNECTED", "AUTH_FAILED", RemoteConnectRules.AuthMessage),
            Cs("DISCONNECTED", "AUTH_FAILED", "other text"), default).Verdict);

        Assert.Equal(ShadowVerdict.Inconclusive, RemoteHostsShadow.CompareConnect(Legacy("DISCONNECTED", "TIMEOUT", "timeout"), Cs("DISCONNECTED", "UNREACHABLE", "x"), default).Verdict);
        Assert.Equal(ShadowVerdict.Inconclusive, RemoteHostsShadow.CompareConnect(ok, Cs("CONNECTED", "DSH_MISSING", probe: Probe with { Dsh = false, DshVersion = null }, timedOut: true), default).Verdict);
        Assert.Equal(ShadowVerdict.Inconclusive, RemoteHostsShadow.CompareConnect(ok, Cs("DISCONNECTED", RemoteConnectRules.IdentityMissing), default).Verdict);
    }

    [Fact]
    public async Task ConnectedAsync_RunsOnlyWhenEnabled_UsesTheProfileReadBeforeTheCall()
    {
        var legacy = Legacy("CONNECTED", "OK", probe: Probe);
        RemoteHostProfile? used = null;
        Task<RemoteConnectAttempt> Connect(RemoteHostProfile p, CancellationToken _)
        {
            used = p;
            return Task.FromResult(Cs("CONNECTED", "OK", probe: Probe));
        }

        var off = new RemoteHostsShadow(_sink, _sink, new MigrationOptions { RemoteConnect = SliceMode.Legacy }, TimeProvider.System);
        Assert.Null(await off.ConnectedAsync(legacy, Profile, Connect));
        Assert.Null(used);

        var r = await Shadow().ConnectedAsync(legacy, Profile, Connect);
        Assert.Equal(ShadowVerdict.Agree, r!.Verdict);
        Assert.Same(Profile, used);

        Assert.Equal(ShadowVerdict.Inconclusive, (await Shadow().ConnectedAsync(legacy, null, Connect))!.Verdict);
        var threw = await Shadow().ConnectedAsync(legacy, Profile, (_, _) => throw new InvalidOperationException("boom"));
        Assert.Equal(ShadowVerdict.Inconclusive, threw!.Verdict);
        Assert.Contains("boom", threw.Detail, StringComparison.Ordinal);
        Assert.Equal(3, _sink.Reports.Count(x => x.Slice == RemoteHostsShadow.ConnectSlice)); // the disabled shadow reported nothing
    }

    // ------------------------------------------------------------ PR-09 third segment: remote.dsh

    private static RemoteStatusInfo Started(string code, int? http) =>
        new("READY", code, null, "a", "pc", Probe, 50000, 3091, true, HttpUnauthenticated: http);

    private static RemoteStatusInfo Stopped(bool? still) =>
        new("CONNECTED", "OK", null, "a", "pc", Probe, null, 3091, false, RemoteDshStillRunning: still);

    [Fact]
    public void CompareDsh_StartAndStopVerdicts()
    {
        Assert.Equal(ShadowVerdict.Agree, RemoteHostsShadow.CompareDshStart(Started("OK", 401), 401, default).Verdict);
        Assert.Equal(ShadowVerdict.Agree, RemoteHostsShadow.CompareDshStart(Started("DSH_START_FAILED", 200), 200, default).Verdict);
        Assert.Equal(ShadowVerdict.Diverge, RemoteHostsShadow.CompareDshStart(Started("OK", 401), null, default).Verdict);
        Assert.Equal(ShadowVerdict.Diverge, RemoteHostsShadow.CompareDshStart(Started("OK", 401), 200, default).Verdict);

        Assert.Equal(ShadowVerdict.Agree, RemoteHostsShadow.CompareDshStop(Stopped(false), null, default).Verdict);
        Assert.Equal(ShadowVerdict.Agree, RemoteHostsShadow.CompareDshStop(Stopped(true), 401, default).Verdict);
        Assert.Equal(ShadowVerdict.Diverge, RemoteHostsShadow.CompareDshStop(Stopped(false), 401, default).Verdict);
        Assert.Equal(ShadowVerdict.Agree, RemoteHostsShadow.CompareDshStop(Stopped(false), 500, default).Verdict); // 500 is not "running"
    }

    [Fact]
    public async Task DshObservedAsync_OnlyChecksLegacyVerdicts_NeverWithoutOne()
    {
        var calls = 0;
        Task<int?> Probe401(RemoteHostProfile p, CancellationToken _)
        {
            calls++;
            Assert.Equal(3091, p.DshPort);
            return Task.FromResult<int?>(401);
        }

        var s = Shadow();
        Assert.Equal(ShadowVerdict.Agree, (await s.DshObservedAsync(BridgeMethods.RemoteStart, Started("OK", 401), Profile, Probe401))!.Verdict);
        // No verdict to check: start that never reached the forward, stop without a conclusion, other methods.
        Assert.Null(await s.DshObservedAsync(BridgeMethods.RemoteStart, new RemoteStatusInfo("CONNECTED", "DSH_START_FAILED", "no URL", "a", "pc", null, null, 3091, false), Profile, Probe401));
        Assert.Null(await s.DshObservedAsync(BridgeMethods.RemoteStop, Stopped(null), Profile, Probe401));
        Assert.Null(await s.DshObservedAsync(BridgeMethods.RemoteConnect, Started("OK", 401), Profile, Probe401));
        Assert.Equal(1, calls);

        var failed = await s.DshObservedAsync(BridgeMethods.RemoteStop, Stopped(false), Profile, (_, _) => throw new InvalidOperationException("C# connect UNREACHABLE"));
        Assert.Equal(ShadowVerdict.Inconclusive, failed!.Verdict);

        var off = new RemoteHostsShadow(_sink, _sink, new MigrationOptions { RemoteDsh = SliceMode.Legacy }, TimeProvider.System);
        Assert.Null(await off.DshObservedAsync(BridgeMethods.RemoteStart, Started("OK", 401), Profile, Probe401));
        Assert.Equal(1, calls);
        Assert.Equal(2, _sink.Reports.Count(r => r.Slice == RemoteHostsShadow.DshSlice));
    }

    private static RemoteStatusInfo Status(string state, string code, string? hostId, string? fp = null) =>
        new(state, code, null, hostId, "pc", null, null, 3091, false, fp);
}
