// Ported from this repo's native/native-bridge/src/main/java/dshmobile/nativebridge/RuntimeController.kt
// (provisionGuestAssets; GPL-3.0-or-later, partly from DSHBox v1.5.1 SandboxService). Registered in docs/UPSTREAMS.md.

using System.Security.Cryptography;
using DshMobile.Runtime.Automation;
using DshMobile.Runtime.Layers;

namespace DshMobile.Runtime.Guest;

/// <summary>Outcome of one <see cref="GuestAssetProvisioner.EnsureAsync"/>: what was (re)written and what could not be.</summary>
public sealed record GuestAssetReport(IReadOnlyList<string> Written, IReadOnlyList<string> Failures)
{
    public bool Ok => Failures.Count == 0;

    public string Describe() =>
        (Written.Count == 0 ? "up to date" : "written: " + string.Join(", ", Written))
        + (Failures.Count == 0 ? "" : "; FAILED: " + string.Join("; ", Failures));
}

/// <summary>
/// The host files the guest expects, staged by C# (MIGRATION "guest 资产"). Legacy <c>provisionGuestAssets</c> wrote them on
/// the first bridge call of every process, and every C# guest path (C# host, diagnostics, plugins, probe, Relay self-test,
/// terminal) silently depended on that side effect. Same files, same places:
/// <list type="bullet">
/// <item><c>dshmobile/link-shim.mjs</c> + <c>libdshmobile-link.so</c> (APK assets) → <c>files/dshmobile</c> (guest <c>/opt/dshmobile</c>):
/// DSH publishes with <c>link()</c>, which Android's data partition refuses; without the shim sessions, the write tool,
/// attachments and plugin installs fail with EPERM;</item>
/// <item>this repo's <c>dsh-probe.mjs</c> and the ui_* scripts → <c>guest-cache/dshmobile</c> (guest <c>/opt/dshmobile-cache/dshmobile</c>);</item>
/// <item>the offline corepack cache and the <c>dsh</c> / <c>pnpm</c> wrappers (<see cref="GuestToolchain"/>).</item>
/// </list>
/// Each step stands alone like the legacy <c>runCatching</c> blocks: one failing does not stop the others, and the
/// failure is reported (never turned into success). Files are rewritten only when their bytes differ, through a
/// temporary file and a rename. <c>browser-check.mjs</c> is not staged: <c>browser_*</c> stays on the legacy path, which
/// stages it itself.
/// </summary>
public static class GuestAssetProvisioner
{
    public const string ShimAssetDir = "dshmobile";
    public const string LinkShimAsset = ShimAssetDir + "/link-shim.mjs";
    public const string LinkShimSoName = "libdshmobile-link.so";
    public const string LinkShimSoAsset = ShimAssetDir + "/" + LinkShimSoName;

    public const string ProbeName = "dsh-probe.mjs";

    /// <summary>Guest path of the probe (legacy <c>GUEST_PROBE</c>).</summary>
    public const string GuestProbe = RuntimeConstants.GuestCacheDir + "/" + UiFacadeAssets.HostSubDir + "/" + ProbeName;

    private const string ProbeResource = "DshMobile.Guest.dsh-probe.mjs";

    /// <summary>The embedded bytes of this repo's <c>dsh-probe.mjs</c> (<c>Guest/Assets/</c>, moved from the bridge's guest assets in PR-10).</summary>
    public static byte[] ReadProbeScript()
    {
        using var stream = typeof(GuestAssetProvisioner).Assembly.GetManifestResourceStream(ProbeResource)
            ?? throw new InvalidOperationException($"embedded guest asset missing: {ProbeResource}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <param name="assets">The APK assets (link shim, corepack cache); null on hosts without them (the two steps then fail visibly).</param>
    public static async Task<GuestAssetReport> EnsureAsync(RuntimeLayout layout, IBundledAssets? assets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var written = new List<string>();
        var failures = new List<string>();

        if (assets is null)
        {
            failures.Add("link shim: no APK assets");
        }
        else
        {
            Step(failures, "link shim", () =>
            {
                Directory.CreateDirectory(layout.ShimDir);
                if (WriteIfDifferent(layout.ShimFile, ReadAsset(assets, LinkShimAsset)))
                {
                    written.Add("link-shim.mjs");
                }
                if (WriteIfDifferent(Path.Combine(layout.ShimDir, LinkShimSoName), ReadAsset(assets, LinkShimSoAsset)))
                {
                    written.Add(LinkShimSoName);
                }
            });
        }
        Step(failures, "probe script", () =>
        {
            var dir = UiFacadeAssets.HostDir(layout.GuestCacheDir);
            Directory.CreateDirectory(dir);
            if (WriteIfDifferent(Path.Combine(dir, ProbeName), ReadProbeScript()))
            {
                written.Add(ProbeName);
            }
        });
        Step(failures, "ui facade", () =>
        {
            if (UiFacadeAssets.Stage(layout.GuestCacheDir) is var n and > 0)
            {
                written.Add($"ui_* scripts ({n})");
            }
        });
        if (assets is null)
        {
            failures.Add("corepack cache: no APK assets");
        }
        else
        {
            var (changed, error) = await GuestToolchain.ProvisionCorepackAsync(layout, assets, ct).ConfigureAwait(false);
            if (error is not null)
            {
                failures.Add(error);
            }
            else if (changed)
            {
                written.Add("corepack cache (pnpm 11.1.2)");
            }
        }
        Step(failures, "dsh/pnpm wrappers", () =>
        {
            if (GuestToolchain.WriteShims(layout) is var n and > 0)
            {
                written.Add($"dsh/pnpm wrappers ({n})");
            }
        });
        return new GuestAssetReport(written, failures);
    }

    /// <summary>Writes <paramref name="bytes"/> unless the file already holds exactly them; temp file + rename. True when written.</summary>
    public static bool WriteIfDifferent(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(bytes);
        if (File.Exists(path) && new FileInfo(path).Length == bytes.Length
            && SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
        {
            return false;
        }
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        return true;
    }

    private static byte[] ReadAsset(IBundledAssets assets, string path)
    {
        using var stream = assets.Open(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void Step(List<string> failures, string name, Action body)
    {
        try
        {
            body();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            failures.Add($"{name}: {e.Message}");
        }
    }
}
