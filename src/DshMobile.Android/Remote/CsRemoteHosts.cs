// Ported from this repo's native/native-bridge/src/main/java/dshmobile/nativebridge/RemoteHosts.kt (prefs, store,
// lastUnknownFingerprint; transition code for ADR-0006). Registered in docs/UPSTREAMS.md.

using System.Text.Json;
using Android.Content;
using DshMobile.Core.Bridge;
using DshMobile.Runtime.Remote;

namespace DshMobile.App.Remote;

/// <summary>
/// The C# owner of the remote host profile list (ADR-0016, MIGRATION row 45): the same SharedPreferences file and key
/// as the legacy <c>RemoteHosts</c>, so the legacy fallback connection sees every profile and pin C# wrote. Process-wide
/// and serialized like the legacy store (which ran under the bridge op lock). The last unconfirmed host key is the one
/// the <b>C#</b> connection saw: <see cref="Trust"/> pins nothing else.
/// </summary>
internal static class CsRemoteHosts
{
    private static readonly Lock Gate = new();
    private static (string HostId, string Fingerprint)? _lastUnknown;

    private static ISharedPreferences Prefs(Context c) =>
        c.ApplicationContext!.GetSharedPreferences(RemoteHostProfile.PreferencesName, FileCreationMode.Private)!;

    /// <summary>The raw stored list (absent = null, read as "[]").</summary>
    public static string? Stored(Context c) => Prefs(c).GetString(RemoteHostProfile.PreferencesKey, null);

    public static IReadOnlyList<RemoteHostProfile> List(Context c) => RemoteHostProfile.ParseStored(Stored(c));

    private static void Store(Context c, IReadOnlyList<RemoteHostProfile> all) =>
        Prefs(c).Edit()!.PutString(RemoteHostProfile.PreferencesKey, RemoteHostStore.Render(all))!.Apply(); // legacy: apply()

    /// <summary>Saves a <c>remote.hosts.save</c>-shaped request; throws <see cref="RemoteProfileException"/> on a rejected one.</summary>
    public static RemoteHostProfile Save(Context c, RemoteHostRequest request)
    {
        var json = BridgeCodec.ToPayload(request, BridgeJsonContext.Default.RemoteHostRequest);
        lock (Gate)
        {
            var (all, saved) = RemoteHostStore.Save(List(c), json, () => Guid.NewGuid().ToString());
            Store(c, all);
            return saved;
        }
    }

    public static IReadOnlyList<RemoteHostProfile> Trust(Context c, string hostId, string fingerprint)
    {
        lock (Gate)
        {
            var all = RemoteHostStore.Trust(List(c), hostId, fingerprint, _lastUnknown);
            Store(c, all);
            _lastUnknown = null;
            return all;
        }
    }

    public static IReadOnlyList<RemoteHostProfile> Remove(Context c, string hostId)
    {
        lock (Gate)
        {
            var all = RemoteHostStore.Remove(List(c), hostId);
            Store(c, all);
            if (_lastUnknown is { } seen && seen.HostId == hostId)
            {
                _lastUnknown = null;
            }
            return all;
        }
    }

    /// <summary>
    /// Records what a C# connection attempt to <paramref name="profile"/> presented. Legacy rule: only an unpinned profile
    /// whose connection saw a key (HOST_KEY_UNKNOWN) makes that key trustable; any other outcome leaves the record alone.
    /// </summary>
    public static void Presented(RemoteHostProfile profile, RemoteConnectAttempt attempt)
    {
        if (profile.PinnedFingerprint is null && attempt.Code == RemoteCodes.HostKeyUnknown && attempt.Fingerprint is { } fp)
        {
            lock (Gate)
            {
                _lastUnknown = (profile.Id, fp);
            }
        }
    }

    public static RemoteHost ToDto(RemoteHostProfile p) => new(p.Id, p.Label, p.Host, p.Port, p.User, p.DshPort, p.PinnedFingerprint);
}
