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
