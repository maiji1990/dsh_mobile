using DshMobile.Application.LocalNetwork;

namespace DshMobile.Application.Tests;

/// <summary>ADR-0032: the Android 17 LNP decision and the private-address check, independent of Android.</summary>
public sealed class LocalNetworkPolicyTests
{
    [Theory]
    [InlineData(36, 37, true, false)] // Android 16: LNP is an opt-in compat flag, not this permission
    [InlineData(37, 36, true, false)] // legacy target: implicit grant, and the docs say do not ask
    [InlineData(29, 29, false, false)]
    public void BelowTheCutoffNothingIsRequired(int sdk, int target, bool declared, bool granted)
        => Assert.Equal(LocalNetworkDecision.NotRequired, LocalNetworkPolicy.Decide(sdk, target, declared, granted));

    [Fact]
    public void DeclaredAndGrantedIsGranted()
        => Assert.Equal(LocalNetworkDecision.Granted, LocalNetworkPolicy.Decide(37, 37, declared: true, granted: true));

    [Fact]
    public void DeclaredButNotGrantedNeedsThePermission()
        => Assert.Equal(LocalNetworkDecision.NeedsPermission, LocalNetworkPolicy.Decide(37, 37, declared: true, granted: false));

    [Fact]
    public void Targeting37WithoutTheManifestEntryIsABuildDefect()
        => Assert.Equal(LocalNetworkDecision.Undeclared, LocalNetworkPolicy.Decide(37, 37, declared: false, granted: false));

    [Fact]
    public void UndeclaredBeatsGrantedBecauseTheGrantCannotExist()
        => Assert.Equal(LocalNetworkDecision.Undeclared, LocalNetworkPolicy.Decide(38, 38, declared: false, granted: true));

    [Theory]
    [InlineData(LocalNetworkDecision.Granted, false)]
    [InlineData(LocalNetworkDecision.NotRequired, false)]
    [InlineData(LocalNetworkDecision.NeedsPermission, true)]
    [InlineData(LocalNetworkDecision.Undeclared, true)]
    public void OnlyDeniedAndUndeclaredRequireAction(LocalNetworkDecision decision, bool expected)
        => Assert.Equal(expected, LocalNetworkPolicy.RequiresAction(decision));

    [Theory]
    [InlineData("192.168.0.181", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("172.32.0.1", false)] // outside 172.16/12
    [InlineData("169.254.10.10", true)]
    [InlineData("192.169.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", false)] // loopback is not local network under LNP
    [InlineData("fe80::1", true)]
    [InlineData("[fe80::1]", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("::1", false)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("printer.local", true)]
    [InlineData("PRINTER.LOCAL", true)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("::ffff:192.168.1.1", true)] // IPv4-mapped private address
    public void PrivateAddressesAreRecognised(string? host, bool expected)
        => Assert.Equal(expected, LocalNetworkPolicy.IsPrivateHost(host));

    [Fact]
    public void CodesAreStable()
    {
        Assert.Equal("LAN_PERMISSION_DENIED", LocalNetworkCodes.PermissionDenied);
        Assert.Equal("LAN_PERMISSION_UNDECLARED", LocalNetworkCodes.PermissionUndeclared);
    }
}
