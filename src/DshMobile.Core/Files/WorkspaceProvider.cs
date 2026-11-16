using System.Security.Cryptography;
using System.Text.Json;

namespace DshMobile.Core.Files;

/// <summary>
/// The app's DSH workspace as an <see cref="IFileProvider"/> (vNext PR-09). Every path rule is the
/// <see cref="WorkspaceFileSystem"/>'s (relative only, no <c>..</c>, never through a symlink, <c>.dsh</c> protected);
/// on top of it:
/// <list type="bullet">
/// <item>Nothing is created, written, moved, copied or restored <b>into</b> <c>.dsh</c>, and nothing in it is read,
/// changed or deleted — the protected tree is list-only. The root itself cannot be deleted or renamed.</item>
/// <item>Symlinks are entries in their own right: they can be renamed, moved and deleted (the link, never its target)
/// but are not read, and copies leave them out (counted in <see cref="TransferResult.SkippedLinks"/>).</item>
/// <item>Writes go to a temp file in the same directory and replace the target by rename, keeping the target's Unix
/// mode (an edited script stays executable); a save against a changed file is <see cref="WorkspaceErrors.Conflict"/>.</item>
/// <item>Deletes move entries into <c>trashDirectory</c> (outside the workspace, same filesystem: a rename) with a
/// manifest, so they can be restored; without a trash directory they are permanent and <see cref="Capabilities"/>
/// lacks <see cref="FileCapabilities.Trash"/>.</item>
/// <item>Copies and moves never overwrite: a taken name becomes "name (1).ext".</item>
/// </list>
/// </summary>
public sealed partial class WorkspaceProvider : IFileProvider
{
    private const string Manifest = "manifest.json";
    private readonly WorkspaceFileSystem _fs;
    private readonly string? _trash;
    private readonly TimeProvider _time;

    public WorkspaceProvider(WorkspaceFileSystem fs, string? trashDirectory, string displayRoot = "/root/projects", TimeProvider? time = null)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        DisplayRoot = displayRoot;
        _time = time ?? TimeProvider.System;
        if (trashDirectory is not null)
        {
            _trash = Path.GetFullPath(trashDirectory);
            var root = _fs.Root + Path.DirectorySeparatorChar;
            if (_trash == _fs.Root || _trash.StartsWith(root, StringComparison.Ordinal))
            {
                throw new ArgumentException("the trash must be outside the workspace (DSH would see and index it)", nameof(trashDirectory));
            }
            Directory.CreateDirectory(_trash);
        }
    }

    public string Id => "workspace";

    public string DisplayRoot { get; }

    public WorkspaceFileSystem FileSystem => _fs;

    public FileCapabilities Capabilities =>
        FileCapabilities.List | FileCapabilities.Read | FileCapabilities.Write | FileCapabilities.Create | FileCapabilities.Delete
        | FileCapabilities.Rename | FileCapabilities.Copy | FileCapabilities.Move | FileCapabilities.Archive | FileCapabilities.Search
        | (_trash is null ? FileCapabilities.None : FileCapabilities.Trash);

    public FileCapabilities CapabilitiesOf(WorkspaceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Protected)
        {
            return entry.Kind == WorkspaceEntryKind.Directory ? FileCapabilities.List : FileCapabilities.None;
        }
        const FileCapabilities change = FileCapabilities.Delete | FileCapabilities.Rename | FileCapabilities.Move;
        return entry.Kind switch
        {
            WorkspaceEntryKind.Directory => FileCapabilities.List | FileCapabilities.Create | FileCapabilities.Copy | FileCapabilities.Archive | change,
            WorkspaceEntryKind.File => FileCapabilities.Read | FileCapabilities.Write | FileCapabilities.Copy | FileCapabilities.Archive | change,
            WorkspaceEntryKind.Symlink => change,
            _ => FileCapabilities.Delete,
        } & Capabilities;
    }

    public Task<IReadOnlyList<WorkspaceEntry>> ListAsync(string directory, CancellationToken ct) =>
        Task.Run(() => _fs.List(directory), ct);

    public Task<WorkspaceEntry> StatAsync(string path, CancellationToken ct) => Task.FromResult(Stat(path));

    public WorkspaceEntry Stat(string path)
    {
        var norm = WorkspaceFileSystem.NormalizeRelative(path);
        if (norm.Length == 0)
        {
            var root = new DirectoryInfo(_fs.Root);
            return new WorkspaceEntry("", "", WorkspaceEntryKind.Directory, 0, root.LastWriteTimeUtc, false, null);
        }
        var full = EntryPath(norm);
        FileSystemInfo info = Directory.Exists(full) && !WorkspaceFileSystem.IsLink(full) ? new DirectoryInfo(full) : new FileInfo(full);
        if (!info.Exists && info.LinkTarget is null)
        {
            throw new WorkspaceException(WorkspaceErrors.NotFound, norm);
        }
        var kind = info.LinkTarget is not null ? WorkspaceEntryKind.Symlink
            : info is DirectoryInfo ? WorkspaceEntryKind.Directory
            : File.Exists(full) ? WorkspaceEntryKind.File : WorkspaceEntryKind.Other;
        return new WorkspaceEntry(info.Name, norm, kind, kind == WorkspaceEntryKind.File ? ((FileInfo)info).Length : 0,
            info.LastWriteTimeUtc, WorkspaceFileSystem.IsProtected(norm), info.LinkTarget);
    }

    public Task<Stream> OpenReadAsync(string path, long? maxBytes, CancellationToken ct) =>
        Task.FromResult<Stream>(_fs.OpenRead(path, maxBytes));

    public Task<FileVersion> VersionAsync(string path, CancellationToken ct) => Task.Run(() => Version(path, ct), ct);

    public FileVersion Version(string path, CancellationToken ct = default)
    {
        var norm = WorkspaceFileSystem.NormalizeRelative(path);
        RequireUnprotected(norm);
        var full = _fs.Resolve(norm);
        if (!File.Exists(full))
        {
            throw new WorkspaceException(Directory.Exists(full) ? WorkspaceErrors.NotAFile : WorkspaceErrors.NotFound, norm);
        }
        return VersionOfFull(full, ct);
    }

    private static FileVersion VersionOfFull(string full, CancellationToken ct)
    {
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        ct.ThrowIfCancellationRequested();
        var info = new FileInfo(full);
        return new FileVersion(info.Length, info.LastWriteTimeUtc, hash);
    }

    public async Task<FileVersion> WriteAsync(string path, Stream content, FileVersion? expected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var norm = RequireChangeable(path);
        var (parent, name) = Split(norm);
        RequireUnprotected(norm);
        var dir = RequireDirectory(parent);
        var target = Path.Combine(dir, name);
        if (WorkspaceFileSystem.IsLink(target))
        {
            throw new WorkspaceException(WorkspaceErrors.ThroughSymlink, $"'{norm}' is a symlink");
        }
        if (Directory.Exists(target))
        {
            throw new WorkspaceException(WorkspaceErrors.NotAFile, norm);
        }
        CheckExpected(norm, target, expected, ct);
        var tmp = Path.Combine(dir, $".{name}.{Guid.NewGuid():N}.part");
        try
        {
            await using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            {
                await content.CopyToAsync(output, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            if (File.Exists(target) && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmp, File.GetUnixFileMode(target)); // an edited script stays executable
            }
            CheckExpected(norm, target, expected, ct); // again, right before the swap
            File.Move(tmp, target, overwrite: expected is not null);
        }
        catch (IOException e) when (e is not FileNotFoundException && File.Exists(target) && expected is null)
        {
            throw new WorkspaceException(WorkspaceErrors.Exists, norm);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
        return VersionOfFull(target, ct);
    }

    private static void CheckExpected(string norm, string target, FileVersion? expected, CancellationToken ct)
    {
        if (expected is null)
        {
            if (File.Exists(target) || Directory.Exists(target) || WorkspaceFileSystem.IsLink(target))
            {
                throw new WorkspaceException(WorkspaceErrors.Exists, norm);
            }
            return;
        }
        if (!File.Exists(target))
        {
            throw new WorkspaceException(WorkspaceErrors.Conflict, $"'{norm}' was deleted or moved since it was opened");
        }
        var info = new FileInfo(target);
        // Length / mtime first (cheap); the hash decides when they agree, so a same-size rewrite within the mtime
        // granularity is still caught.
        if (info.Length != expected.Length || VersionOfFull(target, ct).Sha256 != expected.Sha256)
        {
            throw new WorkspaceException(WorkspaceErrors.Conflict, $"'{norm}' changed since it was opened");
        }
    }

    public Task<string> CreateDirectoryAsync(string parent, string name, CancellationToken ct) => Task.FromResult(CreateDirectory(parent, name));

    public string CreateDirectory(string parent, string name)
    {
        var (dir, rel) = NewChild(parent, name);
        var full = Path.Combine(dir, Path.GetFileName(rel));
        Directory.CreateDirectory(full);
        return rel;
    }

    public Task<string> CreateFileAsync(string parent, string name, CancellationToken ct) => Task.FromResult(CreateFile(parent, name));

    public string CreateFile(string parent, string name)
    {
        var (dir, rel) = NewChild(parent, name);
        try
        {
            using (new FileStream(Path.Combine(dir, Path.GetFileName(rel)), FileMode.CreateNew, FileAccess.Write))
            {
            }
        }
        catch (IOException) when (Exists(Path.Combine(dir, Path.GetFileName(rel))))
        {
            throw new WorkspaceException(WorkspaceErrors.Exists, rel);
        }
        return rel;
    }

    private (string Dir, string Rel) NewChild(string parent, string name)
    {
        var clean = StrictName(name);
        var parentNorm = WorkspaceFileSystem.NormalizeRelative(parent);
        var rel = Join(parentNorm, clean);
        RequireUnprotected(rel);
        var dir = RequireDirectory(parentNorm);
        if (Exists(Path.Combine(dir, clean)))
        {
            throw new WorkspaceException(WorkspaceErrors.Exists, rel);
        }
        return (dir, rel);
    }

    public Task<string> RenameAsync(string path, string newName, CancellationToken ct) => Task.FromResult(Rename(path, newName));

    public string Rename(string path, string newName)
    {
        var norm = RequireChangeable(path);
        RequireUnprotected(norm); // .dsh itself cannot be renamed away (its contents would become editable)
        var clean = StrictName(newName);
        var (parent, name) = Split(norm);
        var target = Join(parent, clean);
        RequireUnprotected(target);
        var dir = RequireDirectory(parent);
        var src = Path.Combine(dir, name);
        var dst = Path.Combine(dir, clean);
        if (!Exists(src))
        {
            throw new WorkspaceException(WorkspaceErrors.NotFound, norm);
        }
        if (name == clean)
        {
            return norm;
        }
        var caseOnly = string.Equals(name, clean, StringComparison.OrdinalIgnoreCase);
        if (Exists(dst) && !(caseOnly && SameEntry(src, dst)))
        {
            throw new WorkspaceException(WorkspaceErrors.Exists, target);
        }
        if (caseOnly)
        {
            // A case-insensitive filesystem treats a → A as "already exists": go through a temp name.
            var tmp = Path.Combine(dir, $".{name}.{Guid.NewGuid():N}.rename");
            MoveEntry(src, tmp);
            MoveEntry(tmp, dst);
        }
        else
        {
            MoveEntry(src, dst);
        }
        return target;
    }

    public Task<TransferResult> CopyAsync(IReadOnlyList<string> paths, string targetDirectory, CancellationToken ct) =>
        Task.Run(() => Transfer(paths, targetDirectory, copy: true, ct), ct);

    public Task<TransferResult> MoveAsync(IReadOnlyList<string> paths, string targetDirectory, CancellationToken ct) =>
        Task.Run(() => Transfer(paths, targetDirectory, copy: false, ct), ct);

    public TransferResult Transfer(IReadOnlyList<string> paths, string targetDirectory, bool copy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var targetNorm = WorkspaceFileSystem.NormalizeRelative(targetDirectory);
        if (WorkspaceFileSystem.IsProtected(targetNorm) || targetNorm == ".dsh")
        {
            throw new WorkspaceException(WorkspaceErrors.Protected, $"'{targetNorm}' is protected");
        }
        var targetDir = RequireDirectory(targetNorm);
        var created = new List<string>();
        var failed = new List<FileFailure>();
        var skipped = 0;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var norm = RequireChangeable(path);
                RequireUnprotected(norm);
                var (parent, name) = Split(norm);
                var src = Path.Combine(RequireDirectory(parent), name);
                var isLink = WorkspaceFileSystem.IsLink(src);
                if (!Exists(src))
                {
                    throw new WorkspaceException(WorkspaceErrors.NotFound, norm);
                }
                var isDir = !isLink && Directory.Exists(src);
                if (isDir && (targetDir == src || targetDir.StartsWith(src + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                {
                    throw new WorkspaceException(WorkspaceErrors.IntoItself, $"'{norm}' cannot go into itself");
                }
                if (!copy && parent == targetNorm)
                {
                    created.Add(norm); // moving into its own directory: nothing to do
                    continue;
                }
                if (copy && isLink)
                {
                    skipped++;
                    failed.Add(new FileFailure(norm, WorkspaceErrors.ThroughSymlink, "symlinks are not copied"));
                    continue;
                }
                var finalName = UniqueName(targetDir, name);
                var dst = Path.Combine(targetDir, finalName);
                if (copy)
                {
                    var staging = Path.Combine(targetDir, $".{finalName}.{Guid.NewGuid():N}.part");
                    try
                    {
                        skipped += isDir ? CopyTree(src, staging, ct) : CopyFile(src, staging, ct);
                        MoveEntry(staging, dst);
                    }
                    finally
                    {
                        DeleteQuietly(staging);
                    }
                }
                else
                {
                    MoveEntry(src, dst);
                }
                created.Add(Join(targetNorm, finalName));
            }
            catch (WorkspaceException e)
            {
                failed.Add(new FileFailure(path, e.Code, e.Message));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add(new FileFailure(path, WorkspaceErrors.IoError, e.Message));
            }
        }
        return new TransferResult(created, failed, skipped);
    }

    public Task<DeleteReceipt> DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct) => Task.Run(() => Delete(paths, ct), ct);

    public DeleteReceipt Delete(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var id = _time.GetUtcNow().ToString("yyyyMMdd'T'HHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var batch = _trash is null ? null : Path.Combine(_trash, id);
        var deleted = new List<string>();
        var failed = new List<FileFailure>();
        var items = new List<(string Path, string Stored)>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var norm = RequireChangeable(path);
                RequireUnprotected(norm);
                var (parent, name) = Split(norm);
                var src = Path.Combine(RequireDirectory(parent), name);
                if (!Exists(src))
                {
                    throw new WorkspaceException(WorkspaceErrors.NotFound, norm);
                }
                if (batch is null)
                {
                    DeleteEntry(src);
                }
                else
                {
                    Directory.CreateDirectory(batch);
                    var stored = items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    MoveEntry(src, Path.Combine(batch, stored));
                    items.Add((norm, stored));
                    WriteManifest(batch, items); // after every item: a crash still leaves a restorable batch
                }
                deleted.Add(norm);
            }
            catch (WorkspaceException e)
            {
                failed.Add(new FileFailure(path, e.Code, e.Message));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add(new FileFailure(path, WorkspaceErrors.IoError, e.Message));
            }
        }
        return new DeleteReceipt(id, deleted, failed, batch is not null && items.Count > 0);
    }

    public Task<IReadOnlyList<string>> RestoreAsync(DeleteReceipt receipt, CancellationToken ct) => Task.Run(() => Restore(receipt), ct);

    public IReadOnlyList<string> Restore(DeleteReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (_trash is null)
        {
            throw new WorkspaceException(WorkspaceErrors.NotSupported, "this workspace has no trash");
        }
        if (receipt.Id.Contains('/') || receipt.Id.Contains('\\') || receipt.Id.Contains("..", StringComparison.Ordinal))
        {
            throw new WorkspaceException(WorkspaceErrors.InvalidName, receipt.Id);
        }
        var batch = Path.Combine(_trash, receipt.Id);
        var manifest = Path.Combine(batch, Manifest);
        if (!File.Exists(manifest))
        {
            throw new WorkspaceException(WorkspaceErrors.TrashGone, receipt.Id);
        }
        var restored = new List<string>();
        foreach (var (path, stored) in ReadManifest(manifest))
        {
            var src = Path.Combine(batch, stored);
            if (!Exists(src))
            {
                continue;
            }
            // Back where it was if that directory still exists (and is still safe), else at the root.
            string parent;
            string name;
            try
            {
                (parent, name) = Split(WorkspaceFileSystem.NormalizeRelative(path));
                RequireUnprotected(path);
                RequireDirectory(parent);
            }
            catch (WorkspaceException)
            {
                (parent, name) = ("", Split(path).Name);
            }
            var dir = RequireDirectory(parent);
            var finalName = UniqueName(dir, name);
            MoveEntry(src, Path.Combine(dir, finalName));
            restored.Add(Join(parent, finalName));
        }
        DeleteQuietly(batch);
        return restored;
    }

    /// <summary>Deletes trashed batches older than <paramref name="age"/>. Returns how many went.</summary>
    public int PurgeTrash(TimeSpan age)
    {
        if (_trash is null || !Directory.Exists(_trash))
        {
            return 0;
        }
        var cutoff = _time.GetUtcNow() - age;
        var purged = 0;
        foreach (var dir in Directory.EnumerateDirectories(_trash))
        {
            var created = Directory.GetCreationTimeUtc(dir);
            if (created <= cutoff.UtcDateTime)
            {
                DeleteQuietly(dir);
                purged++;
            }
        }
        return purged;
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>A path that may be changed: not the root, not inside <c>.dsh</c>, not <c>.dsh</c> itself.</summary>
    private static string RequireChangeable(string? path)
    {
        var norm = WorkspaceFileSystem.NormalizeRelative(path);
        if (norm.Length == 0)
        {
            throw new WorkspaceException(WorkspaceErrors.NotSupported, "the workspace root cannot be changed");
        }
        return norm;
    }

    private static void RequireUnprotected(string norm)
    {
        if (WorkspaceFileSystem.IsProtected(WorkspaceFileSystem.NormalizeRelative(norm)))
        {
            throw new WorkspaceException(WorkspaceErrors.Protected, $"'{norm}' is protected");
        }
    }

    /// <summary>An existing directory reached without symlinks.</summary>
    private string RequireDirectory(string norm)
    {
        var full = _fs.Resolve(norm);
        if (!Directory.Exists(full))
        {
            throw new WorkspaceException(File.Exists(full) ? WorkspaceErrors.NotADirectory : WorkspaceErrors.NotFound, norm);
        }
        return full;
    }

    /// <summary>Host path of an entry whose parent is reached without symlinks; the entry itself may be a link.</summary>
    private string EntryPath(string norm)
    {
        var (parent, name) = Split(norm);
        return Path.Combine(_fs.Resolve(parent), name);
    }

    private static (string Parent, string Name) Split(string norm)
    {
        var i = norm.LastIndexOf('/');
        return i < 0 ? ("", norm) : (norm[..i], norm[(i + 1)..]);
    }

    private static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

    /// <summary>A name typed by the user: must already be a single clean component (no silent trimming of '/').</summary>
    private static string StrictName(string? name)
    {
        var clean = WorkspaceFileSystem.SanitizeFileName(name);
        if (clean != name || clean.Contains('/') || clean.Contains('\\'))
        {
            throw new WorkspaceException(WorkspaceErrors.InvalidName, $"invalid file name '{name}'");
        }
        return clean;
    }

    private static bool Exists(string full) => File.Exists(full) || Directory.Exists(full) || WorkspaceFileSystem.IsLink(full);

    private static bool SameEntry(string a, string b)
    {
        // Same directory, names differing only in case, and the filesystem says both exist: one entry.
        var dir = Path.GetDirectoryName(a)!;
        var names = new DirectoryInfo(dir).EnumerateFileSystemInfos().Select(i => i.Name).ToList();
        return names.Count(n => string.Equals(n, Path.GetFileName(a), StringComparison.OrdinalIgnoreCase)) == 1
            && !names.Contains(Path.GetFileName(b), StringComparer.Ordinal);
    }

    /// <summary>"name.ext", then "name (1).ext", "name (2).ext", … whichever is free in <paramref name="dir"/>.</summary>
    public static string UniqueName(string dir, string name)
    {
        if (!Exists(Path.Combine(dir, name)))
        {
            return name;
        }
        var dot = name.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        for (var i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!Exists(Path.Combine(dir, candidate)))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Renames an entry; a link moves as a link, never its target. Anything that resolves to a directory (a real one,
    /// or a symlink / junction to one) goes through Directory.Move — .NET's File.Move refuses a directory link on Unix —
    /// which renames the link itself; everything else (files, file links, dangling links) through File.Move.
    /// </summary>
    private static void MoveEntry(string src, string dst)
    {
        if (Directory.Exists(src))
        {
            Directory.Move(src, dst);
        }
        else
        {
            File.Move(src, dst);
        }
    }

    private static void DeleteEntry(string full)
    {
        if (WorkspaceFileSystem.IsLink(full))
        {
            if (OperatingSystem.IsWindows() && Directory.Exists(full))
            {
                Directory.Delete(full); // removes the junction, not its target
            }
            else
            {
                File.Delete(full); // unlink(2): the link only
            }
        }
        else if (Directory.Exists(full))
        {
            DeleteTree(full);
        }
        else
        {
            File.Delete(full);
        }
    }

    /// <summary>Recursive delete that unlinks symlinks instead of descending into them.</summary>
    private static void DeleteTree(string dir)
    {
        foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (info.LinkTarget is not null || info is FileInfo)
            {
                DeleteEntry(info.FullName);
            }
            else
            {
                DeleteTree(info.FullName);
            }
        }
        Directory.Delete(dir);
    }

    private static void DeleteQuietly(string full)
    {
        try
        {
            if (Exists(full))
            {
                DeleteEntry(full);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort (a staging leftover is a ".part" the user can delete)
        }
    }

    private static int CopyFile(string src, string dst, CancellationToken ct)
    {
        using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write))
        {
            var buffer = new byte[81920];
            int n;
            while ((n = input.Read(buffer)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, n);
            }
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dst, File.GetUnixFileMode(src));
        }
        return 0;
    }

    /// <summary>Copies a directory tree, leaving symlinks out. Returns how many links were skipped.</summary>
    private static int CopyTree(string src, string dst, CancellationToken ct)
    {
        Directory.CreateDirectory(dst);
        var skipped = 0;
        foreach (var info in new DirectoryInfo(src).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(dst, info.Name);
            if (info.LinkTarget is not null)
            {
                skipped++;
            }
            else if (info is DirectoryInfo)
            {
                skipped += CopyTree(info.FullName, target, ct);
            }
            else if (info is FileInfo)
            {
                CopyFile(info.FullName, target, ct);
            }
        }
        return skipped;
    }

    private static void WriteManifest(string batch, List<(string Path, string Stored)> items)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteStartArray("items");
            foreach (var (path, stored) in items)
            {
                w.WriteStartObject();
                w.WriteString("path", path);
                w.WriteString("stored", stored);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        var tmp = Path.Combine(batch, Manifest + ".tmp");
        File.WriteAllBytes(tmp, buffer.ToArray());
        File.Move(tmp, Path.Combine(batch, Manifest), overwrite: true);
    }

    private static List<(string Path, string Stored)> ReadManifest(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            return doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => (i.GetProperty("path").GetString() ?? "", i.GetProperty("stored").GetString() ?? ""))
                .Where(i => i.Item1.Length > 0 && i.Item2.Length > 0 && i.Item2.All(char.IsAsciiDigit))
                .ToList();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new WorkspaceException(WorkspaceErrors.TrashGone, "unreadable trash manifest: " + e.Message);
        }
    }
}
