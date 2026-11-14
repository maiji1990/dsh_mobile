using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DshMobile.Runtime;
using DshMobile.Runtime.Layers;

namespace DshMobile.Application.Shadow;

/// <summary>What the C# preparer would do, read before the legacy <c>runtime.prepare</c> runs (nothing written).</summary>
public sealed record DshProvisionPrediction(bool EmbeddedRuntime, bool RuntimeInstalled, BundledDshPlan Plan, long Ms)
{
    public static DshProvisionPrediction Read(RuntimeLayout layout, IBundledAssets assets, Func<string, bool>? hostPathExists)
    {
        var sw = Stopwatch.StartNew();
        var embedded = RuntimePreparer.EmbeddedRuntimeWouldInstall(layout, assets);
        var installed = RuntimeInstallCheck.Run(layout, heal: false).Installed;
        var plan = BundledDsh.Plan(assets, new DshLayerInstaller(layout, hostPathExists));
        return new DshProvisionPrediction(embedded, installed, plan, sw.ElapsedMilliseconds);
    }
}

/// <summary>
/// Shadow for the bundled-DSH provisioning slice (<c>runtime.prepare</c>): the legacy bridge decides; C# predicted its
/// steps beforehand from the same assets and layer state, and when a layer was (or would be) installed, dry-runs the
/// same asset — checksum, extraction without writing, shape check — and compares the result with what the legacy code
/// put in <c>runtime-current/dsh</c>.
/// </summary>
public static class DshProvisionShadowCheck
{
    public const string Slice = "runtime.dsh-provision";
    private const string FailedPrefix = "DSH provision failed: ";

    public static async Task<ShadowReport> CompareAsync(
        DshProvisionPrediction before, RuntimeInstallResult legacy, RuntimeLayout layout, IBundledAssets assets,
        Func<string, bool>? hostPathExists, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(legacy);
        var sw = Stopwatch.StartNew();
        if (before.EmbeddedRuntime)
        {
            return Report(ShadowVerdict.Inconclusive, legacy, "the APK embeds a full runtime: not ported to C#", sw, before, time);
        }
        if (!legacy.Ok)
        {
            return Report(ShadowVerdict.Inconclusive, legacy, $"legacy prepare failed: {legacy.Message}", sw, before, time);
        }
        // The adapter joins the steps with "; ", which step 1 itself contains: strip the known first step instead of splitting.
        var message = legacy.Message ?? "";
        var prefix = RuntimePreparer.NoEmbeddedRuntime + "; ";
        var legacyStep2 = message.StartsWith(prefix, StringComparison.Ordinal) ? message[prefix.Length..] : null;
        var step2 = await PredictStep2Async(before, layout, assets, hostPathExists, ct).ConfigureAwait(false);
        var details = new List<string>();
        var agree = legacyStep2 is not null && Same(legacyStep2, step2.Step);
        details.Add(agree ? $"steps equal ({legacyStep2})" : $"steps: legacy=[{message}] csharp=[{prefix}{step2.Step}]");

        if (agree && step2.Entries is { } entries && legacyStep2!.EndsWith(" installed", StringComparison.Ordinal))
        {
            var installer = new DshLayerInstaller(layout, hostPathExists);
            var actual = await LayerTreeScanner.ScanAsync(installer.DshDir, null, ct).ConfigureAwait(false);
            var diff = LayerManifest.Diff(WithVersionRecord(entries, before.Plan.Asset!.Version), actual);
            if (diff.Count > 0)
            {
                agree = false;
                details.Add($"dsh layer: {diff.Count}{(diff.Count >= 20 ? "+" : "")} differences: {string.Join("; ", diff.Take(5))}");
            }
            else
            {
                details.Add($"dsh layer: {actual.Count} entries identical");
            }
        }
        return Report(agree ? ShadowVerdict.Agree : ShadowVerdict.Diverge, legacy, string.Join(" | ", details), sw, before, time);
    }

    /// <summary>Failure texts are compared by code only (exception messages differ between JVM and .NET).</summary>
    private static bool Same(string legacy, string csharp) =>
        legacy.StartsWith(FailedPrefix, StringComparison.Ordinal) && csharp.StartsWith(FailedPrefix, StringComparison.Ordinal)
            ? Code(legacy) == Code(csharp)
            : legacy == csharp;

    private static string Code(string failedStep) => failedStep[FailedPrefix.Length..].Split(' ')[0];

    private static async Task<(string Step, IReadOnlyList<LayerEntry>? Entries)> PredictStep2Async(
        DshProvisionPrediction before, RuntimeLayout layout, IBundledAssets assets, Func<string, bool>? hostPathExists, CancellationToken ct)
    {
        if (!before.RuntimeInstalled)
        {
            return (RuntimePreparer.LayersMissing, null);
        }
        if (before.Plan.Asset is not { } asset)
        {
            return (BundledDsh.NoBaseline, null);
        }
        if (!before.Plan.WouldInstall)
        {
            return ($"DSH kept at {before.Plan.Decision!.Installed}", null);
        }
        try
        {
            if (asset.HasSidecar)
            {
                string sha;
                using (var reader = new StreamReader(assets.Open(asset.SidecarPath), Encoding.UTF8))
                {
                    sha = BundledDsh.ParseSidecar(await reader.ReadToEndAsync(ct).ConfigureAwait(false));
                }
                var s = assets.Open(asset.AssetPath);
                await using (s.ConfigureAwait(false))
                {
                    var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct).ConfigureAwait(false));
                    if (sha.Length == 0 || !actual.Equals(sha, StringComparison.OrdinalIgnoreCase))
                    {
                        return ($"{FailedPrefix}{DshLayerErrorCodes.Sha256Mismatch} dsh bundle failed SHA-256 verification", null);
                    }
                }
            }
            LayerExtractResult extracted;
            var raw = assets.Open(asset.AssetPath);
            await using (raw.ConfigureAwait(false))
            {
                extracted = await LayerExtractor.ExtractAsync(raw, DryRunExtractionTarget.Instance, ct, hostPathExists).ConfigureAwait(false);
            }
            if (!extracted.Ok)
            {
                return ($"{FailedPrefix}{extracted.Code} {extracted.Message}", null);
            }
            if (!extracted.Entries.Any(e => e.Path == DshLayerInstaller.BinJs && e.Kind == LayerEntryKind.File))
            {
                return ($"{FailedPrefix}{DshLayerErrorCodes.BundleInvalid} missing bin.js", null);
            }
            return ($"DSH {asset.Version} installed", extracted.Entries);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ($"{FailedPrefix}{DshLayerErrorCodes.InstallFailed} {e.Message}", null);
        }
    }

    /// <summary>The installer writes <c>.dshbox/version</c> (the incoming version, no newline) after extracting.</summary>
    internal static List<LayerEntry> WithVersionRecord(IReadOnlyList<LayerEntry> entries, string version)
    {
        var list = entries.ToList();
        if (!list.Any(e => e.Path == ".dshbox"))
        {
            list.Add(new LayerEntry(".dshbox", LayerEntryKind.Dir, null, 0, null, null));
        }
        var bytes = Encoding.UTF8.GetBytes(version);
        list.RemoveAll(e => e.Path == DshLayerInstaller.VersionFile);
        list.Add(new LayerEntry(DshLayerInstaller.VersionFile, LayerEntryKind.File, null, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), null));
        return list;
    }

    private static ShadowReport Report(ShadowVerdict verdict, RuntimeInstallResult legacy, string detail, Stopwatch sw, DshProvisionPrediction before, TimeProvider time) =>
        new(Slice, time.GetUtcNow(), verdict, legacy.Ok, legacy.UpstreamCode ?? (legacy.Ok ? null : legacy.Code),
            verdict != ShadowVerdict.Diverge, null, detail, sw.ElapsedMilliseconds + before.Ms);
}
