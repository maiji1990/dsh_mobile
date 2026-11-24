namespace DshMobile.Runtime;

/// <summary>
/// Runtime lifecycle (migration plan section 5). Every transition is validated by <see cref="RuntimeStateMachine"/>
/// and written to the durable journal; UI text and booleans are never the source of truth.
/// </summary>
public enum RuntimeState
{
    /// <summary>No usable base/node layers.</summary>
    NotInstalled,
    /// <summary>A bundle or layer update is being verified / extracted.</summary>
    Staging,
    /// <summary>Layers installed; sandbox not running.</summary>
    Ready,
    /// <summary>Sandbox and/or DSH are starting.</summary>
    Starting,
    /// <summary>Sandbox running (DSH may or may not be; see <see cref="RuntimeSnapshot.Harness"/>).</summary>
    Running,
    Stopping,
    /// <summary>Last operation failed; <see cref="RuntimeSnapshot.ErrorCode"/> says why.</summary>
    Failed,
    /// <summary>Automatic recovery (restart / rollback) in progress.</summary>
    Recovering,
}

/// <summary>DSH (harness) lifecycle inside a running sandbox.</summary>
public enum HarnessState
{
    Stopped,
    Starting,
    /// <summary>Process up, WebUI not answering yet.</summary>
    Running,
    /// <summary>WebUI answered; usable.</summary>
    Ready,
    Failed,
}

/// <summary>Immutable view of the runtime at one point; <see cref="Generation"/> increases on every start.</summary>
public sealed record RuntimeSnapshot(
    RuntimeState State,
    HarnessState Harness,
    long Generation,
    string? DshVersion,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset At);

public sealed record RuntimeProgress(string Step, long Done, long Total);

/// <summary>A runtime bundle on app-private storage (already copied out of SAF).</summary>
public sealed record RuntimePackage(string Path);

public sealed record RuntimeInstallResult(bool Ok, string Code, string? Message, string? UpstreamCode = null);

public sealed record RuntimeStartResult(bool Ok, string Code, string? Message, RuntimeSnapshot Snapshot, string? UpstreamCode = null);

/// <summary>A shipped DSH profile (<c>dsh --profile &lt;name&gt;</c>).</summary>
public sealed record DshProfile(string Name)
{
    public static DshProfile Web { get; } = new(RuntimeConstants.DshWebProfile);
}

/// <summary>Stable runtime error codes.</summary>
public static class RuntimeErrorCodes
{
    public const string Ok = "OK";
    /// <summary>The caller's arguments were rejected before anything ran (same value as the former bridge code).</summary>
    public const string BadRequest = "BAD_REQUEST";
    public const string InvalidTransition = "RUNTIME_INVALID_TRANSITION";
    public const string NotInstalled = "RUNTIME_NOT_INSTALLED";
    /// <summary>A lower layer (installer, guest) failed; its own code is kept separately.</summary>
    public const string Upstream = "RUNTIME_ERROR";
    public const string NotImplemented = "NOT_IMPLEMENTED";
    /// <summary>No runtime bundle zip was given or found where the app looks for one.</summary>
    public const string BundleNotFound = "RUNTIME_BUNDLE_NOT_FOUND";
    /// <summary>The picked runtime bundle could not be read or copied into the app.</summary>
    public const string BundleUnreadable = "RUNTIME_BUNDLE_UNREADABLE";
    /// <summary>guest/沙箱进程以 SIGKILL(137) 退出，可能触及 Android 12+ 32 幻影进程预算上限（N-08-03）。</summary>
    public const string PhantomProcessLimit = "PHANTOM_PROCESS_LIMIT";
    /// <summary>上一次进程被 Android 17 MemoryLimiter:AnonSwap 终止（N-08-04）。</summary>
    public const string MemoryLimited = "MEMORY_LIMITED";
}

public sealed record GuestExecutionOptions(
    string WorkingDirectory = RuntimeConstants.SandboxWorkspace,
    IReadOnlyDictionary<string, string>? Environment = null,
    TimeSpan? Timeout = null,
    int MaxOutputLines = 2000);

public sealed record GuestProcessResult(bool Ok, int? ExitCode, string Output, string? Error);

/// <summary>An interactive guest shell; PTY bytes never travel over a JSON control plane.</summary>
public abstract class GuestPtySession : IAsyncDisposable
{
    public abstract string Id { get; }

    public abstract Stream Stream { get; }

    public abstract Task ResizeAsync(int columns, int rows, CancellationToken ct);

    public abstract ValueTask DisposeAsync();
}

/// <summary>Plan section 5: commands inside the guest, always as argv.</summary>
public interface IGuestProcess
{
    Task<GuestProcessResult> ExecuteAsync(string executable, IReadOnlyList<string> arguments, GuestExecutionOptions options, CancellationToken ct);

    Task<GuestPtySession> OpenPtyAsync(GuestExecutionOptions options, CancellationToken ct);
}
