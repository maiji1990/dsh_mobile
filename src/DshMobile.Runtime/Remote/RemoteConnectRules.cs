// Ported from this repo's native/native-bridge/src/main/java/dshmobile/nativebridge/RemoteHosts.kt (RemoteHosts.connect
// failure classification, runProbe / exec result rules; transition code for ADR-0006 — DSHBox has no remote-host
// feature). Registered in docs/UPSTREAMS.md. Behaviour locked by tests/TestVectors/remote-ssh.json (connectOutcome,
// probe, hostKeyAlgorithms) and LegacySourceContractTests.Remote.

using DshMobile.Core.Contracts;

namespace DshMobile.Runtime.Remote;

/// <summary>Why an SSH connect attempt failed, independent of the library that reported it (ADR-0012 decision 5).</summary>
public enum SshFailureKind
{
    /// <summary>The server did not accept the public key (JSch "Auth fail", SSH.NET <c>SshAuthenticationException</c>).</summary>
    AuthRejected,
    /// <summary>Connect / key exchange did not finish in time.</summary>
    Timeout,
    /// <summary>Anything else: refused, reset, name not resolved, key exchange aborted (also a refused host key).</summary>
    Other,
}

/// <summary>One stable remote outcome: an ADR-0006 <see cref="RemoteCodes"/> value and the message shown with it.</summary>
public sealed record RemoteOutcome(string Code, string? Message);

/// <summary>What one probe command established (legacy <c>runProbe</c> field pair).</summary>
public sealed record ProbeLine(bool Present, string? Version);

/// <summary>
/// The library-independent rules of the legacy connect: outcome classification, capability probe commands and result
/// summarising, and the host-key algorithm preference list (which decides which of a server's keys is presented).
/// </summary>
public static class RemoteConnectRules
{
    public const string UnknownMessage = "confirm the host key fingerprint before connecting";
    public const string ChangedMessage = "host key differs from the pinned one; connection refused";
    public const string AuthMessage = "public key not accepted; add this phone's key to authorized_keys";
    public const string DshMissingMessage = "dsh not found on the remote PATH";

    /// <summary>C# only: the Keystore alias does not exist (the legacy connect creates it; C# never does).</summary>
    public const string IdentityMissing = "IDENTITY_MISSING";
    public const string IdentityMissingMessage = "the phone's SSH key does not exist yet; open the remote page once with the legacy connect";

    /// <summary>Legacy <c>CONNECT_TIMEOUT_MS</c>: TCP connect + key exchange + auth, and each exec channel open.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Legacy <c>serverAliveInterval</c>.</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    /// <summary>Legacy <c>exec</c> output cap (characters of stdout + stderr kept).</summary>
    public const int OutputCap = 8192;

    /// <summary>Legacy probe: fixed commands, each its own exec channel (works under cmd.exe and POSIX shells).</summary>
    public static readonly (string Command, TimeSpan Timeout) NodeProbe = ("node --version", TimeSpan.FromSeconds(20));
    public static readonly (string Command, TimeSpan Timeout) DshProbe = ("dsh --version", TimeSpan.FromSeconds(60));

    /// <summary>
    /// JSch 2.28.8 default <c>server_host_key</c>, in order. The client order decides which host key a server with several
    /// keys presents, so C# must offer exactly this list or a pinned host would look CHANGED (ADR-0012 decision 4).
    /// </summary>
    public static readonly IReadOnlyList<string> HostKeyAlgorithms =
    [
        "ssh-ed25519-cert-v01@openssh.com", "ecdsa-sha2-nistp256-cert-v01@openssh.com", "ecdsa-sha2-nistp384-cert-v01@openssh.com",
        "ecdsa-sha2-nistp521-cert-v01@openssh.com", "rsa-sha2-512-cert-v01@openssh.com", "rsa-sha2-256-cert-v01@openssh.com",
        "ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "rsa-sha2-512", "rsa-sha2-256",
    ];

    /// <summary>
    /// The legacy <c>catch (e: JSchException)</c> of <c>connect</c>: the host-key facts win over the failure kind.
    /// <paramref name="seenFingerprint"/> is the key the server presented during this attempt (null if none);
    /// <paramref name="libraryMessage"/> is the library's own text, kept for TIMEOUT / UNREACHABLE.
    /// </summary>
    public static RemoteOutcome Classify(string? pinned, string? seenFingerprint, SshFailureKind kind, string? libraryMessage)
    {
        if (pinned is null && seenFingerprint is not null)
        {
            return new(RemoteCodes.HostKeyUnknown, UnknownMessage);
        }
        if (pinned is not null && seenFingerprint is not null && seenFingerprint != pinned)
        {
            return new(RemoteCodes.HostKeyChanged, ChangedMessage);
        }
        return kind switch
        {
            SshFailureKind.AuthRejected => new(RemoteCodes.AuthFailed, AuthMessage),
            SshFailureKind.Timeout => new(RemoteCodes.Timeout, libraryMessage),
            _ => new(RemoteCodes.Unreachable, libraryMessage),
        };
    }

    /// <summary>Legacy JSch failure kind: <c>message.contains("Auth fail" / "timeout", ignoreCase = true)</c>, in that order.</summary>
    public static SshFailureKind KindFromJschMessage(string? message)
    {
        var m = message ?? "";
        if (m.Contains("Auth fail", StringComparison.OrdinalIgnoreCase))
        {
            return SshFailureKind.AuthRejected;
        }
        return m.Contains("timeout", StringComparison.OrdinalIgnoreCase) ? SshFailureKind.Timeout : SshFailureKind.Other;
    }

    /// <summary>Legacy <c>exec</c> output: stdout then stderr, each decoded on its own, at most <see cref="OutputCap"/> chars.</summary>
    public static string CombineOutput(string? stdout, string? stderr)
    {
        var all = (stdout ?? "") + (stderr ?? "");
        return all.Length > OutputCap ? all[..OutputCap] : all;
    }

    /// <summary>
    /// Legacy <c>runProbe</c> for one command: present when the exit status is 0; version is the first non-blank line of
    /// the combined output (Kotlin <c>lineSequence</c> splits on CRLF, LF and CR), Kotlin-trimmed, first 80 UTF-16 units.
    /// The version is kept even when the command failed (it is then the shell's error line, as in legacy).
    /// </summary>
    public static ProbeLine Summarize(int? exitStatus, string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        string? version = null;
        foreach (var line in Lines(output))
        {
            if (!KotlinText.IsBlank(line))
            {
                var t = KotlinText.Trim(line);
                version = t.Length > 80 ? t[..80] : t;
                break;
            }
        }
        return new ProbeLine(exitStatus == 0, version);
    }

    /// <summary>Legacy probe JSON → DTO (<c>dsh</c> missing makes the connect CONNECTED + DSH_MISSING).</summary>
    public static RemoteProbe ToProbe(ProbeLine node, ProbeLine dsh)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(dsh);
        return new RemoteProbe(node.Present, node.Version, dsh.Present, dsh.Version);
    }

    /// <summary>The status code after a successful connect + probe.</summary>
    public static RemoteOutcome AfterProbe(RemoteProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return probe.Dsh ? new(RemoteCodes.Ok, null) : new(RemoteCodes.DshMissing, DshMissingMessage);
    }

    /// <summary>Kotlin <c>CharSequence.lineSequence()</c>: splits on "\r\n", "\n" and "\r"; a trailing empty line is kept.</summary>
    internal static IEnumerable<string> Lines(string s)
    {
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\n' || s[i] == '\r')
            {
                yield return s[start..i];
                if (s[i] == '\r' && i + 1 < s.Length && s[i + 1] == '\n')
                {
                    i++;
                }
                start = i + 1;
            }
        }
        yield return s[start..];
    }
}
