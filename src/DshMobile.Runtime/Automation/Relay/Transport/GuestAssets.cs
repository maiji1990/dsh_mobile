// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/transport/GuestAssets.kt (materialize, intact, publish, markerJson, usageText)
// The entry script and the manual template are byte-exact copies in Assets/ (see Assets/README.md), embedded in this
// assembly. Registered in docs/UPSTREAMS.md.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DshMobile.Runtime.Automation.Relay.Logging;
using DshMobile.Runtime.Automation.Relay.Storage;

namespace DshMobile.Runtime.Automation.Relay.Transport;

/// <summary>
/// Upstream <c>GuestAssets</c>: puts the entry script, the capability list and the manual into the bound tree, then a
/// commit marker with their SHA-256. These files only <em>inform</em>: the guest has the host's uid and can rewrite
/// them, which changes none of the host's decisions, so nothing tries to make them read-only.
/// <para>
/// Each file is published on its own (staged outside the bound tree, then renamed in): each is atomic, the set is not.
/// A set abandoned half way is never taken for published: the marker is written last, and <see cref="Intact"/> compares
/// it with every file, so a mixed set is republished from scratch on the next check.
/// </para>
/// </summary>
public sealed class GuestAssets
{
    /// <summary>Upstream <c>MARKER_LAYOUT</c>: an unknown layout counts as no marker.</summary>
    public const int MarkerLayout = 1;

    private readonly Func<string, byte[]?> _assetLoader;
    private readonly RelayPaths _paths;
    private readonly RunLog _runLog;
    private readonly TimeProvider _time;

    /// <param name="assetLoader">Bytes of a packaged asset by name (<see cref="RelayPathPolicy.AssetClient"/>); null when
    /// it cannot be read. Defaults to the copies embedded in this assembly.</param>
    public GuestAssets(RelayPaths paths, RunLog runLog, Func<string, byte[]?>? assetLoader = null, TimeProvider? time = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _runLog = runLog ?? throw new ArgumentNullException(nameof(runLog));
        _assetLoader = assetLoader ?? RelayGuestAssetFiles.Load;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Upstream <c>materialize</c>: true only when the three files and the marker are all on disk. The caller moves
    /// its "published" record forward only then, so a failed pass keeps the "not complete" status.
    /// </summary>
    public bool Materialize(string capabilitiesJson)
    {
        ArgumentNullException.ThrowIfNull(capabilitiesJson);
        if (!_paths.EnsureWritableDirs())
        {
            _runLog.Error(LogSubsystem.Transport, LogEvent.DirsUnavailable);
            return false;
        }
        TryCreateDirectory(_paths.BinDir);
        // Read whole first: the marker hash must be of the bytes that actually land.
        var scriptBytes = _assetLoader(_paths.Policy.AssetClient);
        if (scriptBytes is null)
        {
            _runLog.Error(LogSubsystem.Transport, LogEvent.RequestDropped, ("file", _paths.Policy.AssetClient), ("cause", "asset copy failed"));
            return false;
        }
        // Executable for convenience only (node can run it either way). Set on the staged copy, before the rename:
        // same mode as upstream's setExecutable(true, false) on the target, without following a link the guest may
        // have put at the target in between.
        if (!Publish(_paths.ClientScript, scriptBytes, executable: true))
        {
            return false;
        }
        var capabilitiesBytes = Encoding.UTF8.GetBytes(capabilitiesJson);
        if (!Publish(_paths.CapabilitiesFile, capabilitiesBytes))
        {
            return false;
        }
        var usageBytes = Encoding.UTF8.GetBytes(UsageText(_paths));
        if (!Publish(_paths.UsageFile, usageBytes))
        {
            return false;
        }
        // The marker last: only once it lands are the three "one batch".
        var marker = MarkerJson(Sha256(scriptBytes), Sha256(capabilitiesBytes), Sha256(usageBytes));
        return Publish(_paths.VersionMarkerFile, Encoding.UTF8.GetBytes(marker));
    }

    /// <summary>
    /// Upstream <c>intact</c>: the three files and the marker exist, no link anywhere from the entry directory down
    /// (the same check as the write side), the marker layout is known and every file hashes to what it recorded.
    /// </summary>
    public bool Intact()
    {
        string[] files = [_paths.ClientScript, _paths.CapabilitiesFile, _paths.UsageFile];
        if (!files.All(f => File.Exists(f) && RelayStorageRules.SymlinkFreeFrom(_paths.EntryDir, f)))
        {
            return false;
        }
        var marker = _paths.VersionMarkerFile;
        if (!File.Exists(marker) || !RelayStorageRules.SymlinkFreeFrom(_paths.EntryDir, marker))
        {
            return false;
        }
        string text;
        try
        {
            text = File.ReadAllText(marker, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (OrgJson.ParseObject(text) is not { } parsed || OrgJson.OptInt(parsed, "layout") != MarkerLayout
            || OrgJson.OptObject(parsed, "files") is not { } recorded)
        {
            return false;
        }
        foreach (var (file, name) in new[] { (_paths.ClientScript, ScriptMarkerKey()), (_paths.CapabilitiesFile, "capabilities.json"), (_paths.UsageFile, "USAGE.md") })
        {
            string hash;
            try
            {
                hash = Sha256(File.ReadAllBytes(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            if (OrgJson.OptString(recorded, name) != hash)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Upstream <c>publish</c>: never writes bytes into the bound tree directly. The guest (same uid) can turn the
    /// target, or any directory on the way, into a link into the host's private files; writing through it would let
    /// the guest rewrite the host's audit, quota or preferences with the host's hand. So: no link from the entry
    /// directory down (checked level by level), bytes staged outside the tree, then renamed in (rename(2) replaces a
    /// final link instead of following it).
    /// </summary>
    private bool Publish(string target, byte[] bytes, bool executable = false)
    {
        var name = Path.GetFileName(target);
        if (!RelayStorageRules.SymlinkFreeFrom(_paths.EntryDir, target))
        {
            _runLog.Error(LogSubsystem.Storage, LogEvent.AtomicWriteFailed, ("file", name));
            return false;
        }
        string staged;
        try
        {
            Directory.CreateDirectory(_paths.StagingDir);
            staged = Path.Combine(_paths.StagingDir, $"{name}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}.tmp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        var ok = false;
        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
            }
            if (executable && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(staged, File.GetUnixFileMode(staged) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            File.Move(staged, target, overwrite: true);
            ok = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            try
            {
                if (File.Exists(staged))
                {
                    File.Delete(staged);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        if (!ok)
        {
            _runLog.Error(LogSubsystem.Storage, LogEvent.AtomicWriteFailed, ("file", name));
        }
        return ok;
    }

    private string ScriptMarkerKey() => "bin/" + _paths.CliName;

    /// <summary>Upstream <c>markerJson</c>: file names relative to the entry directory, then the publish second.</summary>
    private string MarkerJson(string scriptHash, string capabilitiesHash, string usageHash) =>
        "{\"layout\":" + MarkerLayout.ToString(CultureInfo.InvariantCulture) + ",\"files\":{" +
        "\"" + ScriptMarkerKey() + "\":\"" + scriptHash + "\"," +
        "\"capabilities.json\":\"" + capabilitiesHash + "\"," +
        "\"USAGE.md\":\"" + usageHash + "\"}," +
        "\"publishedAtWall\":" + (_time.GetUtcNow().ToUnixTimeMilliseconds() / 1000).ToString(CultureInfo.InvariantCulture) + "}";

    /// <summary>Upstream <c>usageText</c>: the fixed English manual with the guest paths filled in.</summary>
    public static string UsageText(RelayPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return RelayGuestAssetFiles.UsageTemplate()
            .Replace("{{ENTRY}}", paths.GuestScript, StringComparison.Ordinal)
            .Replace("{{CAPS}}", paths.GuestCapabilities, StringComparison.Ordinal)
            .Replace("{{UPLOADS}}", paths.GuestUploads, StringComparison.Ordinal)
            .Replace("{{OUT}}", paths.GuestOut, StringComparison.Ordinal)
            .Replace("{{ENTRY_DIR}}", paths.GuestEntry, StringComparison.Ordinal)
            .Replace("{{GATE_DENIED_CODE}}", RelayError.GateHostDenied.Code, StringComparison.Ordinal);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void TryCreateDirectory(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>The byte-exact upstream assets embedded in this assembly (Assets/README.md).</summary>
public static class RelayGuestAssetFiles
{
    public const string ClientScriptResource = "DshMobile.Relay.relay-client.cjs";
    public const string UsageTemplateResource = "DshMobile.Relay.relay-usage.template.md";

    /// <summary>The packaged asset by its upstream name (<see cref="RelayPathPolicy.DefaultAssetClient"/>); null otherwise.</summary>
    public static byte[]? Load(string assetName) =>
        assetName == RelayPathPolicy.DefaultAssetClient ? Read(ClientScriptResource) : null;

    public static string UsageTemplate() =>
        Encoding.UTF8.GetString(Read(UsageTemplateResource) ?? throw new InvalidOperationException("embedded USAGE template missing"));

    private static byte[]? Read(string resource)
    {
        using var stream = typeof(RelayGuestAssetFiles).Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
