using System.Collections.Concurrent;
using System.Text.Json;

namespace DshMobile.Core.Bridge;

/// <summary>
/// The wire to the native facade. <see cref="Send"/> must not block on the result;
/// responses and events arrive later through <see cref="IBridgeSink.OnInbound"/>, typically on a native worker thread.
/// </summary>
public interface IBridgeTransport
{
    void Attach(IBridgeSink sink);
    void Send(string requestJson);
}

public interface IBridgeSink
{
    void OnInbound(string json);
}

/// <summary>
/// Correlates protocol v1 requests with responses by requestId, applies a local deadline,
/// and never reports success for anything the native side did not confirm.
/// </summary>
public sealed class BridgeClient : IBridgeSink, IDisposable
{
    /// <summary>Extra time beyond the native deadline before the client gives up locally.</summary>
    public static readonly TimeSpan LocalGrace = TimeSpan.FromSeconds(2);

    private readonly IBridgeTransport _transport;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeResponse>> _pending = new();
    private long _orphanResponses;
    private long _malformedMessages;
    private volatile bool _disposed;

    public BridgeClient(IBridgeTransport transport, TimeProvider? time = null)
    {
        _transport = transport;
        _time = time ?? TimeProvider.System;
        _transport.Attach(this);
    }

    /// <summary>Raised for native events (e.g. bridge.lifecycle). Invoked on the inbound (native) thread.</summary>
    public event EventHandler<BridgeEvent>? EventReceived;

    /// <summary>Requests sent and still awaiting a response. Must return to 0 after a burst completes.</summary>
    public int InFlightCount => _pending.Count;

    /// <summary>Responses whose requestId was unknown (already timed out, cancelled, or never sent).</summary>
    public long OrphanResponses => Interlocked.Read(ref _orphanResponses);

    /// <summary>Inbound messages that violated the protocol.</summary>
    public long MalformedMessages => Interlocked.Read(ref _malformedMessages);

    /// <summary>
    /// Sends one request. Returns the native response, or a locally synthesized failure
    /// (<see cref="BridgeErrorCodes.DeadlineExceeded"/>, <see cref="BridgeErrorCodes.Closed"/>,
    /// <see cref="BridgeErrorCodes.Internal"/>). Throws <see cref="OperationCanceledException"/> only when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task<BridgeResponse> ExecuteAsync(
        string method,
        JsonElement? payload = null,
        TimeSpan? deadline = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        cancellationToken.ThrowIfCancellationRequested();

        var requestId = Guid.NewGuid().ToString("D");
        if (_disposed)
        {
            return BridgeResponse.Local(requestId, BridgeErrorCodes.Closed, "bridge client disposed");
        }

        var effectiveDeadline = deadline ?? BridgeProtocol.DefaultDeadline;
        if (effectiveDeadline <= TimeSpan.Zero || effectiveDeadline.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }

        var request = new BridgeRequest(BridgeProtocol.Version, requestId, method, (int)Math.Ceiling(effectiveDeadline.TotalMilliseconds), payload);
        var tcs = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        try
        {
            _transport.Send(BridgeCodec.EncodeRequest(request));
        }
        catch (Exception e)
        {
            _pending.TryRemove(requestId, out _);
            return BridgeResponse.Local(requestId, BridgeErrorCodes.Internal, $"transport send failed: {e.GetType().Name}: {e.Message}");
        }

        using var timeoutCts = new CancellationTokenSource(effectiveDeadline + LocalGrace, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        try
        {
            return await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BridgeResponse.Local(requestId, BridgeErrorCodes.DeadlineExceeded, $"no response within {effectiveDeadline + LocalGrace}");
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    void IBridgeSink.OnInbound(string json)
    {
        if (!BridgeCodec.TryDecodeInbound(json, out var response, out var evt))
        {
            Interlocked.Increment(ref _malformedMessages);
            return;
        }

        if (evt is not null)
        {
            try
            {
                EventReceived?.Invoke(this, evt);
            }
            catch
            {
                // Subscriber failures must not propagate into the native worker thread.
            }
            return;
        }

        if (response!.RequestId is { } id && _pending.TryGetValue(id, out var tcs))
        {
            tcs.TrySetResult(response);
        }
        else
        {
            Interlocked.Increment(ref _orphanResponses);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var (id, tcs) in _pending)
        {
            tcs.TrySetResult(BridgeResponse.Local(id, BridgeErrorCodes.Closed, "bridge client disposed"));
        }
    }
}
