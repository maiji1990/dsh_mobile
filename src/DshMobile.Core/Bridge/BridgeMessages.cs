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
[JsonSerializable(typeof(JsonElement))]
public sealed partial class BridgeJsonContext : JsonSerializerContext;
