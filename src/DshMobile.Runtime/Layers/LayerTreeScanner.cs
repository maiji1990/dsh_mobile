using System.Security.Cryptography;

namespace DshMobile.Runtime.Layers;

/// <summary>
/// Reads an installed layer directory into the same manifest shape the extractor produces, without following
/// symlinks. Used to compare what the legacy installer put on disk with the C# extraction of the same archive.
/// </summary>
public static class LayerTreeScanner
{
    /// <param name="exclude">Root-relative paths to leave out (e.g. sentinels the installer adds after extraction).</param>
    public static async Task<IReadOnlyList<LayerEntry>> ScanAsync(string rootDir, IReadOnlySet<string>? exclude, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDir);
        var root = Path.GetFullPath(rootDir);
        var result = new List<LayerEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (exclude?.Contains(rel) == true)
                {
                    continue;
                }
                var info = new FileInfo(path);
                if (info.LinkTarget is { } link)
                {
                    result.Add(new LayerEntry(rel, LayerEntryKind.Symlink, null, 0, null, link));
                }
                else if (info.Attributes.HasFlag(FileAttributes.Directory))
                {
                    result.Add(new LayerEntry(rel, LayerEntryKind.Dir, Mode(path), 0, null, null));
                    pending.Push(path);
                }
                else
                {
                    var fs = File.OpenRead(path);
                    await using (fs.ConfigureAwait(false))
                    {
                        var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
                        result.Add(new LayerEntry(rel, LayerEntryKind.File, Mode(path), fs.Length, sha, null));
                    }
                }
            }
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return result;
    }

    private static string? Mode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }
        var m = File.GetUnixFileMode(path);
        return string.Create(3, m, static (s, v) =>
        {
            s[0] = v.HasFlag(UnixFileMode.UserRead) ? 'r' : '-';
            s[1] = v.HasFlag(UnixFileMode.UserWrite) ? 'w' : '-';
            s[2] = v.HasFlag(UnixFileMode.UserExecute) ? 'x' : '-';
        });
    }
}
