namespace DshMobile.Runtime.Updates;

/// <summary>
/// Where the signed release manifests live (ADR-0022 amendment, 2026-11-16): the source repository is public, so its
/// GitHub Releases are the anonymous distribution point. Each channel is a fixed release tag that only ever holds
/// <c>release-manifest.json</c> + <c>release-manifest.json.sig</c> (replaced by <c>release-publish.yml</c>); the
/// artifacts the manifest points at live in versioned releases. URLs are fixed here, never taken from a manifest, a
/// plugin or the agent.
/// </summary>
public static class ReleaseEndpoints
{
    public const string Repository = "maiji1990/dsh_mobile";
    public const string ManifestName = "release-manifest.json";

    public static IReadOnlyList<string> Channels { get; } = [ReleaseChannels.Stable, ReleaseChannels.Preview];

    public static bool IsChannel(string? channel) => channel is ReleaseChannels.Stable or ReleaseChannels.Preview;

    /// <summary>The release tag that carries a channel's manifest: <c>channel-stable</c> / <c>channel-preview</c>.</summary>
    public static string ChannelTag(string channel) =>
        IsChannel(channel) ? "channel-" + channel : throw new ArgumentException($"unknown release channel '{channel}'", nameof(channel));

    /// <summary><c>https://github.com/maiji1990/dsh_mobile/releases/download/channel-&lt;channel&gt;/release-manifest.json</c>.</summary>
    public static Uri ManifestUrl(string channel) => new($"https://github.com/{Repository}/releases/download/{ChannelTag(channel)}/{ManifestName}");
}
