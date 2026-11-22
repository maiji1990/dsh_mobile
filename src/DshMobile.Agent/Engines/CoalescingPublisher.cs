namespace DshMobile.Agent.Engines;

/// <summary>
/// Leading+trailing 50 ms coalescing publisher with visibility awareness (`SkippedWhileHidden`) (N-03-04).
/// If quiet for at least <see cref="Window"/>, emits immediately on the leading edge (zero delay);
/// rapid updates during the active window coalesce into a single trailing-edge emission.
/// </summary>
public sealed class CoalescingPublisher : IAsyncDisposable
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(50);

    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly Func<ValueTask> _onPublish;
    private readonly Action<Exception>? _onPublishError;
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private long _lastPublishTicks = long.MinValue;
    private int _subscriberCount;
    private bool _pendingTrailing;
    private bool _dirtyWhileHidden;
    private bool _inWindow;
    private int _disposed;

    public CoalescingPublisher(
        Func<ValueTask> onPublish,
        TimeSpan? window = null,
        TimeProvider? time = null,
        Action<Exception>? onPublishError = null)
    {
        ArgumentNullException.ThrowIfNull(onPublish);
        _onPublish = onPublish;
        _onPublishError = onPublishError;
        _window = window ?? DefaultWindow;
        _time = time ?? TimeProvider.System;
    }

    public long SkippedWhileHidden { get; private set; }
    public long Publishes { get; private set; }

    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscriberCount;
            }
        }
    }

    /// <summary>Registers an active observer; triggers catch-up derive/publish on `0 -> 1` if hidden deltas arrived.</summary>
    public IDisposable Subscribe()
    {
        var triggerCatchup = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _subscriberCount++;
            if (_subscriberCount == 1 && _dirtyWhileHidden)
            {
                _dirtyWhileHidden = false;
                triggerCatchup = true;
            }
        }

        if (triggerCatchup)
        {
            PublishNow();
        }
        return new Subscription(this);
    }

    public void NotifyDirty()
    {
        var publishLeading = false;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            if (_subscriberCount == 0)
            {
                SkippedWhileHidden++;
                _dirtyWhileHidden = true;
                return;
            }

            if (!_inWindow && IsQuietPeriodElapsedLocked())
            {
                _inWindow = true;
                _lastPublishTicks = _time.GetTimestamp();
                EnsureWindowTimerLocked();
                publishLeading = true;
            }
            else
            {
                _pendingTrailing = true;
                if (!_inWindow)
                {
                    _inWindow = true;
                    EnsureWindowTimerLocked();
                }
            }
        }

        if (publishLeading)
        {
            PublishNow();
        }
    }

    /// <summary>Synchronously flushes any pending trailing publication (used on turn end / drain).</summary>
    public void Flush()
    {
        var shouldPublish = false;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            if (_subscriberCount == 0)
            {
                if (_pendingTrailing)
                {
                    _pendingTrailing = false;
                    _dirtyWhileHidden = true;
                    SkippedWhileHidden++;
                }
                _inWindow = false;
                return;
            }

            if (_pendingTrailing)
            {
                _pendingTrailing = false;
                _lastPublishTicks = _time.GetTimestamp();
                shouldPublish = true;
            }
            _inWindow = false;
        }

        if (shouldPublish)
        {
            PublishNow();
        }
    }

    private void OnUnsubscribed()
    {
        lock (_gate)
        {
            if (_subscriberCount > 0)
            {
                _subscriberCount--;
            }
        }
    }

    private void OnTimerTick()
    {
        var shouldPublish = false;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            if (_pendingTrailing)
            {
                _pendingTrailing = false;
                if (_subscriberCount == 0)
                {
                    _dirtyWhileHidden = true;
                    SkippedWhileHidden++;
                    _inWindow = false;
                    return;
                }
                _lastPublishTicks = _time.GetTimestamp();
                EnsureWindowTimerLocked();
                shouldPublish = true;
            }
            else
            {
                _inWindow = false;
            }
        }

        if (shouldPublish)
        {
            PublishNow();
        }
    }

    private bool IsQuietPeriodElapsedLocked()
    {
        if (_lastPublishTicks == long.MinValue)
        {
            return true;
        }
        return _time.GetElapsedTime(_lastPublishTicks) >= _window;
    }

    private void EnsureWindowTimerLocked()
    {
        if (_timer is null)
        {
            _timer = _time.CreateTimer(static s => ((CoalescingPublisher)s!).OnTimerTick(), this, _window, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _timer.Change(_window, Timeout.InfiniteTimeSpan);
        }
    }

    private void PublishNow()
    {
        Publishes++;
        try
        {
            var vt = _onPublish();
            if (!vt.IsCompletedSuccessfully)
            {
                _ = vt.AsTask().ContinueWith(
                    t =>
                    {
                        if (t.Exception is { } agg)
                        {
                            _onPublishError?.Invoke(agg.GetBaseException());
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _onPublishError?.Invoke(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        ITimer? timer;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
        }
        if (timer is not null)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Subscription(CoalescingPublisher owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.OnUnsubscribed();
            }
        }
    }
}
