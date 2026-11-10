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
[JsonSerializable(typeof(JsonElement))]
public sealed partial class BridgeJsonContext : JsonSerializerContext;
