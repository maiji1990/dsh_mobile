using DshMobile.Core.Diagnostics;
using DshMobile.Runtime.Hosting;

namespace DshMobile.Application.Tests;

/// <summary>
/// An <see cref="IDshHost"/> that records its calls into a shared list and follows the real host's ownership rule:
/// alive between start and stop, after the health loop gave up (sandbox kept), or after a stop that left orphans.
/// </summary>
internal sealed class FakeDshHost(List<string> calls) : IDshHost
{
    private bool _kept;
    private bool _orphans;

    public DshHostState State { get; private set; }

    public bool? UiToolsAtStart { get; private set; }

    public bool OwnsProcesses => State is DshHostState.Starting or DshHostState.Ready or DshHostState.Stopping || _kept || _orphans;

    public Func<DshHostResult>? OnStart { get; set; }

    public Func<DshHostResult>? OnStop { get; set; }

    public event EventHandler? StateChanged;

    public Task<DshHostResult> StartAsync(CancellationToken ct, StartupTrace? trace = null)
    {
        calls.Add("host.start");
        var r = OnStart?.Invoke() ?? new DshHostResult(true, "OK", null);
        UiToolsAtStart = r.Ok ? true : null;
        Set(r.Ok ? DshHostState.Ready : DshHostState.Failed);
        return Task.FromResult(r);
    }

    public Task<DshHostResult> StopAsync(CancellationToken ct)
    {
        calls.Add("host.stop");
        var r = OnStop?.Invoke() ?? new DshHostResult(true, "OK", null);
        _kept = false;
        _orphans = !r.Ok;
        UiToolsAtStart = null;
        Set(DshHostState.Stopped);
        return Task.FromResult(r);
    }

    /// <summary>What the health loop does when it gives up: DSH stopped, sandbox kept.</summary>
    public void GiveUp()
    {
        _kept = true;
        Set(DshHostState.Failed);
    }

    private void Set(DshHostState s)
    {
        State = s;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class TestRuntime
{
    /// <summary>The smallest tree the C# install check accepts (PRoot, base, a profile whose layer sentinel matches).</summary>
    public static void Install(string files, string version = "1", string sha = "aa", string? body = null)
    {
        var rc = body ?? Path.Combine(files, "runtime", "runtime-current");
        Directory.CreateDirectory(Path.Combine(rc, "base", ".dshmobile"));
        File.WriteAllText(Path.Combine(rc, "base", ".dshmobile", "layer-base.sha256"), sha);
        Directory.CreateDirectory(Path.Combine(rc, "android-side", "bin"));
        File.WriteAllText(Path.Combine(rc, "android-side", "bin", "proot"), "elf");
        File.WriteAllText(Path.Combine(rc, "runtime-profile.json"),
            $$"""{"bundle":{"version":"{{version}}"},"layers":[{"name":"base","sha256":"{{sha}}"}],"assembly":["base"]}""");
    }

    public static string RepoPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }
        return Path.Combine([dir!.FullName, .. parts]);
    }
}
