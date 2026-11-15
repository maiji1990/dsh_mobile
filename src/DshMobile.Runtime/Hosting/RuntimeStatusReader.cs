// Ported from DSHBox v1.5.1 (7c9d33c) sandbox-manager/.../DefaultSandboxManager.kt isRuntimeInstalled /
// isNodeLayerInstalled / dshVersion (dshLayer.installedVersion) and this repo's RuntimeController.kt status()
// (GPL-3.0-or-later). Registered in docs/UPSTREAMS.md.

using DshMobile.Runtime.Layers;

namespace DshMobile.Runtime.Hosting;

/// <summary>
/// What the legacy <c>runtime.status</c> reports about the installed runtime, read from disk by C# (MIGRATION row 23):
/// the install check (with the legacy sentinel self-heal), the node layer, the installed DSH layer version, the
/// hard-link shim and free space. Process state is not part of it: the C# host owns that.
/// </summary>
public sealed record RuntimeFacts(
    bool RuntimeInstalled,
    string InstallReason,
    bool NodeLayerInstalled,
    string? DshVersion,
    bool LinkShim,
    long FreeBytes)
{
    /// <summary>One status line in the legacy <c>LogStatus</c> wording (no token, no paths).</summary>
    public string Describe(string sandbox, string dsh, bool hasToken) =>
        $"sandbox={sandbox} dsh={dsh} version={DshVersion ?? "-"} runtime={RuntimeInstalled} node={NodeLayerInstalled} token={hasToken} shim={LinkShim}"
        + $" free={(FreeBytes < 0 ? "?" : (FreeBytes / 1024 / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture))} MiB"
        + (RuntimeInstalled ? "" : $" ({InstallReason})");
}

public static class RuntimeStatusReader
{
    /// <summary>
    /// Reads the facts. <paramref name="heal"/> = true is the legacy behavior of <c>isRuntimeInstalled</c> (a layer
    /// without its sentinel gets the profile digest recorded); it never touches anything else.
    /// </summary>
    public static RuntimeFacts Read(RuntimeLayout layout, bool heal = true)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var install = RuntimeInstallCheck.Run(layout, heal);
        var reason = install.Installed ? "ok" : install.Broken.Count > 0 ? $"{install.Reason}: {string.Join(", ", install.Broken)}" : install.Reason;
        string? version = null;
        try
        {
            version = new DshLayerInstaller(layout).InstalledVersion();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return new RuntimeFacts(
            install.Installed,
            reason,
            File.Exists(Path.Combine(layout.LayerDir("node"), "bin", "node")),
            version,
            File.Exists(layout.ShimFile),
            FreeBytes(layout.FilesDir));
    }

    /// <summary>
    /// The journal view of a runtime no process of which is running: <see cref="RuntimeState.Ready"/> when installed,
    /// otherwise <see cref="RuntimeState.NotInstalled"/> (legacy <c>LegacyStateMapper.MapSandbox(STOPPED, installed)</c>).
    /// </summary>
    public static RuntimeSnapshot Idle(RuntimeFacts facts, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return new RuntimeSnapshot(facts.RuntimeInstalled ? RuntimeState.Ready : RuntimeState.NotInstalled, HarnessState.Stopped, 0, facts.DshVersion, null, null, at);
    }

    /// <summary>Available bytes on the file system holding <paramref name="path"/> (legacy <c>StatFs.availableBytes</c>); -1 when unknown.</summary>
    public static long FreeBytes(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var drive = DriveInfo.GetDrives()
                .Where(d => full.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
                .MaxBy(d => d.RootDirectory.FullName.Length);
            return drive?.AvailableFreeSpace ?? -1;
        }
#pragma warning disable CA1031 // free space is informational; any failure reads as "unknown"
        catch (Exception)
#pragma warning restore CA1031
        {
            return -1;
        }
    }
}
