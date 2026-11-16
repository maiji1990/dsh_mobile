using System.Globalization;
using System.Text.Json;

namespace DshMobile.Runtime.Updates;

/// <summary>Phases of one online update (vNext PR-05); only <see cref="Committed"/> and <see cref="Failed"/> are terminal.</summary>
public static class UpdatePhases
{
    public const string Downloading = "DOWNLOADING";
    public const string Verified = "VERIFIED";
    public const string Stopping = "STOPPING";
    public const string Swapping = "SWAPPING";
    public const string Probing = "PROBING";
    public const string Committed = "COMMITTED";
    public const string RollingBack = "ROLLING_BACK";
    public const string Failed = "FAILED";

    public static bool IsTerminal(string phase) => phase is Committed or Failed;
}

/// <summary>Stable codes of the update transaction (shown as-is; never mapped to success).</summary>
public static class UpdateTransactionCodes
{
    public const string NothingToInstall = "UPDATE_NOTHING_TO_INSTALL";
    public const string HealthFailed = "UPDATE_HEALTH_FAILED";
    public const string Interrupted = "UPDATE_INTERRUPTED";
    public const string RollbackFailed = "UPDATE_ROLLBACK_FAILED";
    public const string NotSupported = "UPDATE_KIND_NOT_SUPPORTED";
    public const string Busy = "UPDATE_BUSY";
}

/// <summary>The last (or current) update transaction, as persisted.</summary>
public sealed record UpdateState(
    string Phase,
    string Kind,
    string Version,
    string Sha256,
    string Channel,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string? Code,
    string? Message);

/// <summary>
/// Durable record of the update transaction (<c>files/dshmobile-runtime/update-state.json</c>), written before each
/// phase starts. It is what the UI shows after a restart; the <b>disk state</b> of the layer is owned by the layer
/// installer's own journal (a killed health check is undone by the next sync), so this record never drives file
/// operations — <see cref="SettleInterrupted"/> only reports that a transaction did not finish.
/// </summary>
public sealed class UpdateStateStore(RuntimeLayout layout)
{
    public const string FileName = "update-state.json";

    public string FilePath => Path.Combine(layout.HostStateDir, FileName);

    public UpdateState? Read()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            var r = doc.RootElement;
            string S(string n) => r.GetProperty(n).GetString() ?? "";
            string? O(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            DateTimeOffset T(string n) => DateTimeOffset.Parse(S(n), CultureInfo.InvariantCulture);
            return new UpdateState(S("phase"), S("kind"), S("version"), S("sha256"), S("channel"), T("startedAt"), T("updatedAt"), O("code"), O("message"));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            return new UpdateState(UpdatePhases.Failed, "", "", "", "", DateTimeOffset.MinValue, DateTimeOffset.MinValue, UpdateTransactionCodes.Interrupted, $"{FileName} unreadable: {e.Message}");
        }
    }

    /// <summary>Durable write (temp file, fsync, rename). A failure is swallowed: the record is informational.</summary>
    public void Write(UpdateState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        try
        {
            Directory.CreateDirectory(layout.HostStateDir);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("phase", s.Phase);
                w.WriteString("kind", s.Kind);
                w.WriteString("version", s.Version);
                w.WriteString("sha256", s.Sha256);
                w.WriteString("channel", s.Channel);
                w.WriteString("startedAt", s.StartedAt.ToString("O", CultureInfo.InvariantCulture));
                w.WriteString("updatedAt", s.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
                if (s.Code is not null)
                {
                    w.WriteString("code", s.Code);
                }
                if (s.Message is not null)
                {
                    w.WriteString("message", s.Message);
                }
                w.WriteEndObject();
            }
            var tmp = FilePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(ms.ToArray());
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// After a restart: a record left in a non-terminal phase means the process died mid-update. It becomes
    /// <see cref="UpdatePhases.Failed"/> / <see cref="UpdateTransactionCodes.Interrupted"/> (the layer itself was already
    /// restored by the installer's journal). Returns the settled record, or null when there was nothing to settle.
    /// </summary>
    public UpdateState? SettleInterrupted(DateTimeOffset now)
    {
        if (Read() is not { } s || UpdatePhases.IsTerminal(s.Phase))
        {
            return null;
        }
        var settled = s with
        {
            Phase = UpdatePhases.Failed,
            UpdatedAt = now,
            Code = UpdateTransactionCodes.Interrupted,
            Message = $"the app stopped during {s.Phase}; the layer from before the update is kept",
        };
        Write(settled);
        return settled;
    }
}

/// <summary>One installable step, or why a row cannot be updated online.</summary>
public sealed record UpdatePlan(ReleaseArtifact? Dsh, IReadOnlyList<string> Notes);

/// <summary>
/// What can be installed online from a verified discovery (vNext PR-05). Only the DSH layer is installed online in this
/// slice: it has the held / committed / restored transaction. A runtime bundle update stays an offline import (the body
/// swap keeps no rollback copy yet, MIGRATION §7), Node ships inside the runtime bundle, and the app itself updates
/// through Android's package installer (not implemented). Newer DSH versions blocked by their Node requirement are
/// reported, never installed out of order.
/// </summary>
public static class UpdatePlanner
{
    public static UpdatePlan Plan(IReadOnlyList<ReleaseChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var notes = new List<string>();
        ReleaseArtifact? dsh = null;
        foreach (var c in choices)
        {
            switch (c.Kind)
            {
                case ReleaseKinds.Dsh:
                    dsh = c.Update;
                    foreach (var o in c.Offers.Where(o => o.BlockedBy is not null && (c.Installed is null || Versions.IsNewer(o.Artifact.Version, c.Installed))))
                    {
                        notes.Add($"dsh {o.Artifact.Version}: {o.BlockedBy}");
                    }
                    break;
                case ReleaseKinds.Runtime when c.Update is { } r:
                    notes.Add($"runtime {r.Version}: {UpdateTransactionCodes.NotSupported} online; import the bundle offline (Runtime page)");
                    break;
                case ReleaseKinds.Node when c.Update is { } n:
                    notes.Add($"node {n.Version}: ships in the runtime bundle; {UpdateTransactionCodes.NotSupported} on its own");
                    break;
                case ReleaseKinds.App when c.Update is { } a:
                    notes.Add($"app {a.Version}: {UpdateTransactionCodes.NotSupported} (Android package installer not wired yet)");
                    break;
            }
        }
        return new UpdatePlan(dsh, notes);
    }
}
