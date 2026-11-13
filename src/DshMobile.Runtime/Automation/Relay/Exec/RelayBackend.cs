// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/RelayBackend.kt
// Registered in docs/UPSTREAMS.md.

using System.Text.Json;
using System.Text.Json.Nodes;
using DshMobile.Runtime.Automation.Relay.Storage;

namespace DshMobile.Runtime.Automation.Relay.Exec;

/// <summary>Upstream <c>BackendCall</c>: one admitted, approved call handed to a backend.</summary>
public sealed record BackendCall(
    string RequestId,
    string Capability,
    JsonElement Args,
    SurfaceKind Surface,
    RelayPaths Paths,
    long BudgetMs);

/// <summary>Upstream <c>BackendResult</c>. Backends never report the surface: the route was decided before them.</summary>
public abstract record BackendResult
{
    /// <summary><paramref name="Data"/> keeps insertion order (libcore JSONObject); artifact bytes are reported by the writer.</summary>
    public sealed record Ok(JsonObject Data, IReadOnlyList<string> Artifacts, long ArtifactBytes = 0, string? Reason = null) : BackendResult
    {
        public Ok()
            : this(new JsonObject(), [])
        {
        }

        public Ok(JsonObject data)
            : this(data, [])
        {
        }
    }

    public sealed record Failed(RelayError Error, string? Reason = null, string? ConsentKind = null) : BackendResult;
}

/// <summary>Upstream <c>RelayBackend</c>.</summary>
public interface IRelayBackend
{
    Task<BackendResult> ExecuteAsync(BackendCall backendCall, CancellationToken cancellationToken);

    bool Supports(string capability, SurfaceKind surface);

    bool Available();

    void Reconnect()
    {
    }

    /// <summary>Who owns the node at this preorder id (consent-surface checks); null when unknown.</summary>
    TargetOwner? TargetOwnerOf(int nodeId) => null;
}

/// <summary>Upstream <c>RelayBackend.TargetOwner</c>.</summary>
public sealed record TargetOwner(string? PackageName, string? ViewId);
