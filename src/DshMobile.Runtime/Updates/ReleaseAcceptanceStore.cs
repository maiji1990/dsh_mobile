using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DshMobile.Runtime.Updates;

/// <summary>A floor read from (or written to) <see cref="ReleaseAcceptanceStore"/>; <see cref="Ok"/> false = do not trust anything.</summary>
public sealed record ReleaseFloor(bool Ok, string Code, string? Message, DateTimeOffset? GeneratedAt);

/// <summary>
/// Anti-replay state of release discovery (ADR-0022 decision 4): per channel, the <c>generatedAt</c> of the newest
/// manifest this device accepted. A manifest older than that is a replay (<see cref="UpdateErrorCodes.Rollback"/>).
/// <list type="bullet">
/// <item>Stored in <see cref="RuntimeLayout.HostStateDir"/> (never bound into the guest, never in the workspace), as
/// <c>release-acceptance.json</c>; written durably (temp file, fsync, rename).</item>
/// <item>A missing file is "nothing accepted yet". An unreadable or malformed file is an error
/// (<see cref="UpdateErrorCodes.StateUnreadable"/>), never silently treated as empty: that would reopen replays.</item>
/// <item>The floor only moves forward. A failed write is reported (<see cref="UpdateErrorCodes.StateWriteFailed"/>).</item>
/// </list>
/// Not thread-safe by itself; the caller serializes checks.
/// </summary>
public sealed class ReleaseAcceptanceStore(RuntimeLayout layout)
{
    public const string FileName = "release-acceptance.json";

    public string FilePath => Path.Combine(layout.HostStateDir, FileName);

    public ReleaseFloor Read(string channel)
    {
        ValidateChannel(channel);
        var all = ReadAll();
        if (!all.Ok)
        {
            return new ReleaseFloor(false, all.Code, all.Message, null);
        }
        return new ReleaseFloor(true, RuntimeErrorCodes.Ok, null, all.Floors.TryGetValue(channel, out var t) ? t : null);
    }

    /// <summary>Raises the channel's floor to <paramref name="generatedAt"/> (never lowers it). Returns the floor now stored.</summary>
    public ReleaseFloor Accept(string channel, DateTimeOffset generatedAt)
    {
        ValidateChannel(channel);
        var all = ReadAll();
        if (!all.Ok)
        {
            return new ReleaseFloor(false, all.Code, all.Message, null);
        }
        if (all.Floors.TryGetValue(channel, out var current) && current >= generatedAt)
        {
            return new ReleaseFloor(true, RuntimeErrorCodes.Ok, null, current);
        }
        all.Floors[channel] = generatedAt;
        try
        {
            Directory.CreateDirectory(layout.HostStateDir);
            var tmp = FilePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(Serialize(all.Floors));
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, FilePath, overwrite: true);
            return new ReleaseFloor(true, RuntimeErrorCodes.Ok, null, generatedAt);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ReleaseFloor(false, UpdateErrorCodes.StateWriteFailed, $"cannot record the accepted manifest ({FileName}): {e.Message}", null);
        }
    }

    private static void ValidateChannel(string channel)
    {
        if (!ReleaseEndpoints.IsChannel(channel))
        {
            throw new ArgumentException($"unknown release channel '{channel}'", nameof(channel));
        }
    }

    private (bool Ok, string Code, string? Message, Dictionary<string, DateTimeOffset> Floors) ReadAll()
    {
        var floors = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (!File.Exists(FilePath))
        {
            return (true, RuntimeErrorCodes.Ok, null, floors);
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var s) || s.ValueKind != JsonValueKind.Number || s.GetInt32() != 1
                || !root.TryGetProperty("channels", out var channels) || channels.ValueKind != JsonValueKind.Object)
            {
                return Unreadable("not {\"schema\":1,\"channels\":{...}}");
            }
            foreach (var c in channels.EnumerateObject())
            {
                if (c.Value.ValueKind != JsonValueKind.Object || !c.Value.TryGetProperty("generatedAt", out var g) || g.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParse(g.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                {
                    return Unreadable($"channel '{c.Name}' has no valid generatedAt");
                }
                floors[c.Name] = t;
            }
            return (true, RuntimeErrorCodes.Ok, null, floors);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            return Unreadable(e.Message);
        }

        (bool, string, string?, Dictionary<string, DateTimeOffset>) Unreadable(string why) =>
            (false, UpdateErrorCodes.StateUnreadable, $"{FileName} is unreadable ({why}); refusing to trust any manifest until it is repaired", floors);
    }

    private static byte[] Serialize(Dictionary<string, DateTimeOffset> floors)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteStartObject("channels");
            foreach (var (channel, t) in floors.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject(channel);
                w.WriteString("generatedAt", t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        ms.Write(Encoding.UTF8.GetBytes("\n"));
        return ms.ToArray();
    }
}
