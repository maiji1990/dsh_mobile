using DshMobile.Application.Terminal;
using DshMobile.Runtime.Guest;

namespace DshMobile.Application.Tests;

/// <summary>vNext PR-08: configurable extra-keys bar, latched ALT, the new control / punctuation keys, session rename.</summary>
public sealed class TerminalKeysV2Tests
{
    [Fact]
    public void Bar_DefaultsWhenTheLayoutIsMissingOrInvalid_AndKeepsTheSavedOrder()
    {
        Assert.Same(TerminalKeys.Bar, TerminalKeys.ParseBar(null));
        Assert.Same(TerminalKeys.Bar, TerminalKeys.ParseBar("  "));
        Assert.Same(TerminalKeys.Bar, TerminalKeys.ParseBar("NOPE,,ALSO_NOPE"));
        var bar = TerminalKeys.ParseBar(" CTRL_D ,TEXT:|,ESC,CTRL_D,BOGUS,UP");
        Assert.Equal(["CTRL_D", "TEXT:|", "ESC", "UP"], bar.Select(k => k.Key));
        Assert.Equal(["^D", "|", "ESC", "↑"], bar.Select(k => k.Label));
        Assert.Equal(bar, TerminalKeys.ParseBar(TerminalKeys.FormatBar(bar.Select(k => k.Key))));
    }

    [Fact]
    public void EveryCatalogKey_HasASequence_AndTheDefaultBarIsInTheCatalog()
    {
        foreach (var (label, key) in TerminalKeys.Catalog)
        {
            Assert.NotNull(TerminalKeys.Sequence(key, applicationCursor: false));
            Assert.False(string.IsNullOrEmpty(label));
        }
        Assert.All(TerminalKeys.Bar, k => Assert.Contains(k, TerminalKeys.Catalog));
        Assert.Equal(TerminalKeys.Catalog.Count, TerminalKeys.Catalog.Select(k => k.Key).Distinct().Count());
    }

    [Theory]
    [InlineData("CTRL_D", "\u0004")]
    [InlineData("CTRL_Z", "\u001a")]
    [InlineData("CTRL_L", "\u000c")]
    [InlineData("CTRL_R", "\u0012")]
    [InlineData("TEXT:|", "|")]
    [InlineData("TEXT:~", "~")]
    public void NewKeys_SendTheirBytes(string key, string bytes) => Assert.Equal(bytes, TerminalKeys.Sequence(key, false));

    [Theory]
    [InlineData("TEXT:")]
    [InlineData("TEXT:ab")]
    [InlineData("text:|")]
    public void MalformedTextKeys_AreUnknown(string key) => Assert.Null(TerminalKeys.Sequence(key, false));

    [Fact]
    public void Alt_PrefixesEscOnce_AndReleases()
    {
        Assert.Equal(("\u001bb", true), TerminalKeys.ApplyAlt("b"));
        Assert.Equal(("\u001b中", true), TerminalKeys.ApplyAlt("中"));
        Assert.Equal(("", false), TerminalKeys.ApplyAlt(""));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("\u0007\u001b[31m", "[31m")]
    [InlineData("  build  ", "build")]
    [InlineData("服务器日志", "服务器日志")]
    public void CleanTitle_StripsControlsAndBlanks(string? input, string? expected) => Assert.Equal(expected, TerminalSessions.CleanTitle(input));

    [Fact]
    public void CleanTitle_CutsAtTheLimit_WithoutSplittingASurrogatePair()
    {
        var name = new string('a', TerminalSessions.MaxTitleLength - 1) + "😀tail";
        var cut = TerminalSessions.CleanTitle(name)!;
        Assert.Equal(new string('a', TerminalSessions.MaxTitleLength - 1), cut);
        Assert.Equal(TerminalSessions.MaxTitleLength, TerminalSessions.CleanTitle(new string('b', 100))!.Length);
    }

    [Fact]
    public async Task Rename_ChangesTheTitle_BlankRestoresTheNumber_UnknownIsFalse()
    {
        Task<ProotPtySession> Open(int rows, int columns, CancellationToken ct) =>
            Task.FromResult(new ProotPtySession(new FakeShell(pid: 6100), "t6100", ["proot"]));
        await using var s = new TerminalSessions(Open);
        var info = await s.CreateAsync(24, 80, CancellationToken.None);
        var changed = 0;
        s.Changed += () => Interlocked.Increment(ref changed);
        Assert.True(s.Rename(info.Id, " 日志 "));
        Assert.Equal("日志", s.Snapshot()[0].Title);
        Assert.True(s.Rename(info.Id, "日志")); // same name: no change event
        Assert.Equal(1, changed);
        Assert.True(s.Rename(info.Id, ""));
        Assert.Equal("终端 1", s.Snapshot()[0].Title);
        Assert.False(s.Rename("999", "x"));
        Assert.Equal(2, changed);
    }
}

/// <summary>vNext PR-08: links from terminal output are untrusted; only http(s) with a host reach the confirm dialog.</summary>
public sealed class TerminalLinksTests
{
    [Theory]
    [InlineData("https://example.com/a?b=1", "example.com", false)]
    [InlineData("http://例子.测试/路径", "xn--fsqu00a.xn--0zwm56d", false)]
    [InlineData("https://google.com@evil.example/", "evil.example", true)]
    public void Accepts_HttpLinks_AndExposesTheRealHost(string text, string host, bool userInfo)
    {
        var link = TerminalLinks.Parse(text)!;
        Assert.Equal((host, userInfo), (link.Host, link.HasUserInfo));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("intent://scan/#Intent;scheme=zxing;end")]
    [InlineData("content://media/external")]
    [InlineData("https://")]
    [InlineData("/relative/path")]
    [InlineData("https://a b.example")]
    [InlineData("https://example.com/\u0007")]
    public void Refuses_EverythingElse(string? text) => Assert.Null(TerminalLinks.Parse(text));

    [Fact]
    public void Refuses_OverlongLinks() => Assert.Null(TerminalLinks.Parse("https://example.com/" + new string('a', TerminalLinks.MaxLength)));
}
