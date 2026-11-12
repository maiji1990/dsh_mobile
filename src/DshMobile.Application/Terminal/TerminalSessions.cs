// Session model ported from DSHBox terminal-session/src/main/java/com/dshbox/terminal/DshTerminalManager.kt
// (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4): ids from a global counter, display order "终端 N"
// that restarts at 1 once every window is closed, a new session becomes active, closing removes the entry at once and
// activates the first survivor, a shell that exits by itself stays listed with its exit code / signal, stopAll on
// runtime stop. Registered in docs/UPSTREAMS.md. The sessions are C#-owned PTYs (ProotPtySession), not Termux ones.

using DshMobile.Native;
using DshMobile.Runtime.Guest;

namespace DshMobile.Application.Terminal;

public sealed record TerminalSessionInfo(string Id, string Title, bool Active, bool Running, bool Exited, int ExitCode, int? Signal, int Pid);

/// <summary>Opens one guest login shell on a PTY of the given size (production: <see cref="ProotGuestProcess.OpenPtyAsync(GuestExecutionOptions, int, int, CancellationToken)"/>).</summary>
public delegate Task<ProotPtySession> PtyOpener(int rows, int columns, CancellationToken ct);

/// <summary>
/// The C# terminal's sessions (PR-06). They outlive the screen: a view attaches to one session, receives its recent
/// output (<see cref="ScrollbackBytes"/>) and then live output, and detaches without touching the shell. PTY bytes are
/// handed to the view directly, never through the JSON control plane.
/// </summary>
public sealed class TerminalSessions : IAsyncDisposable
{
    public const string Slice = "terminal.csharp";

    /// <summary>Output kept per session for re-attaching a view (the emulator's own scrollback lives in the view).</summary>
    public const int ScrollbackBytes = 256 * 1024;

    private static readonly TimeSpan DrainAfterExit = TimeSpan.FromSeconds(2);

    private readonly PtyOpener _open;
    private readonly Action<string> _log;
    private readonly Lock _lock = new();
    private readonly List<Entry> _entries = [];
    private int _nextId;
    private int _nextOrder = 1;
    private string? _activeId;

    public TerminalSessions(PtyOpener open, Action<string>? log = null)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _log = log ?? (_ => { });
    }

    /// <summary>Raised (on any thread, outside the lock) after the list, the active session or a session state changed.</summary>
    public event Action? Changed;

    public string? ActiveId
    {
        get
        {
            lock (_lock)
            {
                return _activeId;
            }
        }
    }

    public IReadOnlyList<TerminalSessionInfo> Snapshot()
    {
        lock (_lock)
        {
            return _entries.Select(e => e.Info(e.Id == _activeId)).ToList();
        }
    }

    /// <summary>PRoot pids of the sessions whose shell still runs (so shadows do not count them as legacy sessions).</summary>
    public IReadOnlyList<int> Pids
    {
        get
        {
            lock (_lock)
            {
                return _entries.Where(e => e.Running).Select(e => e.Pty.Pid).ToList();
            }
        }
    }

    /// <summary>Starts a login shell and makes it the active session. Failures are <see cref="GuestPtyException"/> (stable code).</summary>
    public async Task<TerminalSessionInfo> CreateAsync(int rows, int columns, CancellationToken ct)
    {
        var pty = await _open(rows, columns, ct).ConfigureAwait(false);
        Entry entry;
        TerminalSessionInfo info;
        lock (_lock)
        {
            entry = new Entry((++_nextId).ToString(System.Globalization.CultureInfo.InvariantCulture), _nextOrder++, pty);
            _entries.Add(entry);
            _activeId = entry.Id;
            info = entry.Info(active: true);
        }
        entry.Pump = Task.Run(() => PumpAsync(entry), CancellationToken.None);
        _ = WatchExitAsync(entry);
        _log($"{Slice}: session {entry.Id} ({info.Title}) started, pid {pty.Pid}, {columns}x{rows}");
        RaiseChanged();
        return info;
    }

    public void Activate(string id)
    {
        lock (_lock)
        {
            if (_activeId == id || _entries.All(e => e.Id != id))
            {
                return;
            }
            _activeId = id;
        }
        RaiseChanged();
    }

    /// <summary>Closes one window: removes it at once, then hangs up / kills its shell. False when no such session.</summary>
    public async Task<bool> CloseAsync(string id)
    {
        List<Entry> removed;
        lock (_lock)
        {
            removed = RemoveLocked(e => e.Id == id);
        }
        if (removed.Count == 0)
        {
            return false;
        }
        RaiseChanged();
        await DisposeAllAsync(removed).ConfigureAwait(false);
        return true;
    }

    /// <summary>Closes every window (runtime stop, like the legacy <c>stopAll</c>). Returns how many were closed.</summary>
    public async Task<int> CloseAllAsync()
    {
        List<Entry> removed;
        lock (_lock)
        {
            removed = RemoveLocked(_ => true);
        }
        if (removed.Count == 0)
        {
            return 0;
        }
        RaiseChanged();
        await DisposeAllAsync(removed).ConfigureAwait(false);
        return removed.Count;
    }

    /// <summary>
    /// Attaches a view: <paramref name="onOutput"/> first receives the kept output (if any), then every new chunk, in
    /// order. It is called under the session's lock on the reader thread, so it must only enqueue (never block, never
    /// call back into this class). Null when the session does not exist. Dispose the result to detach.
    /// </summary>
    public IDisposable? Attach(string id, Action<byte[]> onOutput)
    {
        ArgumentNullException.ThrowIfNull(onOutput);
        var entry = Find(id);
        return entry?.Attach(onOutput);
    }

    /// <summary>Writes typed bytes to the shell. False when the session is gone or its shell has exited.</summary>
    public bool Write(string id, ReadOnlySpan<byte> data)
    {
        var entry = Find(id);
        if (entry is null || !entry.Running)
        {
            return false;
        }
        try
        {
            entry.Pty.Stream.Write(data);
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or NativePtyException)
        {
            return false;
        }
    }

    public async Task<bool> ResizeAsync(string id, int columns, int rows, CancellationToken ct)
    {
        var entry = Find(id);
        if (entry is null || !entry.Running || columns <= 0 || rows <= 0)
        {
            return false;
        }
        try
        {
            await entry.Pty.ResizeAsync(columns, rows, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is NativePtyException or ObjectDisposedException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync() => await CloseAllAsync().ConfigureAwait(false);

    private Entry? Find(string id)
    {
        lock (_lock)
        {
            return _entries.FirstOrDefault(e => e.Id == id);
        }
    }

    /// <summary>Caller holds the lock.</summary>
    private List<Entry> RemoveLocked(Func<Entry, bool> selector)
    {
        var removed = _entries.Where(selector).ToList();
        foreach (var e in removed)
        {
            e.Removed = true;
            _entries.Remove(e);
        }
        if (_activeId is not null && _entries.All(e => e.Id != _activeId))
        {
            _activeId = _entries.FirstOrDefault()?.Id;
        }
        if (_entries.Count == 0)
        {
            _nextOrder = 1; // a new batch starts from "终端 1" again
        }
        return removed;
    }

    private async Task DisposeAllAsync(List<Entry> entries)
    {
        await Task.WhenAll(entries.Select(async e =>
        {
            var wasRunning = e.Running;
            await e.Pty.DisposeAsync().ConfigureAwait(false);
            await e.DrainedAsync().ConfigureAwait(false);
            _log($"{Slice}: session {e.Id} closed" + (wasRunning ? $" (shell {e.Pty.Completion.Status switch { TaskStatus.RanToCompletion => e.Pty.Completion.Result.ToString(), _ => "not reaped" }})" : ""));
        })).ConfigureAwait(false);
    }

    private static async Task PumpAsync(Entry entry)
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var n = await entry.Pty.Stream.ReadAsync(buf).ConfigureAwait(false);
                if (n == 0)
                {
                    return;
                }
                entry.Deliver(buf.AsSpan(0, n));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or NativePtyException)
        {
        }
    }

    /// <summary>A shell that ends by itself stays listed (not running, with its status) until the user closes it.</summary>
    private async Task WatchExitAsync(Entry entry)
    {
        PtyExit exit;
        try
        {
            exit = await entry.Pty.Completion.ConfigureAwait(false);
        }
        catch (NativePtyException e)
        {
            exit = new PtyExit(false, -1);
            _log($"{Slice}: session {entry.Id} wait failed: {e.Message}");
        }
        bool listed;
        lock (_lock)
        {
            entry.Running = false;
            listed = !entry.Removed;
            if (listed)
            {
                entry.Exited = true;
                entry.Exit = exit;
            }
        }
        if (!listed)
        {
            return; // closed on purpose: CloseAsync already updated the list
        }
        _log($"{Slice}: session {entry.Id} shell ended ({exit})");
        RaiseChanged();
        // Let the reader drain what the shell printed last ("logout"), then release the master.
        await entry.DrainedAsync().ConfigureAwait(false);
        await entry.Pty.DisposeAsync().ConfigureAwait(false);
    }

    private void RaiseChanged() => Changed?.Invoke();

    private sealed class Entry(string id, int order, ProotPtySession pty)
    {
        private readonly Lock _lock = new();
        private readonly List<Action<byte[]>> _listeners = [];
        private readonly byte[] _ring = new byte[ScrollbackBytes];
        private int _ringStart;
        private int _ringCount;

        public string Id { get; } = id;

        public ProotPtySession Pty { get; } = pty;

        public Task Pump { get; set; } = Task.CompletedTask;

        public volatile bool Running = true;

        public bool Exited;

        public PtyExit? Exit;

        public bool Removed;

        public TerminalSessionInfo Info(bool active) => new(
            Id,
            "终端 " + order.ToString(System.Globalization.CultureInfo.InvariantCulture),
            active,
            Running,
            Exited,
            Exit is { Signaled: false } e ? e.Value : 0,
            Exit is { Signaled: true } s ? s.Value : null,
            Pty.Pid);

        public void Deliver(ReadOnlySpan<byte> chunk)
        {
            lock (_lock)
            {
                Append(chunk);
                if (_listeners.Count == 0)
                {
                    return;
                }
                var copy = chunk.ToArray();
                foreach (var l in _listeners)
                {
                    l(copy);
                }
            }
        }

        public IDisposable Attach(Action<byte[]> listener)
        {
            lock (_lock)
            {
                if (_ringCount > 0)
                {
                    listener(Kept());
                }
                _listeners.Add(listener);
            }
            return new Detach(this, listener);
        }

        public async Task DrainedAsync()
        {
            try
            {
                await Pump.WaitAsync(DrainAfterExit).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // something still holds the slave open; disposing the master hangs it up
            }
        }

        private void Remove(Action<byte[]> listener)
        {
            lock (_lock)
            {
                _listeners.Remove(listener);
            }
        }

        private void Append(ReadOnlySpan<byte> chunk)
        {
            if (chunk.Length >= _ring.Length)
            {
                chunk[^_ring.Length..].CopyTo(_ring);
                _ringStart = 0;
                _ringCount = _ring.Length;
                return;
            }
            var end = (_ringStart + _ringCount) % _ring.Length;
            var first = Math.Min(chunk.Length, _ring.Length - end);
            chunk[..first].CopyTo(_ring.AsSpan(end));
            chunk[first..].CopyTo(_ring);
            var overflow = Math.Max(0, _ringCount + chunk.Length - _ring.Length);
            _ringStart = (_ringStart + overflow) % _ring.Length;
            _ringCount = Math.Min(_ring.Length, _ringCount + chunk.Length);
        }

        private byte[] Kept()
        {
            var result = new byte[_ringCount];
            var first = Math.Min(_ringCount, _ring.Length - _ringStart);
            _ring.AsSpan(_ringStart, first).CopyTo(result);
            _ring.AsSpan(0, _ringCount - first).CopyTo(result.AsSpan(first));
            return result;
        }

        private sealed class Detach(Entry entry, Action<byte[]> listener) : IDisposable
        {
            private int _done;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0)
                {
                    entry.Remove(listener);
                }
            }
        }
    }
}
