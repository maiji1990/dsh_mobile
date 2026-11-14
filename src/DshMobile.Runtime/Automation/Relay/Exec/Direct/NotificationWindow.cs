// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/direct/RelayNotificationListener.kt (Entry, Holder)
// Registered in docs/UPSTREAMS.md. The in-memory window notify.read answers from: newest first, one entry per key,
// at most 200, 30 minutes. Bodies are never written to disk. The Android listener service only feeds it.

namespace DshMobile.Runtime.Automation.Relay.Exec.Direct;

/// <summary>Upstream <c>RelayNotificationListener.Entry</c>.</summary>
public sealed record NotificationEntry(string Key, string PackageName, string Title, string Text, long PostedAtMs, bool Ongoing)
{
    /// <summary>Upstream <c>keyOf</c>: <c>"$packageName/$id/$tag"</c> (a null tag prints "null").</summary>
    public static string KeyOf(string? packageName, int id, string? tag) =>
        $"{packageName ?? "null"}/{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{tag ?? "null"}";
}

/// <summary>Upstream <c>RelayNotificationListener.Holder</c>, with the wall clock injected.</summary>
public sealed class NotificationWindow(Func<long> wallNowMs)
{
    public const int MaxEntries = 200;
    public const long WindowMs = 30L * 60L * 1000L;

    private readonly Func<long> _wallNowMs = wallNowMs ?? throw new ArgumentNullException(nameof(wallNowMs));
    private readonly LinkedList<NotificationEntry> _entries = new();
    private readonly Lock _lock = new();
    private object? _connected;

    /// <summary>Raised after the listener binds or unbinds (the capability list re-reads its availability).</summary>
    public event EventHandler? BoundChanged;

    public bool IsConnected => Volatile.Read(ref _connected) is not null;

    public IReadOnlyList<NotificationEntry> Snapshot(int limit, string? packageFilter, bool includeOngoing)
    {
        lock (_lock)
        {
            PruneExpired();
            var output = new List<NotificationEntry>(Math.Max(limit, 0));
            foreach (var entry in _entries)
            {
                if (!includeOngoing && entry.Ongoing)
                {
                    continue;
                }
                if (packageFilter is not null && entry.PackageName != packageFilter)
                {
                    continue;
                }
                output.Add(entry);
                if (output.Count >= limit)
                {
                    break;
                }
            }
            return output;
        }
    }

    private void PruneExpired()
    {
        var cutoff = _wallNowMs() - WindowMs;
        while (_entries.Last is { } last && last.Value.PostedAtMs < cutoff)
        {
            _entries.RemoveLast();
        }
    }

    /// <summary>Upstream <c>onListenerConnected</c>: replay the active ones oldest first, then attach.</summary>
    public void Attach(object listener, IEnumerable<NotificationEntry>? active)
    {
        ArgumentNullException.ThrowIfNull(listener);
        // Fill the history before attaching: once IsConnected is true the gate lets notify.read through.
        foreach (var entry in (active ?? []).OrderBy(e => e.PostedAtMs))
        {
            Record(entry);
        }
        Volatile.Write(ref _connected, listener);
        BoundChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Detach(object listener)
    {
        if (Interlocked.CompareExchange(ref _connected, null, listener) == listener)
        {
            lock (_lock)
            {
                _entries.Clear();
            }
            BoundChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A re-posted key replaces the old entry (a progress notification is one notification).</summary>
    public void Record(NotificationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Title.Length == 0 && entry.Text.Length == 0)
        {
            return;
        }
        lock (_lock)
        {
            RetireLocked(entry.Key);
            _entries.AddFirst(entry);
            while (_entries.Count > MaxEntries)
            {
                _entries.RemoveLast();
            }
        }
    }

    public void Retire(string key)
    {
        lock (_lock)
        {
            RetireLocked(key);
        }
    }

    private void RetireLocked(string key)
    {
        for (var node = _entries.First; node is not null;)
        {
            var next = node.Next;
            if (node.Value.Key == key)
            {
                _entries.Remove(node);
            }
            node = next;
        }
    }
}
