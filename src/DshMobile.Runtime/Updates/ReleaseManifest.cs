using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshMobile.Runtime.Updates;

/// <summary>Stable error codes of release discovery (ADR-0022); shown as-is in the UI, never mapped to success.</summary>
public static class UpdateErrorCodes
{
    /// <summary>The app pins no release public key yet: nothing can be trusted, so nothing is offered.</summary>
    public const string NoTrustedKeys = "UPDATE_NO_TRUSTED_KEYS";
    public const string FetchFailed = "MANIFEST_FETCH_FAILED";
    public const string TooLarge = "MANIFEST_TOO_LARGE";
    public const string SignatureMissing = "MANIFEST_SIGNATURE_MISSING";
    public const string KeyUnknown = "MANIFEST_KEY_UNKNOWN";
    public const string SignatureInvalid = "MANIFEST_SIGNATURE_INVALID";
    public const string SchemaInvalid = "MANIFEST_SCHEMA_INVALID";
    public const string ChannelMismatch = "MANIFEST_CHANNEL_MISMATCH";
    public const string Expired = "MANIFEST_EXPIRED";
    public const string NotYetValid = "MANIFEST_NOT_YET_VALID";
    public const string Rollback = "MANIFEST_ROLLBACK";
}

/// <summary>What a release artifact replaces on the device.</summary>
public static class ReleaseKinds
{
    public const string Runtime = "runtime";
    public const string Node = "node";
    public const string Dsh = "dsh";
    public const string App = "app";

    public static IReadOnlyList<string> All { get; } = [Runtime, Node, Dsh, App];
}

public static class ReleaseChannels
{
    public const string Stable = "stable";
    public const string Preview = "preview";
}

/// <summary>One downloadable artifact of a release manifest (schema 1). Optional fields are null when absent.</summary>
public sealed record ReleaseArtifact(
    string Kind,
    string Version,
    string Abi,
    string Archive,
    Uri Url,
    string Sha256,
    long Size,
    string? MinAppVersion,
    string? RequiresNode,
    string? SourceRepo,
    string? SourceTag,
    string? SourceCommit);

/// <summary>A release manifest whose signature has been verified and whose content passed <see cref="ReleaseManifestParser"/>.</summary>
public sealed record ReleaseManifest(
    int Schema,
    string Channel,
    DateTimeOffset GeneratedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<ReleaseArtifact> Artifacts);

/// <summary>Thrown by the parser; the message names the offending field (shown with <see cref="UpdateErrorCodes.SchemaInvalid"/>).</summary>
public sealed class ReleaseManifestException(string message) : Exception(message);

/// <summary>
/// Strict reader for release manifest schema 1 (ADR-0022). Runs only on bytes whose signature already verified; still
/// strict, because a signed manifest with an ambiguous meaning is a bug in the release pipeline that must not reach an
/// installer: duplicate JSON keys, non-https or credential-bearing URLs, archive names with paths, digests that are not
/// 64 lowercase hex, artifacts listed twice, and DSH layers without their pinned source (repo, tag, commit) are refused.
/// Unknown properties are ignored (they are covered by the signature; a breaking change bumps <c>schema</c>).
/// Uses <see cref="JsonDocument"/> only (trimming / AOT safe, no reflection).
/// </summary>
public static partial class ReleaseManifestParser
{
    public const int Schema = 1;

    /// <summary>Generous upper bound for one artifact (a full Debian base layer is a few hundred MB).</summary>
    public const long MaxArtifactBytes = 4L * 1024 * 1024 * 1024;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();

    [GeneratedRegex("^([0-9a-f]{40}|[0-9a-f]{64})$")]
    private static partial Regex CommitHex();

    [GeneratedRegex(@"^v?[0-9][0-9A-Za-z.+\-]{0,63}$")]
    private static partial Regex VersionToken();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,127}$")]
    private static partial Regex FileName();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_\-]{0,31}$")]
    private static partial Regex AbiToken();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+$")]
    private static partial Regex RepoSlug();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/+\-]{0,127}$")]
    private static partial Regex Token();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,9})?(Z|[+\-]\d{2}:\d{2})$")]
    private static partial Regex Rfc3339();

    private static readonly string[] ArchiveSuffixes = [".tar.zst", ".tar.gz", ".tar", ".zip", ".apk"];

    public static ReleaseManifest Parse(ReadOnlySpan<byte> utf8)
    {
        JsonDocument doc;
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            doc = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException e)
        {
            throw new ReleaseManifestException("not valid JSON: " + e.Message);
        }
        using (doc)
        {
            var root = doc.RootElement;
            RequireObject(root, "manifest");
            var schema = root.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n) ? n : -1;
            if (schema != Schema)
            {
                throw new ReleaseManifestException($"schema must be {Schema}");
            }
            var channel = RequiredString(root, "channel", "manifest");
            if (channel is not (ReleaseChannels.Stable or ReleaseChannels.Preview))
            {
                throw new ReleaseManifestException("channel must be 'stable' or 'preview'");
            }
            var generated = RequiredTime(root, "generatedAt");
            var expires = RequiredTime(root, "expiresAt");
            if (expires <= generated)
            {
                throw new ReleaseManifestException("expiresAt must be after generatedAt");
            }
            if (!root.TryGetProperty("artifacts", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                throw new ReleaseManifestException("artifacts must be an array");
            }
            var artifacts = new List<ReleaseArtifact>();
            var seen = new HashSet<(string, string, string)>();
            var i = 0;
            foreach (var a in arr.EnumerateArray())
            {
                var artifact = ParseArtifact(a, $"artifacts[{i++}]");
                if (!seen.Add((artifact.Kind, artifact.Version, artifact.Abi)))
                {
                    throw new ReleaseManifestException($"{artifact.Kind} {artifact.Version} ({artifact.Abi}) is listed twice");
                }
                artifacts.Add(artifact);
            }
            return new ReleaseManifest(schema, channel, generated, expires, artifacts);
        }
    }

    private static ReleaseArtifact ParseArtifact(JsonElement a, string at)
    {
        RequireObject(a, at);
        var kind = RequiredString(a, "kind", at);
        if (!ReleaseKinds.All.Contains(kind))
        {
            throw new ReleaseManifestException($"{at}.kind '{kind}' is not one of {string.Join(", ", ReleaseKinds.All)}");
        }
        var version = Matching(a, "version", at, VersionToken());
        var abi = Matching(a, "abi", at, AbiToken());
        var archive = Matching(a, "archive", at, FileName());
        if (!ArchiveSuffixes.Any(x => archive.EndsWith(x, StringComparison.Ordinal)))
        {
            throw new ReleaseManifestException($"{at}.archive must end with one of {string.Join(", ", ArchiveSuffixes)}");
        }
        var urlText = RequiredString(a, "url", at);
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || url.UserInfo.Length > 0 || url.Fragment.Length > 0 || url.Host.Length == 0)
        {
            throw new ReleaseManifestException($"{at}.url must be an absolute https URL without credentials or fragment");
        }
        var sha = Matching(a, "sha256", at, Sha256Hex());
        var size = a.TryGetProperty("size", out var z) && z.ValueKind == JsonValueKind.Number && z.TryGetInt64(out var len) ? len : 0;
        if (size <= 0 || size > MaxArtifactBytes)
        {
            throw new ReleaseManifestException($"{at}.size must be 1..{MaxArtifactBytes}");
        }
        var minApp = Optional(a, "minAppVersion", at, VersionToken());
        var requiresNode = Optional(a, "requiresNode", at, Token());
        var repo = Optional(a, "sourceRepo", at, RepoSlug());
        var tag = Optional(a, "sourceTag", at, Token());
        var commit = Optional(a, "sourceCommit", at, CommitHex());
        if (kind == ReleaseKinds.Dsh && (repo is null || tag is null || commit is null))
        {
            throw new ReleaseManifestException($"{at}: a dsh layer must pin sourceRepo, sourceTag and sourceCommit");
        }
        return new ReleaseArtifact(kind, version, abi, archive, url, sha, size, minApp, requiresNode, repo, tag, commit);
    }

    /// <summary>Objects must not repeat a key: parsers disagree on which copy wins, and the signature covers both.</summary>
    private static void RequireObject(JsonElement e, string at)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw new ReleaseManifestException($"{at} must be an object");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject())
        {
            if (!names.Add(p.Name))
            {
                throw new ReleaseManifestException($"{at} repeats the key '{p.Name}'");
            }
        }
    }

    private static string RequiredString(JsonElement e, string name, string at) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s
            : throw new ReleaseManifestException($"{at}.{name} must be a non-empty string");

    private static string Matching(JsonElement e, string name, string at, Regex rule)
    {
        var s = RequiredString(e, name, at);
        return rule.IsMatch(s) ? s : throw new ReleaseManifestException($"{at}.{name} '{s}' is malformed");
    }

    private static string? Optional(JsonElement e, string name, string at, Regex rule)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return Matching(e, name, at, rule);
    }

    private static DateTimeOffset RequiredTime(JsonElement e, string name)
    {
        var s = RequiredString(e, name, "manifest");
        // RFC 3339 with Z or an explicit offset; never a local time.
        return Rfc3339().IsMatch(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : throw new ReleaseManifestException($"manifest.{name} '{s}' is not an RFC 3339 time with Z or an offset");
    }
}
