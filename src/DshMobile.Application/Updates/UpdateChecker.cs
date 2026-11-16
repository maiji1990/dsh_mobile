using DshMobile.Runtime;
using DshMobile.Runtime.Updates;

namespace DshMobile.Application.Updates;

/// <summary>Platform facts the checker needs: the app's own version (PackageInfo.VersionName) and the device ABI.</summary>
public sealed record UpdateCheckOptions(string AppVersion, string DeviceAbi);

/// <summary>
/// Read-only update check (vNext PR-02 on top of ADR-0022): installed versions → the channel's anti-replay floor →
/// <see cref="ReleaseDiscovery"/> (trust, fetch, verify, catalog) → record the accepted <c>generatedAt</c>. Downloads
/// nothing but the two manifest files, installs nothing. Every failure is returned with its stable code; a check whose
/// acceptance could not be recorded is a failure (its offers are withheld, the replay floor would be lost). Checks are
/// serialized.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    private readonly RuntimeLayout _layout;
    private readonly ReleaseTrust _trust;
    private readonly Func<string, IReleaseSource> _sourceFor;
    private readonly UpdateCheckOptions _options;
    private readonly TimeProvider _time;
    private readonly ReleaseAcceptanceStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="sourceFor">The source of a channel's manifest; production: <see cref="HttpReleaseSource"/> on <see cref="ReleaseEndpoints.ManifestUrl"/>.</param>
    public UpdateChecker(RuntimeLayout layout, ReleaseTrust trust, Func<string, IReleaseSource> sourceFor, UpdateCheckOptions options, TimeProvider time)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _sourceFor = sourceFor ?? throw new ArgumentNullException(nameof(sourceFor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _store = new ReleaseAcceptanceStore(layout);
    }

    public async Task<ReleaseDiscoveryResult> CheckAsync(string channel, CancellationToken ct)
    {
        if (!ReleaseEndpoints.IsChannel(channel))
        {
            return Fail(RuntimeErrorCodes.BadRequest, $"unknown release channel '{channel}' (stable / preview)");
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var floor = _store.Read(channel);
            if (!floor.Ok)
            {
                return Fail(floor.Code, floor.Message);
            }
            var installed = InstalledVersionsReader.Read(_layout, _options.AppVersion);
            var r = await ReleaseDiscovery.CheckAsync(_sourceFor(channel), _trust, channel, installed, _options.DeviceAbi, floor.GeneratedAt, _time, ct)
                .ConfigureAwait(false);
            if (!r.Ok)
            {
                return r;
            }
            var accepted = _store.Accept(channel, r.Manifest!.GeneratedAt);
            return accepted.Ok ? r : Fail(accepted.Code, accepted.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static ReleaseDiscoveryResult Fail(string code, string? message) => new(false, code, message, null, null, []);

    /// <summary>One log line per row, plus the outcome; never "up to date" for a failed check.</summary>
    public static IEnumerable<string> Describe(string channel, ReleaseDiscoveryResult r)
    {
        if (!r.Ok)
        {
            yield return $"update check ({channel}) FAILED {r.Code}: {r.Message}";
            yield break;
        }
        yield return $"update check ({channel}): manifest {r.Manifest!.GeneratedAt:O} signed by {r.KeyId}, expires {r.Manifest.ExpiresAt:O}";
        foreach (var c in r.Choices)
        {
            var update = c.Update is { } u ? $"update to {u.Version} ({u.Size / (1024 * 1024)} MiB, sha256 {u.Sha256[..12]}…)" : "no applicable update";
            var blocked = c.Offers.Where(o => o.BlockedBy is not null).Select(o => $"{o.Artifact.Version}: {o.BlockedBy}").ToList();
            yield return $"  {c.Kind}: installed {c.Installed ?? "none"}; {c.Offers.Count} offer(s); {update}{(blocked.Count == 0 ? "" : "; blocked " + string.Join("; ", blocked))}";
        }
    }
}
