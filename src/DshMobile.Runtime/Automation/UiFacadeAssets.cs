// Ported from this repo's native/native-bridge/src/main/java/dshmobile/nativebridge/RuntimeController.kt
// (provisionGuestAssets: the ui_* facade copy; relay.selftest's fixed guest command; transition code). The two scripts are
// this repo's own guest assets (native/native-bridge/src/main/guest-assets/dshmobile/), embedded byte for byte from that
// single source until PR-10 moves them. Registered in docs/UPSTREAMS.md.

using System.Security.Cryptography;

namespace DshMobile.Runtime.Automation;

/// <summary>
/// The guest side of <c>ui_*</c> that the C# owner stages itself (ADR-0016, MIGRATION "执行层"): the MCP stdio facade
/// DSH loads through <see cref="UiToolsRegistration"/>'s layer, and its self-check. Legacy copied them from APK assets on
/// the first bridge call of every process; C# writes them (only when the bytes differ, via a temporary file and rename)
/// before it registers the layer and before the self-test, so the C# Relay path never depends on that side effect.
/// </summary>
public static class UiFacadeAssets
{
    public const string FacadeName = "ui-facade.mjs";
    public const string CheckName = "ui-facade-check.mjs";

    /// <summary>Host sub-directory of the guest cache dir (guest <c>/opt/dshbox-cache/dshmobile</c>).</summary>
    public const string HostSubDir = "dshmobile";

    /// <summary>Guest path of the self-check (legacy <c>GUEST_UI_CHECK</c>).</summary>
    public const string GuestCheck = RuntimeConstants.GuestCacheDir + "/" + HostSubDir + "/" + CheckName;

    /// <summary>
    /// Legacy <c>relay.selftest</c>, fixed and read-only: Relay's own readiness check through the real guest CLI, then the
    /// ui_* facade driven as an MCP stdio server exactly as DSH's dsh-mcp-client would. 50 s per approval keeps two
    /// approvals inside the page's 2 min deadline. No caller input reaches the shell. The exit code is the check's.
    /// </summary>
    public const string SelfTestCommand = "/opt/pilot/bin/relay doctor; echo; node " + GuestCheck + " --approval-wait-ms 50000";

    /// <summary>Stable error code: the scripts could not be written, so nothing that needs them is run or registered.</summary>
    public const string NotStaged = "UI_FACADE_NOT_STAGED";

    /// <summary>Legacy keeps the last 400 lines of the self-test.</summary>
    public const int SelfTestMaxLines = 400;

    private static readonly (string Name, string Resource)[] Files =
    [
        (FacadeName, "DshMobile.UiTools.ui-facade.mjs"),
        (CheckName, "DshMobile.UiTools.ui-facade-check.mjs"),
    ];

    public static string HostDir(string guestCacheHostDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(guestCacheHostDir);
        return Path.Combine(guestCacheHostDir, HostSubDir);
    }

    /// <summary>The embedded bytes of <paramref name="name"/> (<see cref="FacadeName"/> / <see cref="CheckName"/>).</summary>
    public static byte[] Read(string name)
    {
        var resource = Files.FirstOrDefault(f => f.Name == name).Resource
            ?? throw new ArgumentException($"not a ui_* guest asset: {name}", nameof(name));
        using var stream = typeof(UiFacadeAssets).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"embedded ui_* asset missing: {resource}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Writes both scripts under <c>&lt;guestCacheHostDir&gt;/dshmobile</c>; a file that already holds the same bytes is left
    /// alone. Returns how many files were (re)written. Throws <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>
    /// when the directory cannot be written: the caller reports it instead of registering a facade that is not there.
    /// </summary>
    public static int Stage(string guestCacheHostDir)
    {
        var dir = HostDir(guestCacheHostDir);
        Directory.CreateDirectory(dir);
        var written = 0;
        foreach (var (name, _) in Files)
        {
            var bytes = Read(name);
            var dest = Path.Combine(dir, name);
            if (File.Exists(dest) && SHA256.HashData(File.ReadAllBytes(dest)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
            {
                continue;
            }
            var tmp = dest + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, dest, overwrite: true);
            written++;
        }
        return written;
    }

    /// <summary>True when both scripts are present with exactly the embedded bytes.</summary>
    public static bool Intact(string guestCacheHostDir)
    {
        var dir = HostDir(guestCacheHostDir);
        return Files.All(f =>
        {
            var path = Path.Combine(dir, f.Name);
            return File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(Read(f.Name));
        });
    }
}
