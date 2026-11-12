using System.Diagnostics;
using DshMobile.Native;

namespace DshMobile.Runtime.Guest;

/// <summary>Fixed, read-only guest diagnostics (same commands and order as the legacy <c>RuntimeController.DIAGNOSTICS</c>).</summary>
public static class GuestDiagnostics
{
    public static IReadOnlyList<(string Key, string Command)> Commands { get; } =
    [
        ("arch", "uname -m"),
        ("debian", "cat /etc/debian_version"),
        ("node", "node --version"),
        ("dsh", "node " + RuntimeConstants.GuestDshBin + " --version"),
    ];

    /// <summary>Legacy keeps the first 20 output lines of each diagnostic.</summary>
    public const int MaxLines = 20;
}

/// <summary>
/// <see cref="IGuestProcess"/> that starts PRoot directly from C# (.NET <see cref="Process"/>; no DSHBox code): the
/// legacy argv and environment from <see cref="ProotCommandBuilder"/>, the PRoot binary only from nativeLibraryDir
/// (W^X), stdin closed at once (non-interactive, like legacy), stdout and stderr merged line by line, a bounded wait
/// that kills the whole process tree, and the real exit code - a non-zero exit is never reported as success.
/// </summary>
public sealed class ProotGuestProcess(RuntimeLayout layout) : IGuestProcess
{
    public const string SpawnFailed = "GUEST_SPAWN_FAILED";
    public const string Timeout = "GUEST_TIMEOUT";

    /// <summary>Legacy GUEST_COMMAND_TIMEOUT_MS.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly ProotCommandBuilder _builder = new(layout);
    private readonly NativeExecutablePolicy _policy = new(layout.NativeLibraryDir);

    public ProotCommandBuilder Builder => _builder;

    /// <summary>argv form: every token is quoted into the single guest <c>bash -c</c> line.</summary>
    public Task<GuestProcessResult> ExecuteAsync(string executable, IReadOnlyList<string> arguments, GuestExecutionOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        return RunShellAsync(GuestCommandLine.Join([executable, .. arguments]), options, ct);
    }

    /// <summary>A fixed, trusted guest shell line (diagnostics). Never pass caller input here; use <see cref="ExecuteAsync"/>.</summary>
    public async Task<GuestProcessResult> RunShellAsync(string command, GuestExecutionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var argv = _builder.GuestCommand(command);
        if (!_policy.IsExecutableLocation(argv[0]))
        {
            return new GuestProcessResult(false, null, "", $"{NativeErrorCodes.ExecutableNotAllowed}: {argv[0]} is not in nativeLibraryDir");
        }
        var env = new Dictionary<string, string>(_builder.Environment("guest"), StringComparer.Ordinal);
        if (options.Environment is { } extra)
        {
            foreach (var (k, v) in extra)
            {
                env[k] = v;
            }
        }
        return await RunProcessAsync(argv, env, options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Spawns <paramref name="argv"/> with <paramref name="env"/> added to the inherited environment (like Java's
    /// ProcessBuilder), closes stdin, merges stdout/stderr lines (last <see cref="GuestExecutionOptions.MaxOutputLines"/>
    /// kept), waits up to the timeout and then kills the whole tree. No policy check: callers do that.
    /// </summary>
    internal static async Task<GuestProcessResult> RunProcessAsync(IReadOnlyList<string> argv, IReadOnlyDictionary<string, string> env, GuestExecutionOptions options, CancellationToken ct)
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

        var lines = new List<string>();
        var gate = new Lock();
        void OnLine(string? line)
        {
            if (line is null)
            {
                return;
            }
            lock (gate)
            {
                lines.Add(line);
                if (lines.Count > options.MaxOutputLines)
                {
                    lines.RemoveAt(0);
                }
            }
        }

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new GuestProcessResult(false, null, "", $"{SpawnFailed}: {e.Message}");
        }
        using (process)
        {
            process.StandardInput.Close();
            var stdout = PumpAsync(process.StandardOutput, OnLine);
            var stderr = PumpAsync(process.StandardError, OnLine);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Timeout ?? DefaultTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillTree(process);
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                ct.ThrowIfCancellationRequested();
                return new GuestProcessResult(false, -1, Snapshot(), $"{Timeout}: guest command exceeded {(options.Timeout ?? DefaultTimeout).TotalSeconds:F0} s");
            }
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            var code = process.ExitCode;
            return new GuestProcessResult(code == 0, code, Snapshot(), code == 0 ? null : $"GUEST_CMD_FAILED: guest command exit={code}");
        }

        string Snapshot()
        {
            lock (gate)
            {
                return string.Join('\n', lines);
            }
        }
    }

    /// <summary>Runs <see cref="GuestDiagnostics.Commands"/> one after another (first 20 lines each, like legacy).</summary>
    public async Task<IReadOnlyList<(string Key, GuestProcessResult Result)>> RunDiagnosticsAsync(CancellationToken ct)
    {
        var results = new List<(string, GuestProcessResult)>();
        foreach (var (key, command) in GuestDiagnostics.Commands)
        {
            var r = await RunShellAsync(command, new GuestExecutionOptions(MaxOutputLines: GuestDiagnostics.MaxLines, Timeout: TimeSpan.FromMinutes(2)), ct).ConfigureAwait(false);
            var first = string.Join('\n', r.Output.Split('\n').Take(GuestDiagnostics.MaxLines));
            results.Add((key, r with { Output = first }));
        }
        return results;
    }

    public async Task<GuestPtySession> OpenPtyAsync(GuestExecutionOptions options, CancellationToken ct) =>
        await OpenPtyAsync(options, rows: 24, columns: 80, ct).ConfigureAwait(false);

    /// <summary>
    /// PR-06: the guest login shell (<see cref="TerminalCommandBuilder"/>, same argv as the legacy terminal) on a PTY
    /// spawned by libdshmobile_native. The guest starts in <c>/root</c> like the legacy terminal
    /// (<see cref="GuestExecutionOptions.WorkingDirectory"/> does not apply); <see cref="GuestExecutionOptions.Environment"/>
    /// entries replace or extend the terminal environment. Failures are <see cref="GuestPtyException"/> with a stable code.
    /// </summary>
    public Task<ProotPtySession> OpenPtyAsync(GuestExecutionOptions options, int rows, int columns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        var terminal = new TerminalCommandBuilder(layout);
        var argv = terminal.LoginShell()
            ?? throw new GuestPtyException(RuntimeErrorCodes.NotInstalled, "no runtime rootfs under " + layout.RuntimeCurrentDir);
        if (!_policy.IsExecutableLocation(argv[0]))
        {
            throw new GuestPtyException(NativeErrorCodes.ExecutableNotAllowed, argv[0] + " is not in nativeLibraryDir");
        }
        var env = terminal.Environment().ToList();
        if (options.Environment is { } extra)
        {
            foreach (var (k, v) in extra)
            {
                env.RemoveAll(e => e.StartsWith(k + "=", StringComparison.Ordinal));
                env.Add($"{k}={v}");
            }
        }
        // Host cwd "/" like the legacy TerminalSession; the guest cwd comes from --cwd.
        var spawned = NativePty.Spawn(argv[0], argv, env, "/", rows, columns);
        if (!spawned.Ok)
        {
            throw new GuestPtyException(spawned.Code, spawned.Message ?? spawned.Code);
        }
        return Task.FromResult(new ProotPtySession(spawned.Value!, "csharp-pty-" + spawned.Value!.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), argv));
    }

    private static async Task PumpAsync(StreamReader reader, Action<string?> onLine)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            onLine(line);
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // already exited / not permitted; PRoot --kill-on-exit takes the guest tree down with it
        }
    }
}
