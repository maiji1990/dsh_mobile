using System.Text.Json;
using DshMobile.Core.Bridge;

namespace DshMobile.Runtime.Legacy;

/// <summary>
/// <see cref="IRuntimeManager"/> over the existing DSHBox bridge (<c>DshNativeFacade</c> -> <c>RuntimeController.kt</c>
/// -> DSHBox sandbox-manager). This is the verified baseline: it stays the production implementation of each slice
/// until the C# replacement passes the same vectors and real-device acceptance, and then remains available for shadow
/// comparison until PR-10 removes the bridge. It adds no behavior of its own.
/// </summary>
public sealed class LegacyBridgeRuntimeManager : IRuntimeManager
{
    public static readonly TimeSpan ImportDeadline = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan SandboxDeadline = TimeSpan.FromMinutes(2);
    /// <summary>DSHBox waits up to 120 s for DSH readiness; leave headroom for sandbox warm-up.</summary>
    public static readonly TimeSpan DshStartDeadline = TimeSpan.FromMinutes(4);

    private readonly BridgeClient _client;
    private readonly TimeProvider _time;

    public LegacyBridgeRuntimeManager(BridgeClient client, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _time = time ?? TimeProvider.System;
    }

    public string Implementation => "legacy-bridge";

    public async Task<RuntimeSnapshot> GetStateAsync(CancellationToken ct = default)
    {
        var resp = await _client.ExecuteAsync(BridgeMethods.RuntimeStatus, cancellationToken: ct).ConfigureAwait(false);
        return ToResult(resp).Snapshot;
    }

    public async Task<RuntimeInstallResult> InstallAsync(RuntimePackage package, IProgress<RuntimeProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        var payload = BridgeCodec.ToPayload(new ImportBundleRequest(package.Path), BridgeJsonContext.Default.ImportBundleRequest);
        var resp = await _client.ExecuteAsync(BridgeMethods.RuntimeImportBundle, payload, ImportDeadline, ct).ConfigureAwait(false);
        return new RuntimeInstallResult(resp.Ok, resp.Code, resp.Message, UpstreamCode(resp));
    }

    public async Task<RuntimeStartResult> StartSandboxAsync(CancellationToken ct) =>
        ToResult(await _client.ExecuteAsync(BridgeMethods.SandboxStart, deadline: SandboxDeadline, cancellationToken: ct).ConfigureAwait(false));

    public async Task<RuntimeStartResult> StartHarnessAsync(DshProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Name != RuntimeConstants.DshWebProfile)
        {
            // The legacy bridge only starts `--profile web`; other profiles are probed, not hosted.
            return new RuntimeStartResult(false, RuntimeErrorCodes.NotImplemented, $"legacy runtime hosts only the '{RuntimeConstants.DshWebProfile}' profile",
                await GetStateAsync(ct).ConfigureAwait(false));
        }
        return ToResult(await _client.ExecuteAsync(BridgeMethods.DshStart, deadline: DshStartDeadline, cancellationToken: ct).ConfigureAwait(false));
    }

    public async Task<RuntimeStartResult> StopHarnessAsync(CancellationToken ct) =>
        ToResult(await _client.ExecuteAsync(BridgeMethods.DshStop, deadline: SandboxDeadline, cancellationToken: ct).ConfigureAwait(false));

    public async Task<RuntimeStartResult> StopSandboxAsync(CancellationToken ct) =>
        ToResult(await _client.ExecuteAsync(BridgeMethods.SandboxStop, deadline: SandboxDeadline, cancellationToken: ct).ConfigureAwait(false));

    /// <summary>Maps a legacy runtime.* / sandbox.* / dsh.* response; exposed for contract tests.</summary>
    public RuntimeStartResult ToResult(BridgeResponse resp)
    {
        ArgumentNullException.ThrowIfNull(resp);
        var now = _time.GetUtcNow();
        if (!resp.Ok)
        {
            return new RuntimeStartResult(false, resp.Code, resp.Message,
                new RuntimeSnapshot(RuntimeState.Failed, HarnessState.Stopped, 0, null, UpstreamCode(resp) ?? resp.Code, resp.Message, now),
                UpstreamCode(resp));
        }
        RuntimeStatus? st;
        try
        {
            st = BridgeCodec.ReadPayload(resp, BridgeJsonContext.Default.RuntimeStatus);
        }
        catch (JsonException e)
        {
            return Malformed(now, e.Message);
        }
        if (st is null)
        {
            return Malformed(now, "missing runtime status payload");
        }
        var state = LegacyStateMapper.MapSandbox(st.SandboxState, st.RuntimeInstalled);
        var harness = LegacyStateMapper.MapDsh(st.DshState);
        if (state is null || harness is null)
        {
            return Malformed(now, $"unknown legacy state sandbox={st.SandboxState} dsh={st.DshState}");
        }
        return new RuntimeStartResult(true, BridgeErrorCodes.Ok, null, new RuntimeSnapshot(state.Value, harness.Value, 0, st.DshVersion, null, null, now));
    }

    private static RuntimeStartResult Malformed(DateTimeOffset now, string message) =>
        new(false, BridgeErrorCodes.Internal, message, new RuntimeSnapshot(RuntimeState.Failed, HarnessState.Stopped, 0, null, BridgeErrorCodes.Internal, message, now));

    private static string? UpstreamCode(BridgeResponse resp) =>
        resp.Payload is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("upstreamCode", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;
}
