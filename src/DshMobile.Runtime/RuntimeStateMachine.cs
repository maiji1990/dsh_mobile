using DshMobile.Runtime.Journal;

namespace DshMobile.Runtime;

/// <summary>
/// Validated runtime lifecycle with a durable journal. Commanded transitions (<see cref="TryTransition"/>) must follow
/// <see cref="IsAllowed"/>; observed state from the legacy runtime (<see cref="Observe"/>) is recorded as-is but
/// marked as an observation, so the journal shows exactly who decided what. Thread-safe.
/// </summary>
public sealed class RuntimeStateMachine
{
    private static readonly Dictionary<RuntimeState, RuntimeState[]> Allowed = new()
    {
        [RuntimeState.NotInstalled] = [RuntimeState.Staging, RuntimeState.Ready, RuntimeState.Failed],
        [RuntimeState.Staging] = [RuntimeState.Ready, RuntimeState.NotInstalled, RuntimeState.Failed],
        [RuntimeState.Ready] = [RuntimeState.Staging, RuntimeState.Starting, RuntimeState.NotInstalled, RuntimeState.Failed],
        [RuntimeState.Starting] = [RuntimeState.Running, RuntimeState.Stopping, RuntimeState.Failed],
        [RuntimeState.Running] = [RuntimeState.Running, RuntimeState.Stopping, RuntimeState.Recovering, RuntimeState.Failed],
        [RuntimeState.Stopping] = [RuntimeState.Ready, RuntimeState.Failed],
        [RuntimeState.Failed] = [RuntimeState.Staging, RuntimeState.Starting, RuntimeState.Stopping, RuntimeState.Ready, RuntimeState.NotInstalled, RuntimeState.Recovering],
        [RuntimeState.Recovering] = [RuntimeState.Starting, RuntimeState.Running, RuntimeState.Ready, RuntimeState.Failed],
    };

    private readonly IRuntimeJournal _journal;
    private readonly TimeProvider _time;
    private readonly string _implementation;
    private readonly Lock _gate = new();
    private RuntimeSnapshot _current;

    public RuntimeStateMachine(IRuntimeJournal journal, string implementation, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        _journal = journal;
        _implementation = implementation;
        _time = time ?? TimeProvider.System;
        var last = journal.ReadLast();
        // A process restart cannot resume an in-flight operation: anything that was mid-way is reported as Failed
        // ("interrupted") instead of pretending it is still Starting/Stopping/Running.
        _current = last is null
            ? new RuntimeSnapshot(RuntimeState.NotInstalled, HarnessState.Stopped, 0, null, null, null, _time.GetUtcNow())
            : Recover(last);
    }

    public RuntimeSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Raised after every recorded change, outside the lock, on the caller's thread.</summary>
    public event EventHandler<RuntimeSnapshot>? Changed;

    public static bool IsAllowed(RuntimeState from, RuntimeState to) => Allowed[from].Contains(to);

    /// <summary>Applies a commanded transition; returns false (and records nothing) when it is not allowed.</summary>
    public bool TryTransition(RuntimeState to, string reason, HarnessState? harness = null, string? errorCode = null, string? errorMessage = null, string? dshVersion = null)
    {
        RuntimeSnapshot next;
        lock (_gate)
        {
            if (!IsAllowed(_current.State, to))
            {
                return false;
            }
            next = Apply(to, reason, harness, errorCode, errorMessage, dshVersion, observed: false);
        }
        Changed?.Invoke(this, next);
        return true;
    }

    /// <summary>Records state reported by another component (legacy bridge events). Not validated, but journaled.</summary>
    public void Observe(RuntimeState state, HarnessState harness, string? dshVersion, string source)
    {
        RuntimeSnapshot next;
        lock (_gate)
        {
            if (_current.State == state && _current.Harness == harness && _current.DshVersion == dshVersion)
            {
                return;
            }
            next = Apply(state, "observed:" + source, harness, null, null, dshVersion, observed: true);
        }
        Changed?.Invoke(this, next);
    }

    private RuntimeSnapshot Apply(RuntimeState to, string reason, HarnessState? harness, string? errorCode, string? errorMessage, string? dshVersion, bool observed)
    {
        var from = _current.State;
        var generation = to == RuntimeState.Starting && from != RuntimeState.Starting && !observed ? _current.Generation + 1 : _current.Generation;
        var keepError = to == RuntimeState.Failed;
        var next = new RuntimeSnapshot(
            to,
            harness ?? _current.Harness,
            generation,
            dshVersion ?? _current.DshVersion,
            keepError ? errorCode : null,
            keepError ? errorMessage : null,
            _time.GetUtcNow());
        // Journal first: if the write fails the in-memory state does not advance (the exception propagates).
        _journal.Append(new RuntimeJournalEntry(next.At, next.Generation, from, to, next.Harness, reason, next.ErrorCode, next.ErrorMessage, _implementation));
        _current = next;
        return next;
    }

    private RuntimeSnapshot Recover(RuntimeJournalEntry last)
    {
        var interrupted = last.To is RuntimeState.Staging or RuntimeState.Starting or RuntimeState.Stopping or RuntimeState.Recovering or RuntimeState.Running;
        if (!interrupted)
        {
            return new RuntimeSnapshot(last.To, last.Harness, last.Generation, null, last.ErrorCode, last.ErrorMessage, last.At);
        }
        var snapshot = new RuntimeSnapshot(RuntimeState.Failed, HarnessState.Stopped, last.Generation, null, "RUNTIME_INTERRUPTED",
            $"process ended while {last.To}", _time.GetUtcNow());
        _journal.Append(new RuntimeJournalEntry(snapshot.At, snapshot.Generation, last.To, RuntimeState.Failed, HarnessState.Stopped,
            "recovered-after-restart", snapshot.ErrorCode, snapshot.ErrorMessage, _implementation));
        return snapshot;
    }
}
