// ADR-0031 (P2): graceful guest stop. SIGTERM first so Node can flush its compile cache and release the writer lock,
// then the existing SIGKILL path finishes whatever is left.
using System.Diagnostics;

namespace DshMobile.Runtime.Hosting;

/// <summary>Sends SIGTERM to a process. Implementations must never throw and return false when the signal was not sent.</summary>
public interface IGuestTerminator
{
    bool TryTerminate(int pid);
}

/// <summary>
/// <c>kill(pid, SIGTERM)</c> through bionic/glibc. Windows has no SIGTERM for an arbitrary pid, so it reports false and
/// the caller falls straight through to the SIGKILL path.
/// </summary>
public sealed class PosixGuestTerminator : IGuestTerminator
{
    public static readonly PosixGuestTerminator Instance = new();

    private const int Sigterm = 15;

    public bool TryTerminate(int pid)
    {
        if (pid <= 1 || OperatingSystem.IsWindows())
        {
            return false;
        }
        try
        {
            return NativeKill(pid, Sigterm) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int NativeKill(int pid, int signal);
}

/// <summary>Outcome of a graceful stop attempt (unit-tested without PRoot).</summary>
public readonly record struct GracefulStopResult(bool Graceful, TimeSpan Elapsed, bool Signalled);

/// <summary>ADR-0031: SIGTERM, bounded wait, then let the caller escalate to SIGKILL.</summary>
public static class GracefulStop
{
    /// <summary>Polling interval while waiting for the signalled process to leave.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public static async Task<GracefulStopResult> RunAsync(
        int pid,
        IGuestTerminator terminator,
        Func<bool> hasExited,
        TimeSpan timeout,
        Action<string> log,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(terminator);
        ArgumentNullException.ThrowIfNull(hasExited);
        ArgumentNullException.ThrowIfNull(log);
        if (hasExited())
        {
            return new GracefulStopResult(true, TimeSpan.Zero, false);
        }
        if (!terminator.TryTerminate(pid))
        {
            log($"host: SIGTERM not available for pid {pid}; stopping it with SIGKILL");
            return new GracefulStopResult(false, TimeSpan.Zero, false);
        }
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (hasExited())
            {
                log($"host: dsh exited after SIGTERM in {sw.ElapsedMilliseconds} ms (graceful stop)");
                return new GracefulStopResult(true, sw.Elapsed, true);
            }
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
        log($"host: dsh still running {timeout.TotalMilliseconds:F0} ms after SIGTERM; escalating to SIGKILL");
        return new GracefulStopResult(false, sw.Elapsed, true);
    }
}
