using DshMobile.Runtime.Remote;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-09 first segment: legacy facts of RemoteHosts.kt / SshKeys.kt that the shared vectors cannot reach (they need
/// SharedPreferences, Keystore or the private trust state).
/// </summary>
public partial class LegacySourceContractTests
{
    [Fact]
    public void RemoteHosts_StorageAndTrustRule_MatchTheCSharpPort()
    {
        var kt = Bridge("RemoteHosts.kt");
        Assert.Contains($"getSharedPreferences(\"{RemoteHostProfile.PreferencesName}\", Context.MODE_PRIVATE)", kt, StringComparison.Ordinal);
        Assert.Contains($"private const val KEY_HOSTS = \"{RemoteHostProfile.PreferencesKey}\"", kt, StringComparison.Ordinal);
        // list(): one unreadable entry empties the whole list (ParseStored; LegacyRemoteSshParityTest repeats this body).
        Assert.Contains("val arr = JSONArray(prefs.getString(KEY_HOSTS, \"[]\"))", kt, StringComparison.Ordinal);
        Assert.Contains("(0 until arr.length()).map { RemoteHostProfile.fromJson(arr.getJSONObject(it)) }", kt, StringComparison.Ordinal);
        Assert.Contains("}.getOrDefault(emptyList())", kt, StringComparison.Ordinal);
        // trust(): only the key last presented for this host on an unconfirmed attempt (HostKeyPin.TrustAllowed).
        Assert.Contains("if (!RemoteHostProfile.isFingerprint(fingerprint) || seen == null || seen.first != id || seen.second != fingerprint) {", kt, StringComparison.Ordinal);
        Assert.Contains($"\"{HostKeyPin.TrustRejected}\"", kt, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(kt, @"lastUnknownFingerprint = p\.id to fp"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(kt, @"lastUnknownFingerprint = null"));
        Assert.Contains("throw BridgeException(RuntimeController.BAD_REQUEST, \"unknown host id\")", kt, StringComparison.Ordinal);
        // The patterns the vectors exercise.
        Assert.Contains("""Regex("^(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,62})(?:\\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,62}))*|[0-9a-fA-F:.]{2,45})$")""", kt, StringComparison.Ordinal);
        Assert.Contains("""Regex("^[A-Za-z0-9_][A-Za-z0-9._-]{0,63}$")""", kt, StringComparison.Ordinal);
        Assert.Contains("""Regex("^SHA256:[A-Za-z0-9+/]{43}$")""", kt, StringComparison.Ordinal);
        Assert.Contains("""line.replace(Regex("token=[^\\s&#]+"), "token=***")""", kt, StringComparison.Ordinal);
    }

    /// <summary>
    /// PR-09 second segment: the legacy connect / probe expressions that RemoteConnectRules ports and that the Kotlin
    /// LegacyRemoteSshParityTest repeats (they need a JSch session to run in place).
    /// </summary>
    [Fact]
    public void RemoteHosts_ConnectAndProbeRules_MatchTheCSharpPort()
    {
        var kt = Bridge("RemoteHosts.kt");
        string[] expected =
        [
            "s.setConfig(\"StrictHostKeyChecking\", \"yes\")",
            "s.setConfig(\"PreferredAuthentications\", \"publickey\")",
            "s.serverAliveInterval = 15_000",
            "private const val CONNECT_TIMEOUT_MS = 15_000",
            // connect(): host-key facts before the failure kind, in this order.
            "p.pinnedFingerprint == null && fp != null -> {",
            $"return fail(\"HOST_KEY_UNKNOWN\", \"{RemoteConnectRules.UnknownMessage}\").put(\"fingerprint\", fp)",
            ".put(\"keyType\", keys.seenType ?: JSONObject.NULL)",
            "p.pinnedFingerprint != null && fp != null && fp != p.pinnedFingerprint ->",
            $"return fail(\"HOST_KEY_CHANGED\", \"{RemoteConnectRules.ChangedMessage}\").put(\"fingerprint\", fp)",
            "(e.message ?: \"\").contains(\"Auth fail\", ignoreCase = true) ->",
            $"return fail(\"AUTH_FAILED\", \"{RemoteConnectRules.AuthMessage}\")",
            "(e.message ?: \"\").contains(\"timeout\", ignoreCase = true) -> return fail(\"TIMEOUT\", e.message)",
            "else -> return fail(\"UNREACHABLE\", e.message)",
            $"if (probe?.optBoolean(\"dsh\") != true) set(\"CONNECTED\", \"DSH_MISSING\", \"{RemoteConnectRules.DshMissingMessage}\")",
            // runProbe / exec.
            $"val node = exec(s, \"{RemoteConnectRules.NodeProbe.Command}\", {RemoteConnectRules.NodeProbe.Timeout.TotalSeconds})",
            $"val dsh = exec(s, \"{RemoteConnectRules.DshProbe.Command}\", {RemoteConnectRules.DshProbe.Timeout.TotalSeconds})",
            ".put(\"node\", node.first == 0)",
            ".put(\"nodeVersion\", node.second.lineSequence().firstOrNull { it.isNotBlank() }?.trim()?.take(80) ?: JSONObject.NULL)",
            ".put(\"dshVersion\", dsh.second.lineSequence().firstOrNull { it.isNotBlank() }?.trim()?.take(80) ?: JSONObject.NULL)",
            "val exit = if (ch.isClosed) ch.exitStatus else null",
            $"return exit to (buf.toString(Charsets.UTF_8.name()) + err.toString(Charsets.UTF_8.name())).take({RemoteConnectRules.OutputCap})",
        ];
        foreach (var line in expected)
        {
            Assert.True(kt.Contains(line, StringComparison.Ordinal), line);
        }
        // The legacy connect never overrides JSch's server_host_key: its default is what hostKeyAlgorithms pins.
        Assert.DoesNotContain("server_host_key", kt, StringComparison.Ordinal);
    }

    [Fact]
    public void SshKeys_AliasAndComment_MatchTheCSharpPort()
    {
        var kt = Bridge("SshKeys.kt");
        Assert.Contains($"private const val ALIAS = \"{SshIdentity.KeystoreAlias}\"", kt, StringComparison.Ordinal);
        Assert.Contains($"private const val COMMENT = \"{SshIdentity.Comment}\"", kt, StringComparison.Ordinal);
        Assert.Contains($"const val ALG = \"{SshWire.Algorithm}\"", kt, StringComparison.Ordinal);
        Assert.Contains("Signature.getInstance(\"SHA256withECDSA\")", kt, StringComparison.Ordinal);
    }
}
