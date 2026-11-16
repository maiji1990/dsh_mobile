namespace DshMobile.Core.Files;

/// <summary>
/// What a provider — or one entry of it — really allows (vNext PR-09). The UI offers exactly these actions; a provider
/// never advertises an operation its backing storage cannot do (a read-only SAF tree has no Write / Delete, a protected
/// <c>.dsh</c> entry has nothing but List).
/// </summary>
[Flags]
public enum FileCapabilities
{
    None = 0,
    List = 1,
    Read = 2,
    Write = 4,
    Create = 8,
    Delete = 16,
    Rename = 32,
    Copy = 64,
    Move = 128,
    /// <summary>Deletes go to a trash and can be restored (<see cref="IFileProvider.RestoreAsync"/>).</summary>
    Trash = 256,
    /// <summary>Zip / tar extraction and zip creation (workspace only: it needs a local staging directory).</summary>
    Archive = 512,
    Search = 1024,
}

/// <summary>
/// What a file was when the editor read it (vNext PR-09). A save passes it back; if the file changed meanwhile (DSH, the
/// terminal or another app wrote it) the save is refused with <see cref="WorkspaceErrors.Conflict"/> instead of silently
/// overwriting the other change.
/// </summary>
public sealed record FileVersion(long Length, DateTimeOffset Modified, string Sha256);

/// <summary>One item of a batch that did not happen, with its stable code.</summary>
public sealed record FileFailure(string Path, string Code, string Message);

/// <summary>Copy / move outcome: the new paths, what failed (the rest still happened), symlinks left out.</summary>
public sealed record TransferResult(IReadOnlyList<string> Created, IReadOnlyList<FileFailure> Failed, int SkippedLinks);

/// <summary>
/// A delete: what went (to the trash when <see cref="Restorable"/>), what failed. <see cref="Id"/> identifies the trash
/// batch for <see cref="IFileProvider.RestoreAsync"/>.
/// </summary>
public sealed record DeleteReceipt(string Id, IReadOnlyList<string> Deleted, IReadOnlyList<FileFailure> Failed, bool Restorable);

/// <summary>
/// One storage the Files screen can browse (vNext PR-09): the app's workspace now; a SAF tree and SFTP later. Paths are
/// provider-relative, '/'-separated, "" = the root. Failures are <see cref="WorkspaceException"/> with a stable code;
/// an operation outside <see cref="Capabilities"/> is <see cref="WorkspaceErrors.NotSupported"/>, never a fake success.
/// Batch operations report per-item failures instead of stopping at the first one.
/// </summary>
public interface IFileProvider
{
    string Id { get; }

    /// <summary>The root as the user knows it (the workspace is <c>/root/projects</c> inside DSH).</summary>
    string DisplayRoot { get; }

    FileCapabilities Capabilities { get; }

    FileCapabilities CapabilitiesOf(WorkspaceEntry entry);

    Task<IReadOnlyList<WorkspaceEntry>> ListAsync(string directory, CancellationToken ct);

    Task<WorkspaceEntry> StatAsync(string path, CancellationToken ct);

    Task<Stream> OpenReadAsync(string path, long? maxBytes, CancellationToken ct);

    Task<FileVersion> VersionAsync(string path, CancellationToken ct);

    /// <summary>
    /// Replaces a file atomically. <paramref name="expected"/> null: the file must not exist yet; otherwise it must still
    /// be exactly that version (<see cref="WorkspaceErrors.Conflict"/>). Returns the new version.
    /// </summary>
    Task<FileVersion> WriteAsync(string path, Stream content, FileVersion? expected, CancellationToken ct);

    Task<string> CreateDirectoryAsync(string parent, string name, CancellationToken ct);

    Task<string> CreateFileAsync(string parent, string name, CancellationToken ct);

    Task<string> RenameAsync(string path, string newName, CancellationToken ct);

    /// <summary>Copies into <paramref name="targetDirectory"/>; a taken name becomes "name (1)".</summary>
    Task<TransferResult> CopyAsync(IReadOnlyList<string> paths, string targetDirectory, CancellationToken ct);

    Task<TransferResult> MoveAsync(IReadOnlyList<string> paths, string targetDirectory, CancellationToken ct);

    Task<DeleteReceipt> DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct);

    /// <summary>Puts a trashed batch back (a taken name gets a suffix). Returns the restored paths.</summary>
    Task<IReadOnlyList<string>> RestoreAsync(DeleteReceipt receipt, CancellationToken ct);
}
