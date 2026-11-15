using System.Text;

namespace DshMobile.Runtime.Migration;

/// <summary>
/// ADR-0019: one-time move of on-device data from the DSHBox-era names (<c>dshbox</c>) to <c>dshmobile</c>. This is the
/// only code that still knows the old names. Every step checks what exists first, so running it again is harmless; a
/// failing step is reported and the others still run (never turned into success). It never overwrites or deletes a
/// user file: a directory is removed only when it is empty, or (<c>files/dshbox</c>) when it holds only the app's own
/// rewritable shims. The caller runs it while no guest process is alive.
/// </summary>
public static class DshboxNameMigration
{
    /// <summary>Old workspace dir (relative to user-data), old layer-state dir, old host shim dir (relative to files).</summary>
    public const string LegacyAssetsRelativeDir = ".dsh/dshbox";
    public const string LegacyLayerStateDir = ".dshbox";
    public const string LegacyShimDir = "dshbox";

    /// <summary>Written when every step succeeded; afterwards nothing is scanned again.</summary>
    public const string MarkerName = "names-migrated-v1";

    /// <summary>Old → new guest paths, longest first so <c>/opt/dshbox-cache</c> is not caught by <c>/opt/dshbox</c>.</summary>
    public static IReadOnlyList<(string Old, string New)> GuestPathRewrites { get; } =
    [
        ("/opt/dshbox-cache", RuntimeConstants.GuestCacheDir),
        ("/root/projects/.dsh/dshbox", RuntimeConstants.AssetsGuestDir),
        ("/opt/dshbox", RuntimeConstants.LinkShimGuestDir),
    ];

    private const long MaxRewriteBytes = 1024 * 1024;
    private static readonly string[] RewriteExtensions = [".yml", ".yaml", ".json"];

    public static string MarkerFile(RuntimeLayout layout) => Path.Combine(layout.HostStateDir, MarkerName);

    /// <summary>Runs the migration unless the marker says it already completed.</summary>
    public static DshboxMigrationReport Run(RuntimeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (File.Exists(MarkerFile(layout)))
        {
            return new DshboxMigrationReport(AlreadyDone: true, [], []);
        }
        var done = new List<string>();
        var failed = new List<string>();
        Step("workspace", () => MoveWorkspaceDir(layout, done), failed);
        Step("layer state", () => MoveLayerState(layout, done), failed);
        Step("guest paths", () => RewriteGuestPaths(layout, done), failed);
        Step("host shim dir", () => RemoveOldShimDir(layout, done), failed);
        if (failed.Count == 0)
        {
            Step("marker", () =>
            {
                Directory.CreateDirectory(layout.HostStateDir);
                File.WriteAllText(MarkerFile(layout), "ADR-0019\n");
            }, failed);
        }
        return new DshboxMigrationReport(AlreadyDone: false, done, failed);
    }

    private static void Step(string name, Action action, List<string> failed)
    {
        try
        {
            action();
        }
#pragma warning disable CA1031 // each step stands alone; the failure is reported, not hidden
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failed.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary><c>user-data/.dsh/dshbox</c> → <c>user-data/.dsh/dshmobile</c>; merged entry by entry when both exist.</summary>
    private static void MoveWorkspaceDir(RuntimeLayout layout, List<string> done)
    {
        var oldDir = Path.Combine([layout.UserDataDir, .. LegacyAssetsRelativeDir.Split('/')]);
        var newDir = Path.Combine([layout.UserDataDir, .. RuntimeConstants.AssetsRelativeDir.Split('/')]);
        if (!Directory.Exists(oldDir))
        {
            return;
        }
        if (!Directory.Exists(newDir))
        {
            Directory.Move(oldDir, newDir);
            done.Add($"moved {LegacyAssetsRelativeDir} -> {RuntimeConstants.AssetsRelativeDir}");
            return;
        }
        var moved = MergeInto(oldDir, newDir);
        done.Add($"merged {moved} entr{(moved == 1 ? "y" : "ies")} of {LegacyAssetsRelativeDir} into {RuntimeConstants.AssetsRelativeDir}");
        if (!DeleteIfEmpty(oldDir))
        {
            throw new IOException($"{LegacyAssetsRelativeDir} still has entries that also exist in {RuntimeConstants.AssetsRelativeDir}; left in place");
        }
    }

    /// <summary>Moves what the target lacks; recurses into directories that exist on both sides. Returns entries moved.</summary>
    private static int MergeInto(string from, string to)
    {
        var moved = 0;
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            var target = Path.Combine(to, entry.Name);
            var targetExists = File.Exists(target) || Directory.Exists(target);
            if (!targetExists)
            {
                if (entry is DirectoryInfo d)
                {
                    d.MoveTo(target);
                }
                else
                {
                    File.Move(entry.FullName, target);
                }
                moved++;
            }
            else if (entry is DirectoryInfo dir && Directory.Exists(target) && entry.LinkTarget is null)
            {
                moved += MergeInto(dir.FullName, target);
                DeleteIfEmpty(dir.FullName);
            }
        }
        return moved;
    }

    /// <summary>
    /// <c>&lt;layer&gt;/.dshbox/{layer-*.sha256, version}</c> → <c>&lt;layer&gt;/.dshmobile/</c> in runtime-current and
    /// runtime-current/previous. Anything else in <c>.dshbox</c> (the bundle's own <c>env.d</c>) stays.
    /// </summary>
    private static void MoveLayerState(RuntimeLayout layout, List<string> done)
    {
        foreach (var root in new[] { layout.RuntimeCurrentDir, Path.Combine(layout.RuntimeCurrentDir, "previous") })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }
            foreach (var layerDir in Directory.EnumerateDirectories(root))
            {
                var oldState = Path.Combine(layerDir, LegacyLayerStateDir);
                if (!Directory.Exists(oldState))
                {
                    continue;
                }
                var files = Directory.EnumerateFiles(oldState)
                    .Where(f => Path.GetFileName(f) is var n && (n == "version" || (n.StartsWith("layer-", StringComparison.Ordinal) && n.EndsWith(".sha256", StringComparison.Ordinal))))
                    .ToList();
                if (files.Count == 0)
                {
                    continue;
                }
                var newState = Path.Combine(layerDir, RuntimeConstants.LayerStateDir);
                Directory.CreateDirectory(newState);
                foreach (var f in files)
                {
                    var target = Path.Combine(newState, Path.GetFileName(f));
                    if (File.Exists(target))
                    {
                        File.Delete(f); // the installer already wrote the current record; the old copy is stale
                    }
                    else
                    {
                        File.Move(f, target);
                    }
                }
                DeleteIfEmpty(oldState);
                done.Add($"layer state {Path.GetRelativePath(layout.RuntimeDir, layerDir).Replace('\\', '/')}: {files.Count} file(s)");
            }
        }
    }

    /// <summary>Old guest paths in pnpm's <c>.modules.yaml</c> under the workspace <c>.dsh</c>, and in our own config files.</summary>
    private static void RewriteGuestPaths(RuntimeLayout layout, List<string> done)
    {
        var dshDir = Path.Combine(layout.UserDataDir, ".dsh");
        if (!Directory.Exists(dshDir))
        {
            return;
        }
        var candidates = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        candidates.AddRange(Directory.EnumerateFiles(dshDir, ".modules.yaml", options)
            .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "node_modules"));
        var ours = Path.Combine([layout.UserDataDir, .. RuntimeConstants.AssetsRelativeDir.Split('/')]);
        if (Directory.Exists(ours))
        {
            candidates.AddRange(Directory.EnumerateFiles(ours, "*", options)
                .Where(f => RewriteExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                    && !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));
        }
        var rewritten = 0;
        foreach (var file in candidates.Distinct(StringComparer.Ordinal))
        {
            if (new FileInfo(file).Length > MaxRewriteBytes)
            {
                continue;
            }
            var text = File.ReadAllText(file, Encoding.UTF8);
            var updated = text;
            foreach (var (oldPath, newPath) in GuestPathRewrites)
            {
                updated = updated.Replace(oldPath, newPath, StringComparison.Ordinal);
            }
            if (updated != text)
            {
                var tmp = file + ".tmp-migrate";
                File.WriteAllText(tmp, updated, new UTF8Encoding(false));
                File.Move(tmp, file, overwrite: true);
                rewritten++;
            }
        }
        if (rewritten > 0)
        {
            done.Add($"guest paths rewritten in {rewritten} file(s)");
        }
    }

    /// <summary><c>files/dshbox</c> holds only the app's link shims, which the provisioner rewrites under <c>files/dshmobile</c>.</summary>
    private static void RemoveOldShimDir(RuntimeLayout layout, List<string> done)
    {
        var old = Path.Combine(layout.FilesDir, LegacyShimDir);
        if (Directory.Exists(old))
        {
            Directory.Delete(old, recursive: true);
            done.Add($"removed files/{LegacyShimDir} (shims are rewritten under files/{Path.GetFileName(layout.ShimDir)})");
        }
    }

    private static bool DeleteIfEmpty(string dir)
    {
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            return true;
        }
        return !Directory.Exists(dir);
    }
}

/// <summary>What <see cref="DshboxNameMigration.Run"/> did; <see cref="Ok"/> is false while any step failed.</summary>
public sealed record DshboxMigrationReport(bool AlreadyDone, IReadOnlyList<string> Done, IReadOnlyList<string> Failed)
{
    public bool Ok => Failed.Count == 0;

    public bool Changed => Done.Count > 0;

    public string Describe() => AlreadyDone ? "already done"
        : $"{(Done.Count == 0 ? "nothing to migrate" : string.Join("; ", Done))}{(Failed.Count == 0 ? "" : " | FAILED: " + string.Join("; ", Failed))}";
}
