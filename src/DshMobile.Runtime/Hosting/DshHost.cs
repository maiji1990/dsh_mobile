using System.Diagnostics;
using DshMobile.Core.Diagnostics;
using DshMobile.Core.WebUi;
using DshMobile.Native;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Supervision;

namespace DshMobile.Runtime.Hosting;

public enum DshHostState
{
    Stopped,
    Starting,
    Ready,
    Stopping,
    Failed,
}

public sealed record DshHostResult(bool Ok, string Code, string? Message);

/// <summary>在线更新与换层时的独立健康探针结果（N-08-05 / ADR-0029）。</summary>
public sealed record DshHealthProbeResult(bool Ok, string Code, string? Message, string? DshVersion = null);

/// <summary>
/// Observable start/stop milestones of <see cref="DshHost"/> (ADR-0031): the UI shows the real stage instead of a
/// generic spinner, and never reports READY before <see cref="DshHostState.Ready"/>.
/// </summary>
public enum DshHostPhase
{
    Idle,
    SandboxStarting,
    SandboxReady,
    DshStarting,
    DshFirstOutput,
    DshTokenAvailable,
    ProtocolConnecting,
    Initializing,
    Ready,
    Stopping,
    Failed,
}

/// <summary>
/// Optional host launch and protocol-handshake configuration for Route B native transport (PR-01, ADR-0033).
/// </summary>
public sealed record DshHostOptions(
    bool TrimmedWeb = false,
    Func<IDshTokenSource, Action<DshHostPhase>, StartupTrace?, CancellationToken, Task<DshHostResult>>? ProtocolHandshakeAsync = null);

/// <summary>Read-only view of the launch token for the C# WebView host. The token never leaves the process.</summary>
public interface IDshTokenSource
{
    /// <summary>The current launch token, or null. Callers must not log or persist it.</summary>
    string? Token { get; }

    long? TokenRevision { get; }

    event EventHandler? TokenChanged;
}

/// <summary>
/// What <c>RuntimeCoordinator</c> needs from the C# runtime host (ADR-0015); <see cref="DshHost"/> is the only production
/// implementation, the seam exists so the orchestration can be tested without PRoot.
/// </summary>
public interface IDshHost : IAsyncDisposable
{
    DshHostState State { get; }

    /// <summary>
    /// True while anything this host started may still be alive (ADR-0015 decision 2): every state between start and
    /// stop, <see cref="DshHostState.Failed"/> after the health loop gave up (sandbox kept), and a stop that left marker
    /// processes behind. False after a failed start (the host killed what it started) and after a clean stop.
    /// </summary>
    bool OwnsProcesses { get; }

    /// <summary>Whether the <c>ui_*</c> layer was registered when DSH was last spawned (null: not spawned since start).</summary>
    bool? UiToolsAtStart { get; }

    event EventHandler? StateChanged;

    /// <param name="trace">Startup timing for the observability slice (docs/STARTUP_PERF.md); null records nothing.</param>
    Task<DshHostResult> StartAsync(CancellationToken ct, StartupTrace? trace = null);

    Task<DshHostResult> StopAsync(CancellationToken ct);

    /// <summary>执行自包含的新层健康探针（启动→就绪/401 检查→停机清理）并返回观测到的 DSH 版本（N-08-05）。</summary>
    Task<DshHealthProbeResult> ProbeHealthAsync(CancellationToken ct);
}

/// <summary>
/// The C# owner of the sandbox keepalive and the DSH web PRoot processes - no DSHBox code on this path.
/// Start: preconditions (PRoot only from nativeLibraryDir, base layer, guest bash, node layer), refuse if a legacy tree
/// is running, sandbox keepalive, sweep dead DSH writer locks, DSH web; READY is decided by
/// <see cref="DshHealthPolicy"/> on <see cref="HttpHealthProbe"/>; the launch token is parsed from DSH's raw stdout
/// and kept in memory only; every logged line is redacted. A background loop keeps applying the policy (bounded
/// in-place restart with the FULL DSH argv - the legacy restart call site drops the guest-cache bind, this does not).
/// Stop: kill the DSH tree, then the sandbox tree (children first), and verify nothing with either marker is left.
/// </summary>
public sealed class DshHost : IDshTokenSource, IDshHost
{
    public const string Conflict = "HOST_CONFLICT";
    public const string NotInstalled = "RUNTIME_NOT_INSTALLED";
    public const string NodeMissing = "NODE_LAYER_MISSING";
    public const string Exited = "DSH_EXITED";
    public const string NotReady = "DSH_NOT_READY";
    public const string Orphans = "HOST_ORPHANS";

    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartupFastProbeInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan StartupFastProbeWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SandboxReadyMaxWait = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan DefaultGracefulStopTimeout = TimeSpan.FromMilliseconds(1800);

    private readonly RuntimeLayout _layout;
    private readonly ProotCommandBuilder _builder;
    private readonly IGuestTerminator _terminator;
    private readonly TimeSpan _gracefulStopTimeout;
    private readonly DshHostOptions _options;
    private readonly Action<string> _log;
    private readonly TimeProvider _time;
    private readonly DshTokenRevisionTracker _tokens = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _op = new(1, 1);
    private Process? _sandbox;
    private Process? _dsh;
    private CancellationTokenSource? _loop;
    private Task? _loopTask;
    private DshWindowState _window;
    private StartupTrace? _trace;
    private double _dshSpawnMark;
    private int _dshFirstOutput;

    public DshHost(RuntimeLayout layout, Action<string> log, TimeProvider? time = null)
        : this(layout, log, time, null, null, null)
    {
    }

    /// <param name="terminator">
    /// SIGTERM sender for the graceful stop (ADR-0031); null uses <see cref="PosixGuestTerminator.Instance"/>, which is a
    /// no-op on Windows and on kernels that refuse the signal.
    /// </param>
    /// <param name="gracefulStopTimeout">How long to wait for the SIGTERM to take effect before SIGKILL; default 1.8 s.</param>
    public DshHost(RuntimeLayout layout, Action<string> log, TimeProvider? time, IGuestTerminator? terminator, TimeSpan? gracefulStopTimeout)
        : this(layout, log, time, terminator, gracefulStopTimeout, null)
    {
    }

    /// <param name="options">Optional Route B trimmed-web / protocol-handshake configuration (PR-01, ADR-0033).</param>
    public DshHost(RuntimeLayout layout, Action<string> log, TimeProvider? time, IGuestTerminator? terminator, TimeSpan? gracefulStopTimeout, DshHostOptions? options)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
        _builder = new ProotCommandBuilder(layout);
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _time = time ?? TimeProvider.System;
        _terminator = terminator ?? PosixGuestTerminator.Instance;
        _gracefulStopTimeout = gracefulStopTimeout ?? DefaultGracefulStopTimeout;
        _options = options ?? new DshHostOptions();
        _window = DshWindowState.Fresh(0);
    }

    public DshHostState State { get; private set; } = DshHostState.Stopped;

    /// <summary>The last observed milestone of a start or stop (ADR-0031); never READY before <see cref="State"/> is.</summary>
    public DshHostPhase Phase { get; private set; } = DshHostPhase.Idle;

    /// <summary>Raised on an arbitrary thread when <see cref="Phase"/> changes.</summary>
    public event EventHandler? PhaseChanged;

    public bool IsActive => State is DshHostState.Starting or DshHostState.Ready or DshHostState.Stopping;

    /// <inheritdoc/>
    public bool OwnsProcesses =>
        State is DshHostState.Starting or DshHostState.Ready or DshHostState.Stopping || _sandbox is not null || Volatile.Read(ref _leftAfterKill) > 0;

    /// <summary>Marker processes still alive after the last kill (a stop that left orphans keeps the host owning them).</summary>
    private int _leftAfterKill;

    /// <inheritdoc/>
    public bool? UiToolsAtStart { get; private set; }

    public string? Token
    {
        get
        {
            lock (_gate)
            {
                return _tokens.Token;
            }
        }
    }

    public long? TokenRevision
    {
        get
        {
            lock (_gate)
            {
                return _tokens.Revision;
            }
        }
    }

    public event EventHandler? TokenChanged;

    public event EventHandler? StateChanged;

    public async Task<DshHostResult> StartAsync(CancellationToken ct, StartupTrace? trace = null)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State is not (DshHostState.Stopped or DshHostState.Failed))
            {
                return new DshHostResult(false, Conflict, $"host is {State}");
            }
            if (trace.TraceSync("host.preconditions", Preconditions) is { } failed)
            {
                return failed;
            }
            if (trace.TraceSync("host.proc_scan", () => RuntimeProcessSnapshot.Observe(Environment.ProcessId)) is { } running
                && (running.SandboxRootPid is not null || running.DshRootPid is not null))
            {
                return new DshHostResult(false, Conflict, $"a legacy-started PRoot tree is running (sandbox {running.SandboxRootPid?.ToString() ?? "-"}, dsh {running.DshRootPid?.ToString() ?? "-"}); stop it first");
            }
            var reapSpan = trace?.Measure("host.orphan_reap");
            ReapOrphanProotLoaders();
            reapSpan?.End();
            SetState(DshHostState.Starting);
            SetPhase(DshHostPhase.SandboxStarting);

            var sandboxReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sandboxSpawn = trace?.Measure("sandbox.spawn");
            try
            {
                _sandbox = Spawn(
                    _builder.SandboxCommand(),
                    _builder.Environment("sandbox"),
                    "sandbox",
                    line =>
                    {
                        if (line.Contains(RuntimeConstants.SandboxKeepaliveMarker, StringComparison.Ordinal))
                        {
                            sandboxReady.TrySetResult(true);
                        }
                    });
            }
            catch
            {
                sandboxSpawn?.End(StartupTrace.Failed);
                throw;
            }
            sandboxSpawn?.End(details: new Dictionary<string, object?> { ["pid"] = _sandbox.Id });
            await trace.TraceAsync("sandbox.fixed_delay", () => WaitSandboxReadyAsync(_sandbox, sandboxReady.Task, SandboxReadyMaxWait, ct)).ConfigureAwait(false);
            SetPhase(DshHostPhase.SandboxReady);
            if (_sandbox.HasExited)
            {
                var code = _sandbox.ExitCode;
                await KillEverythingAsync().ConfigureAwait(false);
                SetState(DshHostState.Failed);
                SetPhase(DshHostPhase.Failed);
                if (code == 137)
                {
                    return new DshHostResult(false, RuntimeErrorCodes.PhantomProcessLimit, "sandbox PRoot exited with 137 (SIGKILL / possible Android 32 phantom process limit)");
                }
                return new DshHostResult(false, Exited, $"sandbox PRoot exited immediately (exit {code})");
            }

            var cleanup = trace?.Measure("host.cleanup_processes");
            KillTreesByMarker(RuntimeConstants.DshStartScript);
            cleanup?.End();
            var sweepSpan = trace?.Measure("host.lock_sweep");
            var sweep = DshWriterLockSweeper.SweepOnThisDevice(_layout.UserDataDir);
            sweepSpan?.End(details: new Dictionary<string, object?> { ["scanned"] = sweep.Scanned, ["removed"] = sweep.Removed.Count });
            if (sweep.Removed.Count > 0)
            {
                _log($"host: removed {sweep.Removed.Count}/{sweep.Scanned} stale DSH writer lock(s)");
            }
            using var startupWake = new SemaphoreSlim(0, 1);
            SetPhase(DshHostPhase.DshStarting);
            _dsh = SpawnDsh(trace, () => SignalStartupWake(startupWake));

            var ready = await WaitReadyAsync(ct, trace, startupWake).ConfigureAwait(false);
            if (!ready.Ok)
            {
                await KillEverythingAsync().ConfigureAwait(false);
                SetState(DshHostState.Failed);
                SetPhase(DshHostPhase.Failed);
                return ready;
            }
            if (_options.ProtocolHandshakeAsync is { } handshakeAsync)
            {
                SetPhase(DshHostPhase.ProtocolConnecting);
                var handshake = await handshakeAsync(this, SetPhase, trace, ct).ConfigureAwait(false);
                if (!handshake.Ok)
                {
                    await KillEverythingAsync().ConfigureAwait(false);
                    SetState(DshHostState.Failed);
                    SetPhase(DshHostPhase.Failed);
                    return handshake;
                }
            }
            SetState(DshHostState.Ready);
            SetPhase(DshHostPhase.Ready);
            _loop = new CancellationTokenSource();
            _loopTask = Task.Run(() => HealthLoopAsync(_loop.Token));
            return ready;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            await KillEverythingAsync().ConfigureAwait(false);
            SetState(DshHostState.Failed);
            SetPhase(DshHostPhase.Failed);
            return new DshHostResult(false, ProotGuestProcess.SpawnFailed, e.Message);
        }
        finally
        {
            _op.Release();
        }
    }

    public async Task<DshHostResult> StopAsync(CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State == DshHostState.Stopped && Volatile.Read(ref _leftAfterKill) == 0)
            {
                return new DshHostResult(true, "OK", "not running");
            }
            SetState(DshHostState.Stopping);
            SetPhase(DshHostPhase.Stopping);
            await StopDshGracefullyAsync().ConfigureAwait(false);
            var left = await KillEverythingAsync().ConfigureAwait(false);
            ReapOrphanProotLoaders();
            UiToolsAtStart = null;
            SetState(DshHostState.Stopped);
            SetPhase(DshHostPhase.Idle);
            return left == 0
                ? new DshHostResult(true, "OK", null)
                : new DshHostResult(false, Orphans, $"{left} process(es) with a runtime marker still alive after stop");
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>
    /// 在线更新与换层独立健康探针（N-08-05 / ADR-0029）：
    /// 启动沙箱与 <c>dsh web</c> 并等待 HTTP 健康检查（端口 + 401/就绪）通过，读取磁盘 DSH 版本后在 <c>finally</c> 中安全停机清理；
    /// 不建立 Route B 长连接、不改写业务状态。
    /// </summary>
    public async Task<DshHealthProbeResult> ProbeHealthAsync(CancellationToken ct)
    {
        await _op.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State is not (DshHostState.Stopped or DshHostState.Failed))
            {
                return new DshHealthProbeResult(false, Conflict, $"host is {State}");
            }
            if (Preconditions() is { } failed)
            {
                return new DshHealthProbeResult(false, failed.Code, failed.Message);
            }
            if (RuntimeProcessSnapshot.Observe(Environment.ProcessId) is { } running
                && (running.SandboxRootPid is not null || running.DshRootPid is not null))
            {
                return new DshHealthProbeResult(false, Conflict, $"a legacy-started PRoot tree is running (sandbox {running.SandboxRootPid?.ToString() ?? "-"}, dsh {running.DshRootPid?.ToString() ?? "-"}); stop it first");
            }
            ReapOrphanProotLoaders();
            SetState(DshHostState.Starting);
            SetPhase(DshHostPhase.SandboxStarting);

            DshHostResult probeOutcome;
            try
            {
                var sandboxReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _sandbox = Spawn(
                    _builder.SandboxCommand(),
                    _builder.Environment("sandbox"),
                    "sandbox",
                    line =>
                    {
                        if (line.Contains(RuntimeConstants.SandboxKeepaliveMarker, StringComparison.Ordinal))
                        {
                            sandboxReady.TrySetResult(true);
                        }
                    });
                await WaitSandboxReadyAsync(_sandbox, sandboxReady.Task, SandboxReadyMaxWait, ct).ConfigureAwait(false);
                SetPhase(DshHostPhase.SandboxReady);
                if (_sandbox.HasExited)
                {
                    var code = _sandbox.ExitCode;
                    probeOutcome = code == 137
                        ? new DshHostResult(false, RuntimeErrorCodes.PhantomProcessLimit, "sandbox PRoot exited with 137 (SIGKILL / possible Android 32 phantom process limit)")
                        : new DshHostResult(false, Exited, $"sandbox PRoot exited immediately (exit {code})");
                }
                else
                {
                    KillTreesByMarker(RuntimeConstants.DshStartScript);
                    DshWriterLockSweeper.SweepOnThisDevice(_layout.UserDataDir);
                    using var startupWake = new SemaphoreSlim(0, 1);
                    SetPhase(DshHostPhase.DshStarting);
                    _dsh = SpawnDsh(null, () => SignalStartupWake(startupWake));
                    probeOutcome = await WaitReadyAsync(ct, null, startupWake).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                probeOutcome = new DshHostResult(false, ProotGuestProcess.SpawnFailed, e.Message);
            }

            var detectedVersion = probeOutcome.Ok
                ? DshMobile.Runtime.Updates.InstalledVersionsReader.Read(_layout, "0.0.0").Dsh
                : null;

            SetState(DshHostState.Stopping);
            SetPhase(DshHostPhase.Stopping);
            await StopDshGracefullyAsync().ConfigureAwait(false);
            var left = await KillEverythingAsync().ConfigureAwait(false);
            ReapOrphanProotLoaders();
            UiToolsAtStart = null;
            SetState(left == 0 ? DshHostState.Stopped : DshHostState.Failed);
            SetPhase(left == 0 ? DshHostPhase.Idle : DshHostPhase.Failed);

            if (!probeOutcome.Ok)
            {
                return new DshHealthProbeResult(false, probeOutcome.Code, probeOutcome.Message, detectedVersion);
            }
            if (left > 0)
            {
                return new DshHealthProbeResult(false, Orphans, $"{left} process(es) with a runtime marker still alive after probe stop", detectedVersion);
            }
            return new DshHealthProbeResult(true, "OK", "ready", detectedVersion);
        }
        finally
        {
            _op.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsActive)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        _op.Dispose();
    }

    // ------------------------------------------------------------------------------------------------

    private DshHostResult? Preconditions()
    {
        var proot = _builder.ProotBinary;
        if (!new NativeExecutablePolicy(_layout.NativeLibraryDir).IsExecutableLocation(proot) || !File.Exists(proot))
        {
            return new DshHostResult(false, NativeErrorCodes.ExecutableNotAllowed, $"PRoot must come from nativeLibraryDir: {proot}");
        }
        if (!Directory.Exists(_layout.LayerDir("base")) || !File.Exists(Path.Combine(_layout.LayerDir("base"), "usr", "bin", "bash")))
        {
            return new DshHostResult(false, NotInstalled, "base layer / guest bash missing: import a runtime bundle");
        }
        if (!File.Exists(Path.Combine(_layout.LayerDir("node"), "bin", "node")))
        {
            return new DshHostResult(false, NodeMissing, "node layer not installed");
        }
        return null;
    }

    /// <summary>计算启动等待阶段的健康探针轮询间隔：前 20 秒内高频探测以缩短就绪感知延迟，之后回退到 1 秒。</summary>
    internal static TimeSpan StartupProbeInterval(long elapsedMs) =>
        elapsedMs < (long)StartupFastProbeWindow.TotalMilliseconds ? StartupFastProbeInterval : ProbeInterval;

    /// <summary>等待沙箱保活进程输出就绪标记或提前退出，最长不超过 <paramref name="maxWait"/>。</summary>
    internal static async Task WaitSandboxReadyAsync(Process sandbox, Task<bool> markerSeen, TimeSpan maxWait, CancellationToken ct)
    {
        if (sandbox.HasExited || markerSeen.IsCompleted)
        {
            return;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = Task.Delay(maxWait, linked.Token);
        var exitTask = sandbox.WaitForExitAsync(linked.Token);
        await Task.WhenAny(markerSeen, exitTask, delayTask).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    private static void SignalStartupWake(SemaphoreSlim wake)
    {
        try
        {
            if (wake.CurrentCount == 0)
            {
                wake.Release();
            }
        }
        catch (Exception e) when (e is SemaphoreFullException or ObjectDisposedException)
        {
        }
    }

    private Process SpawnDshInPlaceRestart()
    {
        _window = DshWindowState.Fresh(NowMs());
        UiToolsAtStart = DshMobile.Runtime.Automation.UiToolsRegistration.IsRegistered(_layout.UserDataDir);
        var argv = _builder.DshCommand(inPlaceRestart: true, trimmedWeb: _options.TrimmedWeb);
        var env = _builder.Environment("dsh");
        return SpawnDshProcess(argv, env, trace: null, onHintReady: null);
    }

    private Process SpawnDsh(StartupTrace? trace = null, Action? onHintReady = null)
    {
        _window = DshWindowState.Fresh(NowMs());
        // DSH reads its --patch layers once at start: this is what "loaded by DSH" means for the ui_* tools.
        UiToolsAtStart = DshMobile.Runtime.Automation.UiToolsRegistration.IsRegistered(_layout.UserDataDir);
        var commandEnv = trace?.Measure("dsh.command_env");
        var argv = _builder.DshCommand(inPlaceRestart: false, trimmedWeb: _options.TrimmedWeb);
        var env = _builder.Environment("dsh");
        commandEnv?.End();
        return SpawnDshProcess(argv, env, trace, onHintReady);
    }

    private Process SpawnDshProcess(IReadOnlyList<string> argv, IReadOnlyDictionary<string, string> env, StartupTrace? trace, Action? onHintReady)
    {
        var spawn = trace?.Measure("dsh.spawn");
        _trace = trace;
        _dshSpawnMark = trace?.Mark() ?? 0d;
        Volatile.Write(ref _dshFirstOutput, 0);
        Process process;
        try
        {
            process = Spawn(argv, env, "dsh", line =>
            {
                if (DshLaunchToken.TryParse(line) is not { } token)
                {
                    return;
                }
                if (_trace is { Enabled: true } t)
                {
                    t.EmitElapsed("dsh.token_available", t.Mark() - _dshSpawnMark);
                }
                bool changed;
                lock (_gate)
                {
                    changed = _tokens.Update(token);
                }
                SetPhase(DshHostPhase.DshTokenAvailable);
                if (changed)
                {
                    _log("host: dsh web launch token refreshed");
                    TokenChanged?.Invoke(this, EventArgs.Empty);
                }
                // 解析到启动令牌时服务端通常已接近监听，立即唤醒健康探针避免白等轮询间隔。
                onHintReady?.Invoke();
            }, onHintReady);
        }
        catch
        {
            spawn?.End(StartupTrace.Failed);
            throw;
        }
        spawn?.End(details: new Dictionary<string, object?> { ["pid"] = process.Id });
        return process;
    }

    private async Task<DshHostResult> WaitReadyAsync(CancellationToken ct, StartupTrace? trace = null, SemaphoreSlim? startupWake = null)
    {
        long last = NowMs();
        var probes = 0;
        var sawPort = false;
        var sawResponse = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var health = await HttpHealthProbe.CheckAsync(ct: ct).ConfigureAwait(false);
            probes++;
            if (trace is { Enabled: true })
            {
                if (!sawPort && health.PortOpen)
                {
                    sawPort = true;
                    trace.EmitSpan("http.first_port_open", _dshSpawnMark, details: new Dictionary<string, object?> { ["attempt"] = probes });
                }
                if (!sawResponse && health.HttpStatus is { } firstStatus)
                {
                    sawResponse = true;
                    trace.EmitSpan("http.first_response", _dshSpawnMark, details: new Dictionary<string, object?> { ["status"] = firstStatus, ["attempt"] = probes });
                }
            }
            var now = NowMs();
            var alive = _dsh is { HasExited: false };
            var d = DshHealthPolicy.Decide(_window, new DshHealthProbe(now, health.WebUiReady, alive, true, now - last),
                (long)RuntimeConstants.DshReadyTimeout.TotalMilliseconds, (long)RuntimeConstants.DshUnresponsiveGrace.TotalMilliseconds, RuntimeConstants.MaxAutoRestartAttempts);
            _window = d.Next;
            last = now;
            switch (d.Action)
            {
                case DshHealthAction.Ready:
                    trace?.EmitSpan("host.ready", _dshSpawnMark, details: new Dictionary<string, object?> { ["probes"] = probes });
                    _log($"host: dsh ready (HTTP {health.HttpStatus}) in {now - (_window.StartedAtMs)} ms");
                    return new DshHostResult(true, "OK", null);
                case DshHealthAction.InitialTimeout:
                    trace?.EmitSpan("host.ready", _dshSpawnMark, StartupTrace.Failed, new Dictionary<string, object?> { ["probes"] = probes, ["code"] = NotReady });
                    return new DshHostResult(false, NotReady, $"DSH not ready within {RuntimeConstants.DshReadyTimeout.TotalSeconds:F0} s");
                case DshHealthAction.Restart or DshHealthAction.Exhausted when !alive:
                    var exitCode = _dsh?.ExitCode;
                    if (exitCode == 137)
                    {
                        trace?.EmitSpan("host.ready", _dshSpawnMark, StartupTrace.Failed, new Dictionary<string, object?> { ["probes"] = probes, ["code"] = RuntimeErrorCodes.PhantomProcessLimit });
                        return new DshHostResult(false, RuntimeErrorCodes.PhantomProcessLimit, "DSH exited during start with 137 (SIGKILL / possible Android 32 phantom process limit)");
                    }
                    trace?.EmitSpan("host.ready", _dshSpawnMark, StartupTrace.Failed, new Dictionary<string, object?> { ["probes"] = probes, ["code"] = Exited });
                    return new DshHostResult(false, Exited, $"DSH exited during start (exit {exitCode})");
            }
            var interval = StartupProbeInterval(now - _window.StartedAtMs);
            if (startupWake is not null)
            {
                await startupWake.WaitAsync(interval, ct).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HealthLoopAsync(CancellationToken ct)
    {
        long last = NowMs();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(LoopInterval, ct).ConfigureAwait(false);
                var health = await HttpHealthProbe.CheckAsync(ct: ct).ConfigureAwait(false);
                var now = NowMs();
                var alive = _dsh is { HasExited: false };
                var d = DshHealthPolicy.Decide(_window, new DshHealthProbe(now, health.WebUiReady, alive, true, now - last),
                    (long)RuntimeConstants.DshReadyTimeout.TotalMilliseconds, (long)RuntimeConstants.DshUnresponsiveGrace.TotalMilliseconds, RuntimeConstants.MaxAutoRestartAttempts);
                _window = d.Next;
                last = now;
                if (d.Action == DshHealthAction.Restart)
                {
                    _log($"host: dsh unhealthy (alive={alive}), restart {d.Next.RestartAttempts}/{RuntimeConstants.MaxAutoRestartAttempts}");
                    KillProcessTree(_dsh);
                    KillTreesByMarker(RuntimeConstants.DshStartScript);
                    var keep = _window;
                    _dsh = SpawnDshInPlaceRestart();
                    _window = keep; // the policy already re-armed the window for the new instance
                }
                else if (d.Action is DshHealthAction.Exhausted or DshHealthAction.InitialTimeout)
                {
                    if (_dsh is { HasExited: true, ExitCode: 137 })
                    {
                        _log($"host: dsh exited 137 ({RuntimeErrorCodes.PhantomProcessLimit}); stopping sandbox-kept failure state");
                    }
                    else
                    {
                        _log($"host: dsh {d.Action}; stopping DSH (sandbox kept)");
                    }
                    KillProcessTree(_dsh);
                    KillTreesByMarker(RuntimeConstants.DshStartScript);
                    SetState(DshHostState.Failed);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// ADR-0031: give DSH up to <see cref="_gracefulStopTimeout"/> to leave on SIGTERM (Node flushes its compile cache and
    /// removes the writer lock) before <see cref="KillEverythingAsync"/> escalates to SIGKILL.
    /// </summary>
    private async Task StopDshGracefullyAsync()
    {
        var dsh = _dsh;
        if (dsh is null)
        {
            return;
        }
        try
        {
            await GracefulStop.RunAsync(dsh.Id, _terminator, () => dsh.HasExited, _gracefulStopTimeout, _log).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _log($"host: graceful stop skipped ({e.GetType().Name}); falling back to SIGKILL");
        }
    }

    /// <summary>Kills DSH, then the sandbox, children first; returns how many marker processes remain.</summary>
    private async Task<int> KillEverythingAsync()
    {
        if (_loop is { } loop)
        {
            await loop.CancelAsync().ConfigureAwait(false);
            if (_loopTask is { } t)
            {
                await t.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            loop.Dispose();
            _loop = null;
            _loopTask = null;
        }
        KillProcessTree(_dsh);
        KillTreesByMarker(RuntimeConstants.DshStartScript);
        KillProcessTree(_sandbox);
        KillTreesByMarker(RuntimeConstants.SandboxKeepaliveMarker);
        _dsh?.Dispose();
        _sandbox?.Dispose();
        _dsh = _sandbox = null;
        bool hadToken;
        lock (_gate)
        {
            hadToken = _tokens.Token is not null;
            _tokens.ClearForRelease();
        }
        if (hadToken)
        {
            TokenChanged?.Invoke(this, EventArgs.Empty);
        }
        for (var i = 0; i < 20; i++)
        {
            var left = RemainingMarkerProcesses();
            if (left == 0)
            {
                Volatile.Write(ref _leftAfterKill, 0);
                return 0;
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
        var remaining = RemainingMarkerProcesses();
        Volatile.Write(ref _leftAfterKill, remaining);
        return remaining;
    }

    private void ReapOrphanProotLoaders()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var report = NativeOrphanReaper.Reap("/proc", Environment.ProcessId);
        if (report.KilledPids.Count > 0 || report.SkippedPids.Count > 0)
        {
            _log($"host: orphan reaper killed {report.KilledPids.Count} skipped {report.SkippedPids.Count}");
        }
    }

    private static int RemainingMarkerProcesses()
    {
        var s = RuntimeProcessSnapshot.Observe(Environment.ProcessId);
        return s is null ? 0 : (s.SandboxRootPid is null ? 0 : s.SandboxTreeSize) + (s.DshRootPid is null ? 0 : s.DshTreeSize);
    }

    private void KillTreesByMarker(string marker)
    {
        if (ProcessTable.Read() is not { } table)
        {
            return;
        }
        // Legacy killAll's real order is parents first (see ProcessTable.KillAllOrder); the host kills children first.
        var order = table.KillAllOrder(marker).Reverse().ToList();
        foreach (var pid in order)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
        }
        if (order.Count > 0)
        {
            _log($"host: killed {order.Count} process(es) matching {(marker == RuntimeConstants.DshStartScript ? "dsh" : "sandbox")} marker");
        }
    }

    private static void KillProcessTree(Process? p)
    {
        if (p is null)
        {
            return;
        }
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
        {
        }
    }

    private Process Spawn(IReadOnlyList<string> argv, IReadOnlyDictionary<string, string> env, string tag, Action<string>? onRawLine, Action? onFirstOutput = null)
    {
        var psi = new ProcessStartInfo(argv[0])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in argv.Skip(1))
        {
            psi.ArgumentList.Add(a);
        }
        foreach (var (k, v) in env)
        {
            psi.Environment[k] = v;
        }
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {tag} PRoot");
        p.StandardInput.Close(); // non-interactive, like legacy
        _ = PumpAsync(p.StandardOutput, tag, "stdout", onRawLine, onFirstOutput);
        _ = PumpAsync(p.StandardError, tag, "stderr", onRawLine, onFirstOutput);
        _log($"host: started {tag} PRoot pid {p.Id}");
        return p;
    }

    private async Task PumpAsync(StreamReader reader, string tag, string stream, Action<string>? onRawLine, Action? onFirstOutput = null)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (tag == "dsh" && Interlocked.Exchange(ref _dshFirstOutput, 1) == 0)
                {
                    if (_trace is { Enabled: true } t)
                    {
                        t.EmitElapsed("dsh.first_output", t.Mark() - _dshSpawnMark, details: new Dictionary<string, object?> { ["stream"] = stream });
                    }
                    SetPhase(DshHostPhase.DshFirstOutput);
                    onFirstOutput?.Invoke();
                }
                onRawLine?.Invoke(line);           // raw: token parsing only, never logged
                _log($"proc-{tag}: {LogRedactor.Redact(line)}");
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private void SetState(DshHostState s)
    {
        State = s;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>ADR-0031: record a start/stop milestone for the UI. READY is only ever set after <see cref="State"/> is.</summary>
    private void SetPhase(DshHostPhase phase)
    {
        if (Phase == phase)
        {
            return;
        }
        Phase = phase;
        PhaseChanged?.Invoke(this, EventArgs.Empty);
    }

    private long NowMs() => (long)_time.GetElapsedTime(0).TotalMilliseconds;
}
