using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshMobile.Core.Contracts;

// Shapes that outlived the legacy JSON bridge (removed in PR-10, ADR-0018): the probe result the guest's dsh-probe.mjs
// prints, the remote host profile / status the remote page shows, and their stable codes. JSON names are unchanged.

/// <summary>Profiles the DSH probe accepts (same allowlist as <c>dsh-probe.mjs</c>).</summary>
public static class DshProfiles
{
    public static IReadOnlyList<string> All { get; } = ["web", "headless", "sdk", "sdk-minimal", "acp", "plugin"];
}

public sealed record ProbeCheck(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("ms")] long? Ms,
    [property: JsonPropertyName("detail")] string? Detail);

/// <summary>Probe outcome: <c>pass</c>, <c>limited</c> (needs a model credential to go further) or <c>fail</c>.</summary>
public sealed record ProbeResult(
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("needsCredential")] bool NeedsCredential,
    [property: JsonPropertyName("shim")] bool Shim,
    [property: JsonPropertyName("checks")] ProbeCheck[] Checks);

/// <summary>Stable codes in <see cref="RemoteStatusInfo.Code"/> (ADR-0006).</summary>
public static class RemoteCodes
{
    public const string Ok = "OK";
    public const string HostKeyUnknown = "HOST_KEY_UNKNOWN";
    public const string HostKeyChanged = "HOST_KEY_CHANGED";
    public const string AuthFailed = "AUTH_FAILED";
    public const string Unreachable = "UNREACHABLE";
    public const string Timeout = "TIMEOUT";
    public const string DshMissing = "DSH_MISSING";
    public const string DshStartFailed = "DSH_START_FAILED";
    public const string ForwardFailed = "FORWARD_FAILED";
    public const string NotConnected = "NOT_CONNECTED";
}

/// <summary>A configured remote host; contains no secret.</summary>
public sealed record RemoteHost(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("dshPort")] int DshPort,
    [property: JsonPropertyName("pinnedFingerprint")] string? PinnedFingerprint);

/// <summary>A host profile to save (Id null = new host).</summary>
public sealed record RemoteHostRequest(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("dshPort")] int DshPort);

public sealed record RemoteProbe(
    [property: JsonPropertyName("node")] bool Node,
    [property: JsonPropertyName("nodeVersion")] string? NodeVersion,
    [property: JsonPropertyName("dsh")] bool Dsh,
    [property: JsonPropertyName("dshVersion")] string? DshVersion);

/// <summary>
/// Remote connection status. <c>Code</c> is a <see cref="RemoteCodes"/> value; <c>Fingerprint</c> is set for
/// HOST_KEY_UNKNOWN / HOST_KEY_CHANGED. The remote launch token never appears here, only <c>HasLaunchToken</c>.
/// </summary>
public sealed record RemoteStatusInfo(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("hostId")] string? HostId,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("probe")] RemoteProbe? Probe,
    [property: JsonPropertyName("localPort")] int? LocalPort,
    [property: JsonPropertyName("dshPort")] int? DshPort,
    [property: JsonPropertyName("hasLaunchToken")] bool HasLaunchToken,
    [property: JsonPropertyName("fingerprint")] string? Fingerprint = null,
    [property: JsonPropertyName("keyType")] string? KeyType = null,
    [property: JsonPropertyName("httpUnauthenticated")] int? HttpUnauthenticated = null,
    [property: JsonPropertyName("remoteDshStillRunning")] bool? RemoteDshStillRunning = null);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ProbeResult))]
[JsonSerializable(typeof(RemoteHost))]
[JsonSerializable(typeof(RemoteHostRequest))]
[JsonSerializable(typeof(RemoteStatusInfo))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class ContractsJsonContext : JsonSerializerContext;
