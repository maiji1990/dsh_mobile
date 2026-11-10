using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshMobile.Core.Bridge;

public static class BridgeProtocol
{
    public const int Version = 1;
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(30);
}

/// <summary>Known protocol v1 methods (docs/PROVIDER_CONTRACTS.md 1.6). Append only.</summary>
public static class BridgeMethods
{
    public const string Info = "bridge.info";
    public const string Ping = "bridge.ping";
    public const string DshboxVersionsCompare = "dshbox.versions.compare";

    // TASK-004: DSHBox sandbox-manager (long-running; executed off the control thread, serialized).
    public const string RuntimeStatus = "runtime.status";
    public const string RuntimePrepare = "runtime.prepare";
    public const string RuntimeImportBundle = "runtime.importBundle";
    public const string RuntimeHealth = "runtime.health";
    public const string RuntimeDiagnostics = "runtime.diagnostics";
    public const string SandboxStart = "sandbox.start";
    public const string SandboxStop = "sandbox.stop";
    public const string DshStart = "dsh.start";
    public const string DshStop = "dsh.stop";

    // TASK-005: real per-profile handshakes inside the guest (fixed probe script, allowlisted profile names).
    public const string DshProbe = "dsh.probe";

    // TASK-009: `dsh plugin` (pnpm via bundled corepack). Spec/name are validated natively (npm registry specs only).
    public const string PluginList = "plugin.list";
    public const string PluginAdd = "plugin.add";
    public const string PluginRemove = "plugin.remove";

    // TASK-010 (ADR-0004): Interlock Relay execution layer for ui_*; optional, off until relay.start.
    public const string RelayStart = "relay.start";
    public const string RelayStop = "relay.stop";
    public const string RelayStatus = "relay.status";
    /// <summary>Fixed read-only end-to-end check (relay doctor + one ui.snapshot); returns <see cref="PluginCommandResult"/>.</summary>
    public const string RelaySelftest = "relay.selftest";

    // TASK-011 (ADR-0005): DSHBox browser stack; browser_* tools only after browser.tools.enable.
    public const string BrowserStatus = "browser.status";
    /// <summary>Opens the browser UI (library BrowserActivity in the :browser process); returns <see cref="BrowserStatusInfo"/>.</summary>
    public const string BrowserOpen = "browser.open";
    /// <summary>Installs the browser-use adapter into the web profile; returns <see cref="PluginCommandResult"/>.</summary>
    public const string BrowserToolsEnable = "browser.tools.enable";
    public const string BrowserToolsDisable = "browser.tools.disable";
    /// <summary>Fixed read-only check through the adapter's own client (status + observe); returns <see cref="PluginCommandResult"/>.</summary>
    public const string BrowserSelftest = "browser.selftest";

    // TASK-012 (ADR-0006): remote DSH host over SSH. SSH outcomes come back as RemoteStatus.Code, not bridge errors.
    public const string RemoteIdentity = "remote.identity";
    public const string RemoteHostsList = "remote.hosts.list";
    public const string RemoteHostsSave = "remote.hosts.save";
    public const string RemoteHostsRemove = "remote.hosts.remove";
    /// <summary>Pins the fingerprint the host just presented (only that exact value is accepted).</summary>
    public const string RemoteTrust = "remote.trust";
    public const string RemoteConnect = "remote.connect";
    public const string RemoteStart = "remote.start";
    public const string RemoteStop = "remote.stop";
    public const string RemoteDisconnect = "remote.disconnect";
    public const string RemoteStatus = "remote.status";
}

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

/// <summary>Event names pushed by the native side.</summary>
public static class BridgeEvents
{
    public const string Lifecycle = "bridge.lifecycle";
    public const string RuntimeState = "runtime.state";
}

public sealed record BridgeRequest(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("deadlineMs")] int DeadlineMs,
    [property: JsonPropertyName("payload")] JsonElement? Payload);

public sealed record BridgeResponse(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("requestId")] string? RequestId,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("payload")] JsonElement? Payload)
{
    /// <summary>A response synthesized on the C# side (timeout, transport failure); never sent by Native.</summary>
    public static BridgeResponse Local(string requestId, string code, string message) =>
        new(BridgeProtocol.Version, requestId, false, code, message, null);
}

public sealed record BridgeEvent(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("payload")] JsonElement? Payload);

/// <summary>Payload of <see cref="BridgeMethods.Info"/>.</summary>
public sealed record BridgeInfo(
    [property: JsonPropertyName("bridgeVersion")] string BridgeVersion,
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("upstream")] Dictionary<string, string> Upstream,
    [property: JsonPropertyName("kotlinVersion")] string KotlinVersion,
    [property: JsonPropertyName("sdkInt")] int SdkInt,
    [property: JsonPropertyName("abis")] string[] Abis,
    [property: JsonPropertyName("deviceModel")] string DeviceModel);

public sealed record VersionsCompareRequest(
    [property: JsonPropertyName("a")] string A,
    [property: JsonPropertyName("b")] string B);

public sealed record VersionsCompareResult(
    [property: JsonPropertyName("result")] int Result);

/// <summary>Payload of runtime.* / sandbox.* / dsh.* responses (and, partially, of runtime.state events).</summary>
public sealed record RuntimeStatus(
    [property: JsonPropertyName("sandboxState")] string SandboxState,
    [property: JsonPropertyName("dshState")] string DshState,
    [property: JsonPropertyName("dshVersion")] string? DshVersion,
    [property: JsonPropertyName("dshUnresponsive")] bool DshUnresponsive,
    [property: JsonPropertyName("runtimeInstalled")] bool RuntimeInstalled,
    [property: JsonPropertyName("nodeLayerInstalled")] bool NodeLayerInstalled,
    [property: JsonPropertyName("hasLaunchToken")] bool HasLaunchToken,
    [property: JsonPropertyName("linkShim")] bool LinkShim,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("freeBytes")] long FreeBytes,
    [property: JsonPropertyName("steps")] string[]? Steps = null,
    [property: JsonPropertyName("dshProvision")] string? DshProvision = null);

public sealed record RuntimeStateEvent(
    [property: JsonPropertyName("sandboxState")] string SandboxState,
    [property: JsonPropertyName("dshState")] string DshState,
    [property: JsonPropertyName("dshVersion")] string? DshVersion);

public sealed record ImportBundleRequest(
    [property: JsonPropertyName("path")] string Path);

public sealed record RuntimeHealth(
    [property: JsonPropertyName("dshProcessRunning")] bool DshProcessRunning,
    [property: JsonPropertyName("portOpen")] bool PortOpen,
    [property: JsonPropertyName("webUiReady")] bool WebUiReady,
    [property: JsonPropertyName("lastError")] string? LastError);

/// <summary>Profiles accepted by <see cref="BridgeMethods.DshProbe"/> (same allowlist as the native side).</summary>
public static class DshProfiles
{
    public static IReadOnlyList<string> All { get; } = ["web", "headless", "sdk", "sdk-minimal", "acp", "plugin"];
}

public sealed record ProbeRequest(
    [property: JsonPropertyName("profile")] string Profile);

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

public sealed record PluginRequest(
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("spec")] string? Spec = null,
    [property: JsonPropertyName("name")] string? Name = null);

/// <summary>Result of a `dsh plugin` command: real exit status and output (non-zero exit = Ok false).</summary>
public sealed record PluginCommandResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("exitCode")] int? ExitCode,
    [property: JsonPropertyName("output")] string Output,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>Payload of relay.* responses.</summary>
public sealed record RelayStatus(
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("channelRunning")] bool ChannelRunning,
    [property: JsonPropertyName("degradedReason")] string? DegradedReason,
    [property: JsonPropertyName("accessibilityEnabled")] bool AccessibilityEnabled,
    [property: JsonPropertyName("allowed")] string[] Allowed,
    [property: JsonPropertyName("guestCli")] string GuestCli,
    [property: JsonPropertyName("uiTools")] UiToolsStatus? UiTools = null);

/// <summary>
/// Registration of the guest ui_* MCP facade with DSH. <c>Registered</c> is the patch layer on disk;
/// <c>LoadedByDsh</c> is what the running DSH read at its start (null when DSH is not running or was not started
/// by this app); <c>DshRestartNeeded</c> means the two differ; <c>Conflict</c> means the layer file holds content
/// this app did not write (left untouched).
/// </summary>
public sealed record UiToolsStatus(
    [property: JsonPropertyName("registered")] bool Registered,
    [property: JsonPropertyName("loadedByDsh")] bool? LoadedByDsh,
    [property: JsonPropertyName("dshRestartNeeded")] bool DshRestartNeeded,
    [property: JsonPropertyName("conflict")] bool Conflict,
    [property: JsonPropertyName("serverName")] string ServerName);

/// <summary>
/// Payload of browser.status / browser.open. <c>AdapterInstalled</c> is the browser-use package in the web profile;
/// <c>ToolsLoadedByDsh</c> is what the running DSH read at its start (null when DSH is not running or was not
/// started by this app); <c>DshRestartNeeded</c> means the two differ.
/// </summary>
public sealed record BrowserStatusInfo(
    [property: JsonPropertyName("brokerAvailable")] bool BrokerAvailable,
    [property: JsonPropertyName("brokerRunning")] bool BrokerRunning,
    [property: JsonPropertyName("cliReady")] bool CliReady,
    [property: JsonPropertyName("channelPublished")] bool ChannelPublished,
    [property: JsonPropertyName("adapterInstalled")] bool AdapterInstalled,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("guestCli")] string GuestCli,
    [property: JsonPropertyName("sandboxState")] string SandboxState,
    [property: JsonPropertyName("toolsLoadedByDsh")] bool? ToolsLoadedByDsh,
    [property: JsonPropertyName("dshRestartNeeded")] bool DshRestartNeeded,
    [property: JsonPropertyName("adapterLogLines")] string[]? AdapterLogLines = null);

/// <summary>The phone's SSH public key (private key stays in Android Keystore).</summary>
public sealed record RemoteIdentityInfo(
    [property: JsonPropertyName("publicKey")] string PublicKey,
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("hardwareBacked")] bool? HardwareBacked);

/// <summary>A configured remote host; contains no secret.</summary>
public sealed record RemoteHost(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("dshPort")] int DshPort,
    [property: JsonPropertyName("pinnedFingerprint")] string? PinnedFingerprint);

public sealed record RemoteHostList([property: JsonPropertyName("hosts")] RemoteHost[] Hosts);

/// <summary>Request for remote.hosts.save (Id null = new host).</summary>
public sealed record RemoteHostRequest(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("dshPort")] int DshPort);

public sealed record RemoteHostIdRequest([property: JsonPropertyName("hostId")] string HostId);

public sealed record RemoteTrustRequest(
    [property: JsonPropertyName("hostId")] string HostId,
    [property: JsonPropertyName("fingerprint")] string Fingerprint);

public sealed record RemoteProbe(
    [property: JsonPropertyName("node")] bool Node,
    [property: JsonPropertyName("nodeVersion")] string? NodeVersion,
    [property: JsonPropertyName("dsh")] bool Dsh,
    [property: JsonPropertyName("dshVersion")] string? DshVersion);

/// <summary>
/// remote.* result. <c>Code</c> is a <see cref="RemoteCodes"/> value; <c>Fingerprint</c> is set for HOST_KEY_UNKNOWN /
/// HOST_KEY_CHANGED. The remote launch token never appears here, only <c>HasLaunchToken</c>.
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

public sealed record DiagnosticResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("output")] string Output,
    [property: JsonPropertyName("error")] string? Error);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(BridgeRequest))]
[JsonSerializable(typeof(BridgeResponse))]
[JsonSerializable(typeof(BridgeEvent))]
[JsonSerializable(typeof(BridgeInfo))]
[JsonSerializable(typeof(VersionsCompareRequest))]
[JsonSerializable(typeof(VersionsCompareResult))]
[JsonSerializable(typeof(RuntimeStatus))]
[JsonSerializable(typeof(RuntimeStateEvent))]
[JsonSerializable(typeof(ImportBundleRequest))]
[JsonSerializable(typeof(RuntimeHealth))]
[JsonSerializable(typeof(Dictionary<string, DiagnosticResult>))]
[JsonSerializable(typeof(ProbeRequest))]
[JsonSerializable(typeof(ProbeResult))]
[JsonSerializable(typeof(PluginRequest))]
[JsonSerializable(typeof(PluginCommandResult))]
[JsonSerializable(typeof(RelayStatus))]
[JsonSerializable(typeof(UiToolsStatus))]
[JsonSerializable(typeof(BrowserStatusInfo))]
[JsonSerializable(typeof(RemoteIdentityInfo))]
[JsonSerializable(typeof(RemoteHostList))]
[JsonSerializable(typeof(RemoteHost))]
[JsonSerializable(typeof(RemoteHostRequest))]
[JsonSerializable(typeof(RemoteHostIdRequest))]
[JsonSerializable(typeof(RemoteTrustRequest))]
[JsonSerializable(typeof(RemoteStatusInfo))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class BridgeJsonContext : JsonSerializerContext;
