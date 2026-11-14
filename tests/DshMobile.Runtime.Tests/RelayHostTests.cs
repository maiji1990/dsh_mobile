// Ports of DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/test/java/interlock/relay/core/runtime/RelayRuntimePhaseTest.kt
// plus C#-only lifecycle tests of RelayHost (the container's assembly root) driven through a real mailbox.

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.A11y;
using DshMobile.Runtime.Automation.Relay.Exec.Direct;
using DshMobile.Runtime.Automation.Relay.Exec.Shizuku;
using DshMobile.Runtime.Automation.Relay.Transport;

namespace DshMobile.Runtime.Tests;

public sealed class RelayRuntimePhaseTests
{
    [Fact]
    public void StoppedThroughStartingToRunning()
    {
        var machine = new RelayRuntimeStateMachine();
        Assert.True(machine.BeginStart());
        Assert.Equal(RelayRuntimePhase.Starting, machine.Phase);
        machine.CommitRunning();
        Assert.Equal(RelayRuntimePhase.Running, machine.Phase);
        Assert.Null(machine.DegradedReason);
    }

    [Fact]
    public void FailureWhileStartingDegradesWithReason()
    {
        var machine = new RelayRuntimeStateMachine();
        Assert.True(machine.BeginStart());
        Assert.True(machine.Fail("mailbox IllegalStateException: x"));
        Assert.Equal(RelayRuntimePhase.Degraded, machine.Phase);
        Assert.Equal("mailbox IllegalStateException: x", machine.DegradedReason);
    }

    [Fact]
    public void DegradedAllowsRetry()
    {
        var machine = new RelayRuntimeStateMachine();
        machine.BeginStart();
        machine.Fail("ledger IOException: slow disk");
        Assert.True(machine.BeginStart());
        Assert.Equal(RelayRuntimePhase.Starting, machine.Phase);
        machine.CommitRunning();
        Assert.Equal(RelayRuntimePhase.Running, machine.Phase);
        Assert.Null(machine.DegradedReason);
    }

    [Fact]
    public void RepeatedStartWhileRunningIsRefused()
    {
        var machine = new RelayRuntimeStateMachine();
        machine.BeginStart();
        machine.CommitRunning();
        Assert.False(machine.BeginStart());
        Assert.Equal(RelayRuntimePhase.Running, machine.Phase);
    }

    [Fact]
    public void StopWhileStoppedIsRefused()
    {
        var machine = new RelayRuntimeStateMachine();
        Assert.False(machine.BeginStop());
        Assert.Equal(RelayRuntimePhase.Stopped, machine.Phase);
    }

    [Fact]
    public void StartingAndRunningBothStop()
    {
        var starting = new RelayRuntimeStateMachine();
        Assert.True(starting.BeginStart());
        Assert.True(starting.BeginStop());
        Assert.Equal(RelayRuntimePhase.Stopped, starting.Phase);
        var running = new RelayRuntimeStateMachine();
        running.BeginStart();
        running.CommitRunning();
        Assert.True(running.BeginStop());
        Assert.Equal(RelayRuntimePhase.Stopped, running.Phase);
    }

    [Fact]
    public void FailOutsideStartingIsRefused()
    {
        var machine = new RelayRuntimeStateMachine();
        Assert.False(machine.Fail("nowhere"));
        machine.BeginStart();
        machine.CommitRunning();
        Assert.False(machine.Fail("too late"));
        Assert.Equal(RelayRuntimePhase.Running, machine.Phase);
    }

    [Fact]
    public void LateCommitRunningIsNoOp()
    {
        var machine = new RelayRuntimeStateMachine();
        machine.BeginStart();
        Assert.True(machine.BeginStop());
        machine.CommitRunning();
        Assert.Equal(RelayRuntimePhase.Stopped, machine.Phase);
    }

    [Fact]
    public void TransitionsNotifyInOrder()
    {
        var seen = new List<RelayRuntimePhase>();
        var machine = new RelayRuntimeStateMachine(seen.Add);
        machine.BeginStart();
        machine.CommitRunning();
        machine.BeginStop();
        machine.BeginStart();
        machine.Fail("assets boom");
        Assert.Equal(
            [RelayRuntimePhase.Starting, RelayRuntimePhase.Running, RelayRuntimePhase.Stopped, RelayRuntimePhase.Starting, RelayRuntimePhase.Degraded],
            seen);
    }

    [Fact]
    public async Task ConcurrentBeginStartOnlyOneWins()
    {
        var machine = new RelayRuntimeStateMachine();
        const int threads = 8;
        using var barrier = new Barrier(threads);
        var successes = 0;
        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait();
            if (machine.BeginStart())
            {
                Interlocked.Increment(ref successes);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
        await Task.WhenAll(tasks);
        Assert.Equal(1, successes);
        Assert.Equal(RelayRuntimePhase.Starting, machine.Phase);
    }
}

public sealed class RelayHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay-host-" + Guid.NewGuid().ToString("N"));

    public RelayHostTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class MemoryPrefs : IRelayPrefs
    {
        public Dictionary<string, string> Strings { get; } = new(StringComparer.Ordinal);

        public string? GetString(string file, string key) => Strings.GetValueOrDefault(file + "/" + key);

        public void PutString(string file, string key, string value) => Strings[file + "/" + key] = value;

        public bool GetBoolean(string file, string key, bool defValue) => defValue;
    }

    /// <summary>An interface stub answering every member with its default (null / false / 0).</summary>
    public class Stub : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var type = targetMethod?.ReturnType;
            return type is null || type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
        }

        public static T Of<T>() where T : class => Create<T, Stub>();
    }

    private (RelayHost Host, MemoryPrefs Prefs, List<string> Service) NewHost(Func<bool>? foreground = null)
    {
        var prefs = new MemoryPrefs();
        var service = new List<string>();
        var host = new RelayHost(new RelayHostPlatform
        {
            FilesDir = _root,
            SelfPackage = "org.example.dshmobile",
            SdkInt = 35,
            Prefs = prefs,
            A11y = Stub.Of<IA11yPlatform>(),
            Direct = Stub.Of<IDirectPlatform>(),
            Audio = Stub.Of<IAudioRecorder>(),
            Projection = Stub.Of<IProjectionPlatform>(),
            Shizuku = Stub.Of<IShizukuPlatform>(),
            DeviceFacts = () => new RelaySystemFacts(35, A11yBound: false, A11yListed: false, ShellAvailable: false,
                TrustedDisplayAvailable: false, NotificationListener: false, new HashSet<string>(), MicrophoneBlocked: false),
            UiInForeground = foreground ?? (() => false),
            AppInForeground = () => false,
            StartChannelService = () => service.Add("start"),
            StopChannelService = () => service.Add("stop"),
        });
        return (host, prefs, service);
    }

    [Fact]
    public void StartRunsEveryStepThenStopTearsDown()
    {
        var (host, prefs, service) = NewHost();
        using (host)
        {
            var phases = new List<RelayRuntimePhase>();
            host.PhaseChanged += phases.Add;
            host.StartInline();
            Assert.Equal(RelayRuntimePhase.Running, host.Phase);
            Assert.True(host.IsChannelRunning);
            Assert.True(Directory.Exists(host.Paths.InboxDir));
            Assert.Equal(["start"], service);
            Assert.Matches("^[0-9a-f]{32}$", prefs.Strings["relay/digest_salt"]);

            host.StartInline(); // already running: refused, nothing re-runs
            Assert.Equal(["start"], service);

            host.Stop();
            Assert.Equal(RelayRuntimePhase.Stopped, host.Phase);
            Assert.False(host.IsChannelRunning);
            Assert.Equal(["start", "stop"], service);
            Assert.Equal([RelayRuntimePhase.Starting, RelayRuntimePhase.Running, RelayRuntimePhase.Stopped], phases);
            var log = File.ReadAllText(Path.Combine(host.Paths.LogsDir, "relay.log"));
            Assert.Contains("runtime starting", log, StringComparison.Ordinal);
            Assert.Contains("runtime running", log, StringComparison.Ordinal);
            Assert.Contains("runtime stopped", log, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FailedStepDegradesUndoesInReverseAndAllowsRetry()
    {
        // The mailbox directories cannot be created: a file sits where the run dir must go.
        var (host, _, service) = NewHost();
        using (host)
        {
            var blocker = host.Paths.RunDir;
            if (Directory.Exists(blocker))
            {
                Directory.Delete(blocker, recursive: true);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
            File.WriteAllText(blocker, "blocker");
            host.StartInline();
            Assert.Equal(RelayRuntimePhase.Degraded, host.Phase);
            Assert.StartsWith("mailbox ", host.DegradedReason, StringComparison.Ordinal);
            Assert.Empty(service); // the channel service step never ran
            host.Stop(); // DEGRADED: nothing to stop
            Assert.Equal(RelayRuntimePhase.Degraded, host.Phase);

            File.Delete(blocker);
            host.StartInline();
            Assert.Equal(RelayRuntimePhase.Running, host.Phase);
            Assert.Null(host.DegradedReason);
            host.Stop();
        }
    }

    [Fact]
    public async Task RequestsFlowThroughMailboxCoordinatorAndBack()
    {
        var (host, _, _) = NewHost();
        using (host)
        {
            host.StartInline();
            var staging = Path.Combine(_root, "drop");
            Directory.CreateDirectory(staging);
            void Drop(string id, string capability, string args)
            {
                var temp = Path.Combine(staging, id + ".json");
                File.WriteAllText(temp, $$"""{"v":1,"id":"{{id}}","capability":"{{capability}}","args":{{args}},"ttlMs":20000}""",
                    new UTF8Encoding(false));
                File.Move(temp, Path.Combine(host.Paths.InboxDir, id + ".json"));
            }
            // Unknown capability: refused at admission. ui.snapshot: allowed by default policy, but the accessibility
            // service is revoked (not listed), so the gate refuses it as a missing system grant before any prompt.
            Drop("h1", "no.such", "{}");
            Drop("h2", "ui.snapshot", "{}");
            var sw = Stopwatch.StartNew();
            string? r1 = null, r2 = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(20) && (r1 is null || r2 is null))
            {
                await Task.Delay(50);
                r1 ??= TryRead(Path.Combine(host.Paths.OutboxDir, "h1.json"));
                r2 ??= TryRead(Path.Combine(host.Paths.OutboxDir, "h2.json"));
            }
            Assert.NotNull(r1);
            Assert.NotNull(r2);
            using var a = JsonDocument.Parse(r1);
            using var b = JsonDocument.Parse(r2);
            Assert.False(a.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("E_NOT_IMPLEMENTED", a.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.False(b.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("E_GATE_SYSTEM_MISSING", b.RootElement.GetProperty("error").GetProperty("code").GetString());
            host.Stop();
        }
    }

    [Fact]
    public async Task ApprovalIsPresentedOnThePageAndADenialAnswersTheCall()
    {
        var (host, _, _) = NewHost(foreground: () => true);
        using (host)
        {
            var routed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.ApprovalRoutingChanged += p =>
            {
                if (p.Request is { } r && p.Via == Automation.Relay.Interlock.InterlockChannel.Page)
                {
                    routed.TrySetResult(r.Id);
                }
            };
            host.StartInline();
            var staging = Path.Combine(_root, "drop");
            Directory.CreateDirectory(staging);
            var temp = Path.Combine(staging, "h3.json");
            File.WriteAllText(temp, """{"v":1,"id":"h3","capability":"pkg.query","args":{},"ttlMs":30000}""", new UTF8Encoding(false));
            File.Move(temp, Path.Combine(host.Paths.InboxDir, "h3.json"));

            var requestId = await routed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal("pkg.query", host.ApprovalRouting!.Request!.Prompt.Capability);
            Assert.False(host.ResolveApproval(InterlockChoice.Deny, requestId + 1)); // another card's answer is not taken
            Assert.True(host.ResolveApproval(InterlockChoice.Deny, requestId));

            var sw = Stopwatch.StartNew();
            string? r3 = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(20) && r3 is null)
            {
                await Task.Delay(50);
                r3 = TryRead(Path.Combine(host.Paths.OutboxDir, "h3.json"));
            }
            Assert.NotNull(r3);
            using var doc = JsonDocument.Parse(r3);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("E_GATE_HOST_DENIED", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            host.Stop();
        }
    }

    [Fact]
    public async Task StartPublishesTheGuestFilesAndAChangeIsRepublished()
    {
        var (host, prefs, _) = NewHost();
        using (host)
        {
            host.StartInline();
            Assert.True(File.Exists(host.Paths.ClientScript));
            Assert.True(File.Exists(host.Paths.UsageFile));
            var published = File.ReadAllText(host.Paths.CapabilitiesFile);
            Assert.Equal(host.CapabilitiesJson(), published);
            Assert.Equal(published, host.PublishedCapabilitiesJson);
            Assert.Contains("\"entry\": \"\\/opt\\/pilot\\/bin\\/relay\"", published, StringComparison.Ordinal);
            Assert.Contains("\"assistantTier\": \"ASK\"", published, StringComparison.Ordinal);

            // A tier the legacy page wrote: the next check publishes the new listing.
            prefs.PutString(RelaySurfaces.PrefsFile, RelayTierStore.Key("pkg.query"), "DENIED");
            host.RefreshAssetsIfChanged();
            var after = File.ReadAllText(host.Paths.CapabilitiesFile);
            Assert.NotEqual(published, after);
            Assert.Equal(after, host.PublishedCapabilitiesJson);

            // The guest rewrote the manual: unchanged listing, but the set is no longer intact, so it is republished.
            File.WriteAllText(host.Paths.UsageFile, "guest wrote this");
            host.RefreshAssetsIfChanged();
            Assert.NotEqual("guest wrote this", File.ReadAllText(host.Paths.UsageFile));

            // Stopped: a late trigger publishes nothing.
            host.Stop();
            File.WriteAllText(host.Paths.UsageFile, "after stop");
            host.RefreshAssetsIfChanged();
            host.RefreshAssets();
            await Task.Delay(50);
            Assert.Equal("after stop", File.ReadAllText(host.Paths.UsageFile));
        }
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
