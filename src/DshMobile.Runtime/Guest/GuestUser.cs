// Ported from this repo's native/native-bridge/src/main/java/dshmobile/nativebridge/GuestUser.kt, itself ported from
// DSHBox app/src/main/java/.../runtime/GuestUserProvisioner.kt (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4).
// Same idempotent logic and user name `dshmobile` (AGENTS.md rule 4). Registered in docs/UPSTREAMS.md.

using DshMobile.Native;

namespace DshMobile.Runtime.Guest;

/// <summary>
/// Adds a guest <c>/etc/passwd</c> entry for the app's real Linux uid, so shells show a user name instead of
/// "I have no name!" (PRoot runs the guest as the app uid, which the Debian base layer does not know). The legacy
/// terminal does this before every new session; the C# terminal does the same.
/// </summary>
public static class GuestUser
{
    public const string UserName = "dshmobile";
    private const string HomeDir = "/root";
    private const string LoginShell = "/bin/bash";

    public enum Outcome
    {
        Updated,
        AlreadyPresent,
        Skipped,
    }

    /// <param name="uid">Defaults to <see cref="Posix.GetUid"/> (tests pass one).</param>
    public static Outcome Provision(string baseRoot, int? uid = null, int? gid = null)
    {
        ArgumentNullException.ThrowIfNull(baseRoot);
        if (!Directory.Exists(baseRoot))
        {
            return Outcome.Skipped;
        }
        var file = Path.Combine(baseRoot, "etc", "passwd");
        string current;
        try
        {
            current = File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Outcome.Skipped;
        }
        if (!Lines(current).Any(l => l.StartsWith("root:", StringComparison.Ordinal)))
        {
            return Outcome.Skipped;
        }
        var u = uid ?? Posix.GetUid();
        var g = gid ?? Posix.GetGid();
        if (u < 0 || g < 0)
        {
            return Outcome.Skipped;
        }
        var updated = WithEntry(current, u, g);
        if (updated == current)
        {
            return Outcome.AlreadyPresent;
        }
        try
        {
            var tmp = Path.Combine(Path.GetDirectoryName(file)!, "passwd.dshmobile.tmp");
            File.WriteAllText(tmp, updated);
            File.Move(tmp, file, overwrite: true);
            return Outcome.Updated;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Outcome.Skipped;
        }
    }

    /// <summary>Pure: drops any previous entry of ours and blank lines, appends the current one.</summary>
    public static string WithEntry(string content, int uid, int gid)
    {
        ArgumentNullException.ThrowIfNull(content);
        var kept = Lines(content)
            .Where(l => !string.IsNullOrWhiteSpace(l) && l.Split(':')[0].Trim() != UserName)
            .ToList();
        kept.Add($"{UserName}:x:{uid}:{gid}::{HomeDir}:{LoginShell}");
        return string.Join('\n', kept) + "\n";
    }

    /// <summary>Kotlin <c>lineSequence()</c>: splits on CRLF, LF and CR only.</summary>
    private static string[] Lines(string s) => s.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
}
