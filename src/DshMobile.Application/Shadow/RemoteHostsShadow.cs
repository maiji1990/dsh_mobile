using System.Text.Json;
using DshMobile.Core.Bridge;
using DshMobile.Runtime.Remote;

namespace DshMobile.Application.Shadow;

/// <summary>A legacy bridge answer as the remote page received it.</summary>
public sealed record RemoteLegacyOutcome<T>(bool Ok, string Code, string? Message, T? Payload) where T : class;

/// <summary>
/// PR-09 first segment, read-only shadow of the remote host slice (ADR-0006). After each legacy <c>remote.*</c> call
/// of the remote page, the C# port reads the same SharedPreferences profile list and the same Keystore key and must
/// reach the same result: identity (public key line, fingerprint, hardware flag, plus a Keystore signature that the C#
/// DER → SSH conversion keeps verifiable), profile list / save / trust / remove (validation messages, pin carry-over,
/// the stored list afterwards) and the host-key decision of each connect. SSH itself stays legacy (JSch).
/// The C# side never writes the profile list and never creates the key.
/// </summary>
public sealed class RemoteHostsShadow(IShadowReportSink sink, IDiagnosticsLog log, MigrationOptions options, TimeProvider time)
{
    public const string IdentitySlice = "remote.identity";
    public const string HostsSlice = "remote.hosts";
    public const string HostKeySlice = "remote.host-key";
    public const string ConnectSlice = "remote.connect";
    public const string DshSlice = "remote.dsh";

    /// <summary>Bridge code of a rejected profile / trust request.</summary>
    public const string BadRequest = "BAD_REQUEST";

    /// <summary>Message the legacy save gives for an id that is not in the list.</summary>
    public const string UnknownHostId = "unknown host id";

    private readonly Lock _gate = new();
    private (string HostId, string Fingerprint)? _lastUnknown;

    public bool Enabled => options.RemoteHosts == SliceMode.LegacyWithShadow;

    /// <summary>The (host, fingerprint) of the last HOST_KEY_UNKNOWN this shadow observed; mirrors legacy memory.</summary>
    public (string HostId, string Fingerprint)? LastUnknown
    {
        get
        {
            lock (_gate)
            {
                return _lastUnknown;
            }
        }
    }

    /// <summary>After a legacy <c>remote.identity</c>: the C# Keystore probe must describe the same key.</summary>
    public ShadowReport? Identity(RemoteIdentityInfo legacy, Func<SshIdentityFacts> probe)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(probe);
        if (!Enabled)
        {
            return null;
        }
        var start = time.GetTimestamp();
        var facts = probe();
        return Emit(CompareIdentity(legacy, facts, time.GetUtcNow()) with { CSharpMs = (long)time.GetElapsedTime(start).TotalMilliseconds });
    }

    /// <summary>After a legacy <c>remote.hosts.list</c>.</summary>
    public ShadowReport? Listed(RemoteHost[] legacy, string? stored)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        return Enabled ? Emit(CompareList(legacy, stored, time.GetUtcNow())) : null;
    }

    /// <summary>After a legacy <c>remote.hosts.save</c>; <paramref name="before"/> was read just before the call.</summary>
    public ShadowReport? Saved(JsonElement request, string? before, RemoteLegacyOutcome<RemoteHost> legacy, string? after)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        return Enabled ? Emit(CompareSave(request, before, legacy, after, time.GetUtcNow())) : null;
    }

    /// <summary>After a legacy <c>remote.trust</c>. A successful trust clears the remembered unknown key, as in legacy.</summary>
    public ShadowReport? Trusted(string hostId, string fingerprint, string? before, RemoteLegacyOutcome<RemoteHostList> legacy, string? after)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        if (!Enabled)
        {
            return null;
        }
        ShadowReport report;
        lock (_gate)
        {
            report = CompareTrust(hostId, fingerprint, _lastUnknown, before, legacy, after, time.GetUtcNow());
            if (legacy.Ok)
            {
                _lastUnknown = null;
            }
        }
        return Emit(report);
    }

    /// <summary>After a legacy <c>remote.hosts.remove</c>.</summary>
    public ShadowReport? Removed(string hostId, string? before, RemoteLegacyOutcome<RemoteHostList> legacy, string? after)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        return Enabled ? Emit(CompareRemove(hostId, before, legacy, after, time.GetUtcNow())) : null;
    }

    /// <summary>
    /// After any legacy <c>remote.*</c> status answer. A HOST_KEY_UNKNOWN is remembered (the only key a later trust may
    /// pin). The host-key decision is compared when the answer implies one: UNKNOWN / CHANGED carry the fingerprint;
    /// a connect that got past the key exchange (CONNECTED, or AUTH_FAILED) implies the pinned key matched.
    /// </summary>
    public ShadowReport? StatusObserved(string method, RemoteStatusInfo status, string? stored)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!Enabled)
        {
            return null;
        }
        if (status is { Code: RemoteCodes.HostKeyUnknown, HostId: { } id, Fingerprint: { } fp })
        {
            lock (_gate)
            {
                _lastUnknown = (id, fp);
            }
        }
        var report = CompareHostKey(method, status, stored, time.GetUtcNow());
        return report is null ? null : Emit(report);
    }

    public static ShadowReport CompareIdentity(RemoteIdentityInfo legacy, SshIdentityFacts cs, DateTimeOffset at)
    {
        if (!cs.KeyPresent)
        {
            var verdict = cs.Error is null ? ShadowVerdict.Diverge : ShadowVerdict.Inconclusive;
            return Report(IdentitySlice, at, verdict, true, false, cs.Error is null
                ? $"legacy reported {legacy.Fingerprint} but C# found no Keystore alias {SshIdentity.KeystoreAlias}"
                : $"C# could not read the Keystore key: {cs.Error}");
        }
        var checks = new (string Name, string Legacy, string CSharp)[]
        {
            ("publicKey", legacy.PublicKey, cs.PublicKeyLine ?? "-"),
            ("fingerprint", legacy.Fingerprint, cs.Fingerprint ?? "-"),
            ("algorithm", legacy.Algorithm, SshWire.Algorithm),
            ("hardwareBacked", Show(legacy.HardwareBacked), Show(cs.HardwareBacked)),
            ("selfTest", "verified", cs.SelfTestVerified == true ? "verified" : cs.Error ?? "signature did not verify"),
        };
        return FromChecks(IdentitySlice, at, checks, cs.SelfTestVerified == true,
            $"{cs.Fingerprint} hardwareBacked={Show(cs.HardwareBacked)}, Keystore signature verified after DER → SSH");
    }

    public static ShadowReport CompareList(RemoteHost[] legacy, string? stored, DateTimeOffset at)
    {
        var cs = RemoteHostProfile.ParseStored(stored);
        var checks = new[] { ("hosts", Describe(legacy.Select(FromDto)), Describe(cs)) };
        return FromChecks(HostsSlice, at, checks, true, $"after remote.hosts.list: {cs.Count} profile(s) identical");
    }

    public static ShadowReport CompareSave(JsonElement request, string? before, RemoteLegacyOutcome<RemoteHost> legacy, string? after, DateTimeOffset at)
    {
        var listBefore = RemoteHostProfile.ParseStored(before);
        var listAfter = RemoteHostProfile.ParseStored(after);
        var id = RemoteHostProfile.RequestedId(request);
        var existing = id is null ? null : listBefore.FirstOrDefault(p => p.Id == id);
        RemoteHostProfile? predicted = null;
        string? rejected = null;
        if (id is not null && existing is null)
        {
            rejected = UnknownHostId;
        }
        else
        {
            try
            {
                predicted = RemoteHostProfile.FromRequest(request, existing, () => "<new>");
            }
            catch (RemoteProfileException ex)
            {
                rejected = ex.Message;
            }
        }

        var checks = new List<(string Name, string Legacy, string CSharp)>
        {
            ("outcome", legacy.Ok ? "saved" : $"{legacy.Code}: {legacy.Message}", rejected is null ? "saved" : $"{BadRequest}: {rejected}"),
        };
        if (legacy.Ok && predicted is not null && legacy.Payload is { } savedDto)
        {
            var saved = FromDto(savedDto);
            var isNew = existing is null;
            var idOk = isNew ? saved.Id.Length > 0 && listBefore.All(p => p.Id != saved.Id) : saved.Id == existing!.Id;
            checks.Add(("id", isNew ? "new id" : saved.Id, idOk ? (isNew ? "new id" : saved.Id) : $"unexpected {saved.Id}"));
            checks.Add(("profile", Describe([saved with { Id = "" }]), Describe([predicted with { Id = "" }])));
            checks.Add(("storedAfter", Describe(listAfter), Describe([.. listBefore.Where(p => p.Id != saved.Id), saved])));
        }
        else
        {
            checks.Add(("storedAfter", Describe(listAfter), Describe(listBefore)));
        }
        var detail = legacy.Ok
            ? $"after remote.hosts.save: {(existing is null ? "new host" : "updated host")}, pin {(predicted?.PinnedFingerprint is null ? "none" : "kept")}"
            : $"after remote.hosts.save: rejected alike ({rejected})";
        return FromChecks(HostsSlice, at, checks, true, detail, legacy.Ok);
    }

    public static ShadowReport CompareTrust(string hostId, string fingerprint, (string HostId, string Fingerprint)? lastUnknown,
        string? before, RemoteLegacyOutcome<RemoteHostList> legacy, string? after, DateTimeOffset at)
    {
        var listBefore = RemoteHostProfile.ParseStored(before);
        var listAfter = RemoteHostProfile.ParseStored(after);
        var allowed = HostKeyPin.TrustAllowed(hostId, fingerprint, lastUnknown);
        var expectedAfter = allowed
            ? listBefore.Select(p => p.Id == hostId ? p with { PinnedFingerprint = fingerprint } : p).ToList()
            : listBefore.ToList();
        var checks = new List<(string Name, string Legacy, string CSharp)>
        {
            ("outcome", legacy.Ok ? "pinned" : $"{legacy.Code}: {legacy.Message}", allowed ? "pinned" : $"{BadRequest}: {HostKeyPin.TrustRejected}"),
            ("storedAfter", Describe(listAfter), Describe(expectedAfter)),
        };
        if (legacy is { Ok: true, Payload: { } hosts })
        {
            checks.Add(("reply", Describe(hosts.Hosts.Select(FromDto)), Describe(listAfter)));
        }
        var detail = $"after remote.trust: {(allowed ? "pinned the key last presented by this host" : "refused alike (not the key last presented)")}"
            + (lastUnknown is null ? "; C# saw no unknown key in this process" : "");
        return FromChecks(HostsSlice, at, checks, true, detail, legacy.Ok);
    }

    public static ShadowReport CompareRemove(string hostId, string? before, RemoteLegacyOutcome<RemoteHostList> legacy, string? after, DateTimeOffset at)
    {
        var listAfter = RemoteHostProfile.ParseStored(after);
        var expected = RemoteHostProfile.ParseStored(before).Where(p => p.Id != hostId).ToList();
        var checks = new List<(string Name, string Legacy, string CSharp)>
        {
            ("outcome", legacy.Ok ? "removed" : $"{legacy.Code}: {legacy.Message}", "removed"),
            ("storedAfter", Describe(listAfter), Describe(expected)),
        };
        if (legacy is { Ok: true, Payload: { } hosts })
        {
            checks.Add(("reply", Describe(hosts.Hosts.Select(FromDto)), Describe(listAfter)));
        }
        return FromChecks(HostsSlice, at, checks, true, $"after remote.hosts.remove: {expected.Count} profile(s) left", legacy.Ok);
    }

    /// <summary>Null when the status implies no host-key decision (e.g. UNREACHABLE before the key exchange).</summary>
    public static ShadowReport? CompareHostKey(string method, RemoteStatusInfo status, string? stored, DateTimeOffset at)
    {
        if (status.HostId is not { } id)
        {
            return null;
        }
        HostKeyDecision legacyDecision;
        string? fp = status.Fingerprint;
        switch (status.Code)
        {
            case RemoteCodes.HostKeyUnknown when fp is not null:
                legacyDecision = HostKeyDecision.NotIncluded;
                break;
            case RemoteCodes.HostKeyChanged when fp is not null:
                legacyDecision = HostKeyDecision.Changed;
                break;
            case RemoteCodes.AuthFailed when method == BridgeMethods.RemoteConnect:
                legacyDecision = HostKeyDecision.Ok;
                break;
            default:
                if (method != BridgeMethods.RemoteConnect || status.State is not ("CONNECTED" or "READY"))
                {
                    return null;
                }
                legacyDecision = HostKeyDecision.Ok;
                break;
        }
        var profile = RemoteHostProfile.ParseStored(stored).FirstOrDefault(p => p.Id == id);
        if (profile is null)
        {
            return Report(HostKeySlice, at, ShadowVerdict.Inconclusive, true, false, $"after {method}: host {id} is no longer stored");
        }
        // Without the key blob, an accepted key can only be checked against "a pin exists" (a match is implied).
        var csDecision = fp is not null ? HostKeyPin.Decide(profile.PinnedFingerprint, fp)
            : profile.PinnedFingerprint is null ? HostKeyDecision.NotIncluded : HostKeyDecision.Ok;
        var checks = new[] { ("decision", legacyDecision.ToString(), csDecision.ToString()) };
        return FromChecks(HostKeySlice, at, checks, true,
            $"after {method}: {status.Code} ↔ {csDecision} (pin {(profile.PinnedFingerprint is null ? "none" : "set")}{(fp is null ? "" : ", key " + fp)})");
    }

    public bool ConnectEnabled => options.RemoteConnect == SliceMode.LegacyWithShadow;

    /// <summary>
    /// After a legacy <c>remote.connect</c> (PR-09 second segment): C# connects on its own to <paramref name="profile"/>
    /// (read before the legacy call, so both used the same pin), probes, disconnects and must reach the same outcome.
    /// </summary>
    public async Task<ShadowReport?> ConnectedAsync(RemoteStatusInfo legacy, RemoteHostProfile? profile,
        Func<RemoteHostProfile, CancellationToken, Task<RemoteConnectAttempt>> connect, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(connect);
        if (!ConnectEnabled)
        {
            return null;
        }
        if (profile is null)
        {
            return Emit(Report(ConnectSlice, time.GetUtcNow(), ShadowVerdict.Inconclusive, true, false,
                $"after remote.connect: host {legacy.HostId ?? "-"} was not stored before the call"));
        }
        var start = time.GetTimestamp();
        RemoteConnectAttempt attempt;
        try
        {
            attempt = await connect(profile, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Emit(Report(ConnectSlice, time.GetUtcNow(), ShadowVerdict.Inconclusive, true, false,
                $"C# connect threw {e.GetType().Name}: {e.Message}"));
        }
        // Nothing else may use the shadow's session: it only exists to be compared.
        attempt.Session?.Dispose();
        var ms = (long)time.GetElapsedTime(start).TotalMilliseconds;
        return Emit(CompareConnect(legacy, attempt, time.GetUtcNow()) with { CSharpMs = ms });
    }

    private static readonly HashSet<string> NetworkCodes = [RemoteCodes.Unreachable, RemoteCodes.Timeout];

    /// <summary>
    /// Legacy connect status ↔ C# attempt: state, code, the fixed messages, fingerprint / key type, and the probe.
    /// Library-worded messages (UNREACHABLE / TIMEOUT) are not compared; a network failure on one side and a different
    /// network failure on the other, a C# probe timeout or a missing C# key is Inconclusive.
    /// </summary>
    public static ShadowReport CompareConnect(RemoteStatusInfo legacy, RemoteConnectAttempt cs, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(cs);
        var summary = $"legacy {legacy.State}/{legacy.Code}, C# {cs.State}/{cs.Code}";
        if (cs.Code == RemoteConnectRules.IdentityMissing)
        {
            return Report(ConnectSlice, at, ShadowVerdict.Inconclusive, true, false, $"{summary}: C# found no Keystore key");
        }
        if (legacy.Code != cs.Code && NetworkCodes.Contains(legacy.Code) && NetworkCodes.Contains(cs.Code))
        {
            return Report(ConnectSlice, at, ShadowVerdict.Inconclusive, true, true, $"{summary}: network failures differ ({cs.Message})");
        }
        if (cs.ProbeTimedOut)
        {
            return Report(ConnectSlice, at, ShadowVerdict.Inconclusive, true, true, $"{summary}: a C# probe command timed out");
        }
        var checks = new List<(string Name, string Legacy, string CSharp)>
        {
            ("state", legacy.State, cs.State),
            ("code", legacy.Code, cs.Code),
            ("fingerprint", legacy.Fingerprint ?? "-", cs.Fingerprint ?? "-"),
            ("keyType", legacy.KeyType ?? "-", cs.KeyType ?? "-"),
        };
        if (!NetworkCodes.Contains(legacy.Code))
        {
            checks.Add(("message", legacy.Message ?? "-", cs.Message ?? "-"));
        }
        checks.Add(("probe", DescribeProbe(legacy.Probe), DescribeProbe(cs.Probe)));
        var detail = cs.Probe is { } p
            ? $"after remote.connect: {cs.Code}, node {(p.Node ? p.NodeVersion : "missing")}, dsh {(p.Dsh ? p.DshVersion : "missing")}, host key {cs.SeenFingerprint}"
            : $"after remote.connect: {cs.Code}{(cs.SeenFingerprint is { } fp ? ", host key " + fp : "")}";
        return FromChecks(ConnectSlice, at, checks, true, detail);
    }

    public bool DshEnabled => options.RemoteDsh == SliceMode.LegacyWithShadow;

    /// <summary>
    /// After a legacy <c>remote.start</c> / <c>remote.stop</c> (PR-09 third segment, ADR-0013): C# checks on its own —
    /// through its own SSH.NET connection and its own forward to the remote DSH port, without starting anything —
    /// what the unauthenticated GET answers, and must reach the legacy verdict. Null when the legacy answer carries no
    /// verdict to check (start that never reached the forward, stop without a conclusion).
    /// </summary>
    public async Task<ShadowReport?> DshObservedAsync(string method, RemoteStatusInfo legacy, RemoteHostProfile? profile,
        Func<RemoteHostProfile, CancellationToken, Task<int?>> probeHttp, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(probeHttp);
        var applicable = method switch
        {
            BridgeMethods.RemoteStart => legacy.State == "READY",
            BridgeMethods.RemoteStop => legacy.RemoteDshStillRunning is not null,
            _ => false,
        };
        if (!DshEnabled || !applicable)
        {
            return null;
        }
        if (profile is null)
        {
            return Emit(Report(DshSlice, time.GetUtcNow(), ShadowVerdict.Inconclusive, true, false, $"after {method}: host {legacy.HostId ?? "-"} was not stored"));
        }
        var start = time.GetTimestamp();
        int? http;
        try
        {
            http = await probeHttp(profile, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Emit(Report(DshSlice, time.GetUtcNow(), ShadowVerdict.Inconclusive, true, false, $"after {method}: C# check failed: {e.GetType().Name}: {e.Message}"));
        }
        var ms = (long)time.GetElapsedTime(start).TotalMilliseconds;
        var report = method == BridgeMethods.RemoteStart ? CompareDshStart(legacy, http, time.GetUtcNow()) : CompareDshStop(legacy, http, time.GetUtcNow());
        return Emit(report with { CSharpMs = ms });
    }

    /// <summary>Legacy READY ↔ the C# GET through its own forward: same HTTP status, same verdict (401 → OK).</summary>
    public static ShadowReport CompareDshStart(RemoteStatusInfo legacy, int? csHttp, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        var cs = RemoteDshRules.AfterStart(csHttp);
        var checks = new[]
        {
            ("httpUnauthenticated", Show(legacy.HttpUnauthenticated), Show(csHttp)),
            ("code", legacy.Code, cs.Code),
        };
        return FromChecks(DshSlice, at, checks, true,
            $"after remote.start: remote DSH answers HTTP {Show(csHttp)} without the token through a C# forward too → {cs.Code}");
    }

    /// <summary>Legacy stop verdict ↔ the C# GET through its own forward (C# session up, so no answer = not running).</summary>
    public static ShadowReport CompareDshStop(RemoteStatusInfo legacy, int? csHttp, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        var cs = RemoteDshRules.StillRunning(true, csHttp, true);
        var checks = new[] { ("remoteDshStillRunning", Show(legacy.RemoteDshStillRunning), Show(cs)) };
        return FromChecks(DshSlice, at, checks, true,
            $"after remote.stop: C# forward got {(csHttp is null ? "no answer" : "HTTP " + Show(csHttp))} → still running {Show(cs)}");
    }

    private static string Show(int? i) => i is { } v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";

    private static string DescribeProbe(RemoteProbe? p) =>
        p is null ? "-" : $"node={p.Node}|{p.NodeVersion ?? "-"} dsh={p.Dsh}|{p.DshVersion ?? "-"}";

    private ShadowReport Emit(ShadowReport report)
    {
        sink.Report(report);
        log.Note($"shadow {report.Slice}: {report.Verdict} ({report.Detail})");
        return report;
    }

    private static RemoteHostProfile FromDto(RemoteHost h) =>
        new(h.Id, h.Label, h.Host, h.Port, h.User, h.DshPort, h.PinnedFingerprint);

    /// <summary>A stable, comparable text form of a profile list (never contains a secret: profiles have none).</summary>
    private static string Describe(IEnumerable<RemoteHostProfile> profiles) =>
        "[" + string.Join("; ", profiles.Select(p =>
            $"{p.Id}|{p.Label}|{p.User}@{p.Host}:{p.Port}|dsh {p.DshPort}|pin {p.PinnedFingerprint ?? "-"}")) + "]";

    private static string Show(bool? b) => b switch { true => "true", false => "false", null => "unknown" };

    private static ShadowReport FromChecks(string slice, DateTimeOffset at, IReadOnlyList<(string Name, string Legacy, string CSharp)> checks,
        bool csharpOk, string agreeDetail, bool legacyOk = true)
    {
        var diffs = checks.Where(c => c.Legacy != c.CSharp).ToList();
        var detail = diffs.Count == 0 ? agreeDetail : string.Join("; ", diffs.Select(d => $"{d.Name} legacy={d.Legacy} csharp={d.CSharp}"));
        return Report(slice, at, diffs.Count == 0 ? ShadowVerdict.Agree : ShadowVerdict.Diverge, legacyOk, csharpOk, detail);
    }

    private static ShadowReport Report(string slice, DateTimeOffset at, ShadowVerdict verdict, bool legacyOk, bool csharpOk, string detail) =>
        new(slice, at, verdict, legacyOk, null, csharpOk, null, detail.Length > 1500 ? detail[..1500] : detail, 0);
}
