namespace DshMobile.Runtime.Diagnostics;

/// <summary>
/// One throw-away guest home/workspace/cache under the app's private files dir, used by E3/E4 so a diagnostic Web start
/// never reads or writes <c>user-data</c> (docs/STARTUP_EXPERIMENTS.md). Only the staged wrapper scripts are copied from
/// production code; the user's sessions, credentials, MCP config and workspace are not touched.
/// </summary>
public sealed class IsolatedGuestLayout : IDisposable
{
    private static readonly string[] OverlayRelatives =
    [
        ".dsh/dshmobile/dsh-official-plugin/dsh-official-plugin-overlay.yml",
        ".dsh/dshmobile/plugin-market/market-and-safe-mode-overlay.yml",
        ".dsh/dshmobile/safe-mode/absolute-safe-mode.yml",
    ];

    private IsolatedGuestLayout(string root)
    {
        Root = root;
    }

    public string Root { get; }

    /// <summary>Bound to guest <c>/root/projects</c>.</summary>
    public string WorkspaceDir => Path.Combine(Root, "user-data");

    /// <summary>Bound to guest <c>/opt/dshmobile-cache</c>.</summary>
    public string CacheDir => Path.Combine(Root, "guest-cache");

    /// <summary>Host side of the guest <c>NODE_COMPILE_CACHE</c> directory.</summary>
    public string CompileCacheDir => Path.Combine(CacheDir, "experiments", "node-cache");

    public static IsolatedGuestLayout Create(RuntimeLayout production, string root)
    {
        ArgumentNullException.ThrowIfNull(production);
        ArgumentException.ThrowIfNullOrEmpty(root);
        var layout = new IsolatedGuestLayout(root);
        Directory.CreateDirectory(layout.WorkspaceDir);
        Directory.CreateDirectory(layout.CacheDir);
        Directory.CreateDirectory(Path.Combine(layout.WorkspaceDir, ".dsh"));
        // Production wrapper text (dsh / pnpm / xdg-user-dir), written into the isolated home only.
        var isolatedRuntime = new RuntimeLayout(layout.Root, null, production.NativeLibraryDir);
        Guest.GuestToolchain.WriteShims(isolatedRuntime);
        return layout;
    }

    /// <summary>Copies the registered overlay files (non-secret YAML) so E3-1 can measure <c>--patch</c>; names are fixed.</summary>
    public IReadOnlyList<string> CopyRegisteredOverlays(RuntimeLayout production)
    {
        ArgumentNullException.ThrowIfNull(production);
        var copied = new List<string>();
        foreach (var relative in OverlayRelatives)
        {
            var source = Path.Combine(production.UserDataDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source))
            {
                continue;
            }
            var target = Path.Combine(WorkspaceDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            copied.Add("/root/projects/" + relative);
        }
        return copied;
    }

    /// <summary>Removes this experiment's tree; never touches anything outside <see cref="Root"/>.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover temp tree is harmless; the suite root is removed by the runner as well.
        }
    }
}
