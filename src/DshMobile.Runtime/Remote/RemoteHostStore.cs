// Ported from this repo's native/native-bridge/src/main/java/dshmobile/nativebridge/RemoteHosts.kt (RemoteHosts.save /
// remove / trust / store, RemoteHostProfile.toJson; transition code for ADR-0006 — DSHBox has no remote-host feature).
// Registered in docs/UPSTREAMS.md. The read side (ParseStored, FromRequest, TrustAllowed) is the vector-locked port the
// PR-09 shadow verified against the legacy bridge.

using System.Text;
using System.Text.Json;

namespace DshMobile.Runtime.Remote;

/// <summary>
/// The remote host profile list as the legacy <c>RemoteHosts</c> keeps it (ADR-0016: C# writes it by default), as pure
/// list → list functions. The caller owns storage (SharedPreferences <see cref="RemoteHostProfile.PreferencesName"/> /
/// <see cref="RemoteHostProfile.PreferencesKey"/>) and serializes calls. Rejections are <see cref="RemoteProfileException"/>
/// with the legacy <c>BAD_REQUEST</c> message. Host-key protection is unchanged: saving never adds a pin (and drops it when
/// host or port change), and a pin is only accepted for the key last presented to this app (<see cref="HostKeyPin.TrustAllowed"/>).
/// </summary>
public static class RemoteHostStore
{
    public const string UnknownHostId = "unknown host id";

    /// <summary>Legacy <c>save</c>: an id that is not in the list is refused; otherwise the profile replaces its old entry, appended last.</summary>
    public static (IReadOnlyList<RemoteHostProfile> All, RemoteHostProfile Saved) Save(
        IReadOnlyList<RemoteHostProfile> all, JsonElement request, Func<string> newId)
    {
        ArgumentNullException.ThrowIfNull(all);
        var id = RemoteHostProfile.RequestedId(request);
        var existing = id is null ? null : all.FirstOrDefault(p => p.Id == id);
        if (id is not null && existing is null)
        {
            throw new RemoteProfileException(UnknownHostId);
        }
        var saved = RemoteHostProfile.FromRequest(request, existing, newId);
        return ([.. all.Where(p => p.Id != saved.Id), saved], saved);
    }

    /// <summary>Legacy <c>trust</c>: only the key this app last saw for that host on an unconfirmed attempt.</summary>
    public static IReadOnlyList<RemoteHostProfile> Trust(
        IReadOnlyList<RemoteHostProfile> all, string hostId, string fingerprint, (string HostId, string Fingerprint)? lastUnknown)
    {
        ArgumentNullException.ThrowIfNull(all);
        if (!HostKeyPin.TrustAllowed(hostId, fingerprint, lastUnknown))
        {
            throw new RemoteProfileException(HostKeyPin.TrustRejected);
        }
        return [.. all.Select(p => p.Id == hostId ? p with { PinnedFingerprint = fingerprint } : p)];
    }

    /// <summary>Legacy <c>remove</c> (an unknown id leaves the list as it is).</summary>
    public static IReadOnlyList<RemoteHostProfile> Remove(IReadOnlyList<RemoteHostProfile> all, string hostId)
    {
        ArgumentNullException.ThrowIfNull(all);
        return [.. all.Where(p => p.Id != hostId)];
    }

    /// <summary>
    /// Legacy <c>JSONArray(all.map { it.toJson() }).toString()</c>: same keys in the same order, a missing pin as JSON
    /// null. Escaping differs from org.json (which writes <c>\/</c>); both read back the same values.
    /// </summary>
    public static string Render(IEnumerable<RemoteHostProfile> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var p in all)
            {
                w.WriteStartObject();
                w.WriteString("id", p.Id);
                w.WriteString("label", p.Label);
                w.WriteString("host", p.Host);
                w.WriteNumber("port", p.Port);
                w.WriteString("user", p.User);
                w.WriteNumber("dshPort", p.DshPort);
                if (p.PinnedFingerprint is { } pin)
                {
                    w.WriteString("pinnedFingerprint", pin);
                }
                else
                {
                    w.WriteNull("pinnedFingerprint");
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
