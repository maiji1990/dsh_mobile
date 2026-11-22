using System.Text.Json;
using DshMobile.Agent.Protocol;

namespace DshMobile.Agent.Transport;

public enum RemoteConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Draining,
    Disposed,
}

/// <summary>Public snapshot of an active `/api/remote.mux` connection generation (never exposes secrets).</summary>
public sealed record ConnectionGeneration(
    long GenerationId,
    string ClientId,
    string? HostHome);

/// <summary>Decoded unary HTTP `/api/&lt;endpoint&gt;` response.</summary>
public sealed record RemoteUnaryOutcome(
    bool Ok,
    JsonElement? Value,
    RemoteRpcError? Error,
    IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Attachments);

/// <summary>Handle to an active multiplexed `/api/remote.mux` logical stream.</summary>
public interface IRemoteStreamHandle : IAsyncDisposable
{
    string StreamId { get; }
    long GenerationId { get; }
    Task Completion { get; }
    ValueTask SendItemAsync(JsonElement value, CancellationToken ct = default);
    ValueTask EndUplinkAsync(CancellationToken ct = default);
}

public class RemoteConnectionException : Exception
{
    public RemoteConnectionException(string message) : base(message) { }
    public RemoteConnectionException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class RemoteAuthenticationException : RemoteConnectionException
{
    public int? StatusCode { get; }

    public RemoteAuthenticationException(string message, int? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }
}

public sealed class RemoteProtocolException : RemoteConnectionException
{
    public RemoteProtocolException(string message) : base(message) { }
    public RemoteProtocolException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class RemoteDrainingException : RemoteConnectionException
{
    public RemoteDrainingException()
        : base("Remote connection is draining or closed.")
    {
    }
}
