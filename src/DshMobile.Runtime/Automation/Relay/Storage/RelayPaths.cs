// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/spi/RelayPathPolicy.kt, storage/RelayPaths.kt
// Registered in docs/UPSTREAMS.md. Locked by tests/TestVectors/relay-storage.json.

namespace DshMobile.Runtime.Automation.Relay.Storage;

/// <summary>
/// Upstream <c>RelayPathPolicy</c>: where the guest sees the shared tree, the host directory name (private
/// directories add <c>-state</c> / <c>-stage</c> / <c>-audit</c>), the CLI name, the artifact prefix, the packaged
/// client asset and the media-library album. The sub-directory words (run/inbox/…) are fixed.
/// </summary>
public sealed record RelayPathPolicy(
    string GuestEntry,
    string HostDirName,
    string CliName,
    string ArtifactPrefix,
    string AssetClient,
    string MediaAlbumDir)
{
    public const string DefaultAssetClient = "relay/relay-client.cjs";

    /// <summary>Upstream <c>BuiltIn</c>.</summary>
    public static RelayPathPolicy BuiltIn { get; } = new("/opt/interlock-relay", "relay", "relay", "relay-", DefaultAssetClient, "Relay");

    /// <summary>The policy this app passes (native-bridge <c>MobileAutomation.Paths</c>).</summary>
    public static RelayPathPolicy DshMobile { get; } = new("/opt/pilot", "pilot", "relay", "relay-", DefaultAssetClient, "DSH Mobile");
}

/// <summary>
/// Upstream <c>RelayPaths</c>: every directory the relay uses. Anything the sandbox must not rewrite (request
/// states, quota, audit, staging) lives <em>outside</em> the bound tree; the mailbox and artifacts live inside it.
/// </summary>
public sealed class RelayPaths
{
    public const string LogsDirName = "logs";

    public RelayPaths(string hostFilesDir, RelayPathPolicy? policy = null)
    {
        Policy = policy ?? RelayPathPolicy.BuiltIn;
        EntryDir = Path.Combine(hostFilesDir, Policy.HostDirName);
        BinDir = Path.Combine(EntryDir, "bin");
        CapabilitiesFile = Path.Combine(EntryDir, "capabilities.json");
        UsageFile = Path.Combine(EntryDir, "USAGE.md");
        ClientScript = Path.Combine(BinDir, Policy.CliName);
        VersionMarkerFile = Path.Combine(EntryDir, "version.json");
        RunDir = Path.Combine(EntryDir, "run");
        InboxDir = Path.Combine(RunDir, "inbox");
        ProcessingDir = Path.Combine(RunDir, "processing");
        OutboxDir = Path.Combine(RunDir, "outbox");
        ArtifactsDir = Path.Combine(RunDir, "artifacts");
        ControlInboxDir = Path.Combine(RunDir, "control-inbox");
        ControlOutboxDir = Path.Combine(RunDir, "control-outbox");
        UploadsDir = Path.Combine(RunDir, "uploads");
        OutDir = Path.Combine(EntryDir, "out");
        OutShotDir = Path.Combine(OutDir, ArtifactOut.Shot);
        OutVideoDir = Path.Combine(OutDir, ArtifactOut.Video);
        OutAudioDir = Path.Combine(OutDir, ArtifactOut.Audio);
        OutFileDir = Path.Combine(OutDir, ArtifactOut.File);
        PrivateDir = Path.Combine(hostFilesDir, Policy.HostDirName + "-state");
        RequestStateDir = Path.Combine(PrivateDir, "requests");
        StagingDir = Path.Combine(hostFilesDir, Policy.HostDirName + "-stage");
        QuotaFile = Path.Combine(PrivateDir, "quota.properties");
        AuditDir = Path.Combine(hostFilesDir, Policy.HostDirName + "-audit");
        LogsDir = Path.Combine(hostFilesDir, LogsDirName);
    }

    public RelayPathPolicy Policy { get; }
    public string EntryDir { get; }
    public string BinDir { get; }
    public string CapabilitiesFile { get; }
    public string UsageFile { get; }
    public string ClientScript { get; }
    public string VersionMarkerFile { get; }
    public string RunDir { get; }
    public string InboxDir { get; }
    public string ProcessingDir { get; }
    public string OutboxDir { get; }
    public string ArtifactsDir { get; }
    public string ControlInboxDir { get; }
    public string ControlOutboxDir { get; }
    public string UploadsDir { get; }
    public string OutDir { get; }
    public string OutShotDir { get; }
    public string OutVideoDir { get; }
    public string OutAudioDir { get; }
    public string OutFileDir { get; }
    public string PrivateDir { get; }
    public string RequestStateDir { get; }
    public string StagingDir { get; }
    public string QuotaFile { get; }
    public string AuditDir { get; }
    public string LogsDir { get; }

    public string AssetClient => Policy.AssetClient;
    public string CliName => Policy.CliName;
    public string GuestEntry => Policy.GuestEntry;
    public string GuestRun => GuestEntry + "/run";
    public string GuestInbox => GuestRun + "/inbox";
    public string GuestOutbox => GuestRun + "/outbox";
    public string GuestArtifacts => GuestRun + "/artifacts";
    public string GuestUploads => GuestRun + "/uploads";
    public string GuestControlInbox => GuestRun + "/control-inbox";
    public string GuestControlOutbox => GuestRun + "/control-outbox";
    public string GuestOut => GuestEntry + "/out";
    public string GuestScript => GuestEntry + "/bin/" + Policy.CliName;
    public string GuestCapabilities => GuestEntry + "/capabilities.json";

    /// <summary>Upstream <c>ensureWritableDirs</c>: a directory that cannot be created must be reported.</summary>
    public bool EnsureWritableDirs() => new[]
    {
        InboxDir, ProcessingDir, OutboxDir, ArtifactsDir, UploadsDir, ControlInboxDir, ControlOutboxDir,
        OutDir, OutShotDir, OutVideoDir, OutAudioDir, OutFileDir, StagingDir, PrivateDir, RequestStateDir, AuditDir, LogsDir,
    }.All(TryMakeDir);

    /// <summary>
    /// Upstream <c>ensureMailboxDirs</c>: the six directories the channel lives on. A plain file the sandbox put in
    /// the way of one of them (same uid) is deleted and the directory rebuilt; that is the only way to recover.
    /// </summary>
    public bool EnsureMailboxDirs() => new[] { InboxDir, ProcessingDir, OutboxDir, ArtifactsDir, ControlInboxDir, ControlOutboxDir }
        .All(d =>
        {
            if (Directory.Exists(d))
            {
                return true;
            }
            if (File.Exists(d))
            {
                try
                {
                    File.Delete(d);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            return TryMakeDir(d);
        });

    /// <summary>
    /// Upstream <c>resolveUpload</c>: one plain name (no separators, no <c>..</c>), no link on the way from the
    /// entry directory, an existing regular file; null otherwise.
    /// </summary>
    public string? ResolveUpload(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!RelayStorageRules.IsUploadName(name) || name.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }
        if (!Directory.Exists(UploadsDir) && !TryMakeDir(UploadsDir))
        {
            return null;
        }
        var file = Path.Combine(UploadsDir, name);
        if (!RelayStorageRules.SymlinkFreeFrom(EntryDir, file))
        {
            return null;
        }
        return File.Exists(file) ? file : null;
    }

    /// <summary>Upstream <c>toGuestPath</c>: a host artifact path under <c>run/artifacts</c> → its guest path.</summary>
    public string? ToGuestPath(string artifact)
    {
        string hostDir, hostFile;
        try
        {
            hostDir = Canonical(ArtifactsDir);
            hostFile = Canonical(artifact);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        if (hostFile != hostDir && !hostFile.StartsWith(hostDir + "/", StringComparison.Ordinal))
        {
            return null;
        }
        return GuestArtifacts + hostFile[hostDir.Length..];
    }

    private static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        var target = info.LinkTarget is not null ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full : full;
        return target.Replace('\\', '/');
    }

    internal static bool TryMakeDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>The pure rules of upstream <c>RelayPaths</c>' companion.</summary>
public static class RelayStorageRules
{
    /// <summary>Upstream <c>UPLOAD_NAME</c>: <c>[A-Za-z0-9][A-Za-z0-9._-]{0,63}</c>.</summary>
    public static bool IsUploadName(string name)
    {
        if (name.Length is 0 or > 64 || !IsAlnum(name[0]))
        {
            return false;
        }
        for (var i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (!IsAlnum(c) && c is not ('.' or '_' or '-'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsAlnum(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    /// <summary>
    /// Upstream <c>symlinkFreeFrom</c>: every level from <paramref name="root"/> (inclusive, but not its parents) to
    /// <paramref name="target"/> must not be a symbolic link, and the target must be inside root. Separators are
    /// normalised to '/'.
    /// </summary>
    public static bool SymlinkFreeFrom(string root, string target) => SymlinkFreeFrom(root, target, IsLink);

    public static bool SymlinkFreeFrom(string root, string target, Func<string, bool> isLink)
    {
        ArgumentNullException.ThrowIfNull(isLink);
        var rootPath = Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/');
        var targetPath = Path.GetFullPath(target).Replace('\\', '/');
        if (targetPath != rootPath && !targetPath.StartsWith(rootPath + "/", StringComparison.Ordinal))
        {
            return false;
        }
        var cursor = rootPath;
        if (isLink(cursor))
        {
            return false;
        }
        var rest = targetPath.Length > rootPath.Length ? targetPath[(rootPath.Length + 1)..] : "";
        var segments = rest.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            cursor = cursor + "/" + segments[i];
            if (isLink(cursor))
            {
                return false;
            }
        }
        return !isLink(targetPath);
    }

    private static bool IsLink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists || Directory.Exists(path) ? (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null : false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Upstream <c>ArtifactOut</c>: delivery sub-directories and their reclaim parameters (mirrored in the CLI).</summary>
public static class ArtifactOut
{
    public const string Shot = "shot";
    public const string Video = "video";
    public const string Audio = "audio";
    public const string File = "file";

    public static IReadOnlyList<string> Subdirs { get; } = [Shot, Video, Audio, File];

    public static string SubdirFor(string capability) => capability switch
    {
        "screen.capture" => Shot,
        "screen.record" => Video,
        "audio.capture" => Audio,
        _ => File,
    };

    public const long MaxTotalBytes = 256L * 1024L * 1024L;
    public const int KeepPerKind = 20;
    public const long MaxAgeMs = 7L * 24L * 60L * 60L * 1000L;
    public const long RecentGraceMs = 10L * 60L * 1000L;
}
