// Behavior ported from DSHBox v1.5.1 (7c9d33c) sandbox-manager/.../BundleManager.kt extractTarGz / applyEntryMode /
// isWithinRoot and the magic-number sniffing (GPL-3.0-or-later). Parity: tests/TestVectors/layer-extract.json runs
// against both implementations (native-bridge LegacyLayerExtractParityTest, LayerExtractParityTests).

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using ZstdSharp;

namespace DshMobile.Runtime.Layers;

public static class LayerErrorCodes
{
    public const string NotFound = "BUNDLE_NOT_FOUND";
    public const string ProtectedPath = "BUNDLE_PROTECTED_PATH";
    public const string UnsafePath = "BUNDLE_UNSAFE_PATH";
    public const string UnsafeLink = "BUNDLE_UNSAFE_LINK";
    public const string SymlinkFailed = "BUNDLE_SYMLINK_FAILED";
    public const string ExtractFailed = "BUNDLE_EXTRACT_FAILED";
    /// <summary>C#-only: bzip2 / xz containers, which the legacy extractor accepts (documented gap, docs/MIGRATION.md).</summary>
    public const string FormatUnsupported = "BUNDLE_FORMAT_UNSUPPORTED";
}

public enum LayerArchiveFormat
{
    Zstd,
    Gzip,
    Bzip2,
    Xz,
    Tar,
    /// <summary>No known magic: like upstream, treated as gzip (and then fails as such).</summary>
    Unknown,
}

public sealed record LayerExtractResult(
    bool Ok,
    string Code,
    string? Message,
    LayerArchiveFormat Format,
    IReadOnlyList<LayerEntry> Entries,
    IReadOnlyList<string> Warnings,
    long Bytes);

/// <summary>
/// Extracts a runtime layer archive with the legacy semantics, deciding every path on an in-memory model of the tree
/// so the same code yields a manifest without touching disk (<see cref="DryRunExtractionTarget"/>) or extracts for
/// real (<see cref="DiskExtractionTarget"/>). Rules:
/// <list type="bullet">
/// <item>container by magic: zstd, gzip, plain tar; anything else is tried as gzip; bzip2 / xz are not supported yet;</item>
/// <item>leading <c>/</c> stripped; names touching <c>user-data</c> or <c>.dsh</c> are refused (BUNDLE_PROTECTED_PATH);</item>
/// <item>the canonical target (JDK realpath semantics, through already-extracted symlinks) must stay in the root;</item>
/// <item>symlink / hard-link targets must resolve inside the root; symlinks are created with the literal link text;</item>
/// <item>hard links become copies; targets appearing later are resolved at the end, unresolvable ones only warn;</item>
/// <item>every type that is not a directory, symlink or hard link is a regular file (device nodes and FIFOs become
/// empty files: commons-compress <c>isFile()</c> semantics);</item>
/// <item>only the owner permission bits are applied.</item>
/// </list>
/// </summary>
public static class LayerExtractor
{
    private const int SniffBytes = 262;
    private const int MaxSymlinkHops = 40;

    /// <summary>The host filesystem this process runs on (what the legacy extractor on the device sees).</summary>
    public static Func<string, bool> RealHost { get; } = p => Directory.Exists(p) || File.Exists(p);

    /// <summary>
    /// Top-level paths of an Android 10+ device, for reproducing on-device outcomes on a desktop (tests, CI). Absolute
    /// links into these resolve on the device; links into Debian-only paths such as <c>/usr</c> dangle.
    /// </summary>
    public static Func<string, bool> AndroidLikeHost { get; } = p =>
    {
        var top = p.TrimStart('/').Split('/')[0];
        return top is "" or "etc" or "system" or "vendor" or "product" or "odm" or "apex" or "data" or "dev" or "proc"
            or "sys" or "mnt" or "storage" or "sdcard" or "bin" or "config" or "acct" or "cache" or "linkerconfig";
    };

    /// <param name="hostPathExists">Existence probe for paths outside the layer root; default <see cref="RealHost"/>.</param>
    public static async Task<LayerExtractResult> ExtractAsync(string archivePath, IExtractionTarget target, CancellationToken ct, Func<string, bool>? hostPathExists = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        if (!File.Exists(archivePath))
        {
            return Fail(LayerErrorCodes.NotFound, $"bundle not found: {archivePath}", LayerArchiveFormat.Unknown);
        }
        var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        await using (fs.ConfigureAwait(false))
        {
            return await ExtractAsync(fs, target, ct, hostPathExists).ConfigureAwait(false);
        }
    }

    /// <summary>Extracts from any readable stream (e.g. a zip entry, not seekable).</summary>
    public static async Task<LayerExtractResult> ExtractAsync(Stream archive, IExtractionTarget target, CancellationToken ct, Func<string, bool>? hostPathExists = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(target);
        var head = new byte[SniffBytes];
        var headLen = await archive.ReadAtLeastAsync(head, SniffBytes, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        var format = Sniff(head.AsSpan(0, headLen));
        if (format is LayerArchiveFormat.Bzip2 or LayerArchiveFormat.Xz)
        {
            return Fail(LayerErrorCodes.FormatUnsupported, $"{format} layer archives are not supported by the C# extractor yet", format);
        }
        var raw = new PrefixedStream(head.AsMemory(0, headLen), archive);
        var tree = new VirtualTree(target, hostPathExists ?? RealHost);
        try
        {
            Stream decompressed = format switch
            {
                LayerArchiveFormat.Zstd => new DecompressionStream(raw, leaveOpen: true),
                LayerArchiveFormat.Tar => raw,
                _ => new GZipStream(raw, CompressionMode.Decompress, leaveOpen: true),
            };
            await using (decompressed.ConfigureAwait(false))
            {
                var reader = new TarReader(decompressed, leaveOpen: true);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
                    {
                        await tree.ApplyAsync(entry, ct).ConfigureAwait(false);
                    }
                }
            }
            tree.ResolvePendingHardLinks();
            return new LayerExtractResult(true, "OK", null, format, tree.Manifest(), tree.Warnings, tree.Bytes);
        }
        catch (LayerExtractException e)
        {
            return Fail(e.Code, e.Message, format);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or ZstdException or UnauthorizedAccessException or EndOfStreamException)
        {
            return Fail(LayerErrorCodes.ExtractFailed, e.Message, format);
        }
    }

    /// <summary>Upstream order: zstd, bzip2, xz, plain tar ("ustar" at 257), else gzip.</summary>
    public static LayerArchiveFormat Sniff(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 4 && h[0] == 0x28 && h[1] == 0xB5 && h[2] == 0x2F && h[3] == 0xFD)
        {
            return LayerArchiveFormat.Zstd;
        }
        if (h.Length >= 3 && h[0] == 0x42 && h[1] == 0x5A && h[2] == 0x68)
        {
            return LayerArchiveFormat.Bzip2;
        }
        if (h.Length >= 6 && h[0] == 0xFD && h[1] == 0x37 && h[2] == 0x7A && h[3] == 0x58 && h[4] == 0x5A && h[5] == 0x00)
        {
            return LayerArchiveFormat.Xz;
        }
        if (h.Length >= 262 && h[257] == 0x75 && h[258] == 0x73 && h[259] == 0x74 && h[260] == 0x61 && h[261] == 0x72)
        {
            return LayerArchiveFormat.Tar;
        }
        return h.Length >= 2 && h[0] == 0x1F && h[1] == 0x8B ? LayerArchiveFormat.Gzip : LayerArchiveFormat.Unknown;
    }

    private static LayerExtractResult Fail(string code, string message, LayerArchiveFormat format) =>
        new(false, code, message, format, [], [], 0);

    private sealed class LayerExtractException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    /// <summary>The extracted tree as the legacy code would see it on disk, plus the side effects to perform.</summary>
    private sealed class VirtualTree(IExtractionTarget target, Func<string, bool> hostPathExists)
    {
        private sealed class Node
        {
            public LayerEntryKind Kind;
            public string? Mode;
            public long Size;
            public string? Sha256;
            public string? Link;
        }

        // Host path of the (canonical) root. Its value never reaches disk: it only gives ".." above the root a place
        // to go, so "<root>/../<rootname>/x" behaves like the legacy canonical-path check.
        private static readonly string[] RootComps = ["dshmobile-layer-root", "root"];

        private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
        private readonly List<(string[] Path, string[] Target, int Mode)> _pending = [];

        public List<string> Warnings { get; } = [];

        public long Bytes { get; private set; }

        public async Task ApplyAsync(TarEntry entry, CancellationToken ct)
        {
            if (entry.EntryType == TarEntryType.GlobalExtendedAttributes)
            {
                return; // consumed by commons-compress, never surfaced as an entry
            }
            var name = entry.Name;
            var relative = name.TrimStart('/');
            if (relative.Length == 0)
            {
                return;
            }
            var lower = relative.ToLowerInvariant();
            if (lower == "user-data" || lower.StartsWith("user-data/", StringComparison.Ordinal) || lower.Contains("/user-data/", StringComparison.Ordinal)
                || lower.EndsWith("/user-data", StringComparison.Ordinal) || lower == ".dsh" || lower.StartsWith(".dsh/", StringComparison.Ordinal)
                || lower.Contains("/.dsh/", StringComparison.Ordinal))
            {
                throw new LayerExtractException(LayerErrorCodes.ProtectedPath, $"protected path in bundle: {name}");
            }
            var targetPath = Canonicalize(Join(RootComps, relative));
            if (!IsInsideOrRoot(targetPath))
            {
                throw new LayerExtractException(LayerErrorCodes.UnsafePath, $"unsafe tar path: {name}");
            }
            var mode = (int)entry.Mode;
            if (entry.EntryType == TarEntryType.Directory || name.EndsWith('/'))
            {
                MkDirs(targetPath);
                ApplyMode(targetPath, mode);
            }
            else if (entry.EntryType == TarEntryType.SymbolicLink)
            {
                var link = entry.LinkName;
                MkDirs(Parent(targetPath));
                var linkTarget = link.StartsWith('/')
                    ? Canonicalize(Join(RootComps, link.TrimStart('/')))
                    : Canonicalize(Join(Parent(targetPath), link));
                if (!IsInsideOrRoot(linkTarget))
                {
                    throw new LayerExtractException(LayerErrorCodes.UnsafeLink, $"unsafe symlink: {name} -> {link}");
                }
                // Os.symlink: fails when anything (even a dangling link) exists there or the parent is not a directory.
                if (IsRoot(targetPath) || Lookup(targetPath) is not null || !IsDirectory(Parent(targetPath)) || !CanCreateIn(Parent(targetPath)))
                {
                    throw new LayerExtractException(LayerErrorCodes.SymlinkFailed, $"failed to create symlink: {name}");
                }
                try
                {
                    target.CreateSymlink(Rel(targetPath), link);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw new LayerExtractException(LayerErrorCodes.SymlinkFailed, $"failed to create symlink: {name}: {e.Message}");
                }
                _nodes[Rel(targetPath)] = new Node { Kind = LayerEntryKind.Symlink, Link = link };
            }
            else if (entry.EntryType == TarEntryType.HardLink)
            {
                var link = entry.LinkName;
                MkDirs(Parent(targetPath));
                var stripped = link.TrimStart('/');
                if (stripped.StartsWith("./", StringComparison.Ordinal))
                {
                    stripped = stripped[2..];
                }
                var linkTarget = Canonicalize(Join(RootComps, stripped));
                if (!IsInsideOrRoot(linkTarget))
                {
                    throw new LayerExtractException(LayerErrorCodes.UnsafeLink, $"unsafe hard link: {name} -> {link}");
                }
                if (ResolveFile(linkTarget) is { } source)
                {
                    CopyInto(targetPath, source, mode);
                }
                else
                {
                    _pending.Add((targetPath, linkTarget, mode));
                }
            }
            else
            {
                MkDirs(Parent(targetPath));
                var dest = OpenForWrite(targetPath);
                var hashing = new HashingStream(entry.DataStream ?? Stream.Null);
                await target.WriteFileAsync(dest, hashing, ct).ConfigureAwait(false);
                _nodes[dest] = new Node { Kind = LayerEntryKind.File, Size = hashing.Length, Sha256 = hashing.Hash(), Mode = _nodes.GetValueOrDefault(dest)?.Mode };
                Bytes += hashing.Length;
                ApplyMode(Split(dest), mode, isRel: true);
            }
        }

        public void ResolvePendingHardLinks()
        {
            foreach (var (path, linkTarget, mode) in _pending)
            {
                if (ResolveFile(linkTarget) is { } source)
                {
                    CopyInto(path, source, mode);
                }
                else
                {
                    Warnings.Add($"unresolved tar hard link: {Rel(path)} -> {Rel(linkTarget)}");
                }
            }
        }

        public IReadOnlyList<LayerEntry> Manifest() =>
            _nodes.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new LayerEntry(kv.Key, kv.Value.Kind, kv.Value.Mode, kv.Value.Size, kv.Value.Sha256, kv.Value.Link))
                .ToList();

        // ---------------------------------------------------------------- legacy file operations on the model

        private void CopyInto(string[] targetPath, string sourceRel, int mode)
        {
            var dest = OpenForWrite(targetPath);
            var src = _nodes[sourceRel];
            target.CopyFile(sourceRel, dest);
            // FileOutputStream truncates before reading: copying a file onto itself leaves it empty.
            var (size, sha) = dest == sourceRel ? (0L, EmptySha) : (src.Size, src.Sha256);
            _nodes[dest] = new Node { Kind = LayerEntryKind.File, Size = size, Sha256 = sha, Mode = _nodes.GetValueOrDefault(dest)?.Mode };
            ApplyMode(Split(dest), mode, isRel: true);
        }

        /// <summary><c>FileOutputStream(target)</c>: follows a final symlink to an existing file; a directory or a
        /// missing parent fails with an IOException (BUNDLE_EXTRACT_FAILED). Returns the root-relative file path.</summary>
        private string OpenForWrite(string[] path)
        {
            if (IsRoot(path))
            {
                throw new LayerExtractException(LayerErrorCodes.ExtractFailed, "cannot write the root directory");
            }
            switch (Lookup(path))
            {
                case { Kind: LayerEntryKind.Dir }:
                    throw new LayerExtractException(LayerErrorCodes.ExtractFailed, $"{Rel(path)} (Is a directory)");
                case { Kind: LayerEntryKind.File }:
                    return Rel(path);
                case { Kind: LayerEntryKind.Symlink }:
                    return ResolveFile(path) ?? throw new LayerExtractException(LayerErrorCodes.ExtractFailed, $"{Rel(path)} (dangling symlink)");
            }
            if (!IsDirectory(Parent(path)))
            {
                throw new LayerExtractException(LayerErrorCodes.ExtractFailed, $"{Rel(path)} (No such file or directory)");
            }
            if (!CanCreateIn(Parent(path)))
            {
                throw new LayerExtractException(LayerErrorCodes.ExtractFailed, $"{Rel(path)} (Permission denied)");
            }
            return Rel(path);
        }

        /// <summary><c>File.mkdirs()</c> on a canonical path: creates missing directories, stops at anything else.</summary>
        private void MkDirs(string[] path)
        {
            for (var i = RootComps.Length + 1; i <= path.Length; i++)
            {
                var prefix = path[..i];
                var node = Lookup(prefix);
                if (node is null)
                {
                    target.CreateDirectory(Rel(prefix));
                    _nodes[Rel(prefix)] = new Node { Kind = LayerEntryKind.Dir };
                }
                else if (node.Kind != LayerEntryKind.Dir)
                {
                    return;
                }
                if (i < path.Length && !CanCreateIn(prefix))
                {
                    return; // mkdirs fails silently; a later write below it fails
                }
            }
        }

        /// <summary><c>applyEntryMode</c>: setReadable/Writable/Executable follow links and no-op on missing paths.</summary>
        private void ApplyMode(string[] path, int mode, bool isRel = false)
        {
            var full = isRel ? Join(RootComps, string.Join('/', path)) : path;
            if (IsRoot(full))
            {
                _rootMode = LayerManifest.OwnerMode(mode);
                target.SetOwnerMode("", (mode & 0x100) != 0, (mode & 0x80) != 0, (mode & 0x49) != 0);
                return;
            }
            var resolved = RealPath(full);
            if (resolved is null || !IsInsideOrRoot(resolved) || IsRoot(resolved) || Lookup(resolved) is not { } node)
            {
                return;
            }
            node.Mode = LayerManifest.OwnerMode(mode);
            target.SetOwnerMode(Rel(resolved), (mode & 0x100) != 0, (mode & 0x80) != 0, (mode & 0x49) != 0);
        }

        /// <summary><c>File.isFile()</c>: follows symlinks; returns the root-relative path of the regular file.</summary>
        private string? ResolveFile(string[] path)
        {
            var r = RealPath(path);
            return r is not null && IsInside(r) && Lookup(r) is { Kind: LayerEntryKind.File } ? Rel(r) : null;
        }

        // ---------------------------------------------------------------- JDK canonicalize model

        /// <summary>
        /// <c>File.getCanonicalFile()</c> (OpenJDK canonicalize_md.c, also used by Android): realpath of the whole
        /// path, else of the longest prefix realpath can resolve, with the unresolved tail appended and collapsed
        /// lexically. A dangling symlink is therefore NOT followed.
        /// </summary>
        private string[] Canonicalize(string[] comps)
        {
            if (RealPath(comps) is { } whole)
            {
                return whole;
            }
            for (var k = comps.Length - 1; k >= 1; k--)
            {
                if (RealPath(comps[..k]) is { } prefix)
                {
                    return Collapse([.. prefix, .. comps[k..]]);
                }
            }
            return Collapse(comps);
        }

        /// <summary>realpath(3): every component must exist, symlinks are followed (absolute ones from the host root).</summary>
        private string[]? RealPath(string[] comps)
        {
            var stack = new List<string>();
            var queue = new LinkedList<string>(comps);
            var hops = 0;
            while (queue.First is { } first)
            {
                var c = first.Value;
                queue.RemoveFirst();
                if (c.Length == 0 || c == ".")
                {
                    continue;
                }
                if (c == "..")
                {
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                    continue;
                }
                stack.Add(c);
                var kind = HostKind(stack);
                if (kind is null)
                {
                    return null; // ENOENT
                }
                if (kind == LayerEntryKind.Symlink)
                {
                    if (++hops > MaxSymlinkHops)
                    {
                        return null; // ELOOP
                    }
                    var link = _nodes[RelOf(stack)].Link!;
                    stack.RemoveAt(stack.Count - 1);
                    if (link.StartsWith('/'))
                    {
                        stack.Clear(); // literal absolute link: the HOST root, outside the layer
                    }
                    foreach (var part in link.Split('/').Reverse())
                    {
                        queue.AddFirst(part);
                    }
                }
                else if (kind == LayerEntryKind.File && queue.Any(q => q.Length > 0 && q != "."))
                {
                    return null; // ENOTDIR
                }
            }
            return [.. stack];
        }

        /// <summary>
        /// Kind of a host path: inside the root from the model; the root's ancestors are directories; anything else
        /// outside the root is asked from the host probe. This matters because legacy creates absolute links
        /// literally: <c>x -> /etc</c> resolves on Android (so writing under <c>x</c> is unsafe) while
        /// <c>x.pem -> /usr/share/ca-certificates/...</c> dangles on Android and is therefore NOT followed by the
        /// canonical-path check (Debian's <c>/etc/ssl/certs</c> relies on this).
        /// </summary>
        private LayerEntryKind? HostKind(List<string> stack)
        {
            if (!StartsWithRoot(stack))
            {
                var ancestor = stack.Count < RootComps.Length && stack.Select((c, i) => c == RootComps[i]).All(b => b);
                return ancestor || hostPathExists("/" + string.Join('/', stack)) ? LayerEntryKind.Dir : null;
            }
            return stack.Count == RootComps.Length ? LayerEntryKind.Dir : _nodes.GetValueOrDefault(RelOf(stack))?.Kind;
        }

        private bool IsDirectory(string[] path) => IsRoot(path) || Lookup(path) is { Kind: LayerEntryKind.Dir };

        /// <summary>
        /// Linux: creating an entry needs owner write + execute on the directory (the app is not root). Legacy applies
        /// a directory's archived mode immediately, so a <c>0555</c> directory blocks its later children on Android.
        /// </summary>
        private bool CanCreateIn(string[] dir)
        {
            var mode = IsRoot(dir) ? _rootMode : Lookup(dir)?.Mode;
            return mode is null || (mode[1] == 'w' && mode[2] == 'x');
        }

        private string? _rootMode;

        private static string[] Collapse(string[] comps)
        {
            var outList = new List<string>();
            foreach (var c in comps)
            {
                if (c.Length == 0 || c == ".")
                {
                    continue;
                }
                if (c == ".." && outList.Count > 0 && outList[^1] != "..")
                {
                    outList.RemoveAt(outList.Count - 1);
                    continue;
                }
                outList.Add(c);
            }
            return [.. outList];
        }

        // ---------------------------------------------------------------- path helpers

        private Node? Lookup(string[] path) => IsInside(path) ? _nodes.GetValueOrDefault(Rel(path)) : null;

        private static string[] Join(string[] baseComps, string rel) =>
            [.. baseComps, .. rel.Split('/', StringSplitOptions.RemoveEmptyEntries)];

        private static string[] Parent(string[] path) => path.Length > 0 ? path[..^1] : path;

        private static string[] Split(string rel) => rel.Split('/', StringSplitOptions.RemoveEmptyEntries);

        private static bool StartsWithRoot(IReadOnlyList<string> p)
        {
            if (p.Count < RootComps.Length)
            {
                return false;
            }
            for (var i = 0; i < RootComps.Length; i++)
            {
                if (p[i] != RootComps[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsRoot(string[] p) => p.Length == RootComps.Length && StartsWithRoot(p);

        private static bool IsInside(string[] p) => p.Length > RootComps.Length && StartsWithRoot(p);

        private static bool IsInsideOrRoot(string[] p) => p.Length >= RootComps.Length && StartsWithRoot(p);

        private static string Rel(string[] p) => string.Join('/', p.Skip(RootComps.Length));

        private static string RelOf(List<string> p) => string.Join('/', p.Skip(RootComps.Length));

        private static readonly string EmptySha = Convert.ToHexStringLower(SHA256.HashData(ReadOnlySpan<byte>.Empty));
    }

    /// <summary>Read-through stream that hashes and counts what is read.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;

        public override long Length => _length;

        public string Hash() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            _hash.AppendData(buffer[..n]);
            _length += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _hash.AppendData(buffer.Span[..n]);
            _length += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Replays the sniffed header bytes, then continues with the underlying (non-seekable) stream.</summary>
    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (!_prefix.IsEmpty)
            {
                var n = Math.Min(buffer.Length, _prefix.Length);
                _prefix.Span[..n].CopyTo(buffer);
                _prefix = _prefix[n..];
                return n;
            }
            return inner.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_prefix.IsEmpty)
            {
                return new ValueTask<int>(Read(buffer.Span));
            }
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
