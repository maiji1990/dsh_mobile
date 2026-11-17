using DshMobile.Runtime.Journal;
using Microsoft.Extensions.Time.Testing;

namespace DshMobile.Runtime.Tests;

public sealed class RuntimeStateMachineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-journal-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void FreshInstall_StartsNotInstalledGenerationZero()
    {
        var sm = new RuntimeStateMachine(new InMemoryRuntimeJournal(), "test");
        Assert.Equal(RuntimeState.NotInstalled, sm.Current.State);
        Assert.Equal(0, sm.Current.Generation);
    }

    [Fact]
    public void HappyPath_IsJournaledAndBumpsGenerationOnStart()
    {
        var journal = new InMemoryRuntimeJournal();
        var sm = new RuntimeStateMachine(journal, "test");
        Assert.True(sm.TryTransition(RuntimeState.Staging, "import"));
        Assert.True(sm.TryTransition(RuntimeState.Ready, "imported"));
        Assert.True(sm.TryTransition(RuntimeState.Starting, "start"));
        Assert.True(sm.TryTransition(RuntimeState.Running, "up", HarnessState.Ready, dshVersion: "0.2.1-rc.5"));
        Assert.True(sm.TryTransition(RuntimeState.Stopping, "stop"));
        Assert.True(sm.TryTransition(RuntimeState.Ready, "stopped", HarnessState.Stopped));
        Assert.True(sm.TryTransition(RuntimeState.Starting, "again"));
        Assert.Equal(2, sm.Current.Generation);
        var tail = journal.ReadTail(100);
        Assert.Equal(7, tail.Count);
        Assert.All(tail, e => Assert.Equal("test", e.Implementation));
        Assert.Equal("0.2.1-rc.5", sm.Current.DshVersion);
    }

    [Theory]
    [InlineData(RuntimeState.NotInstalled, RuntimeState.Starting)]
    [InlineData(RuntimeState.NotInstalled, RuntimeState.Running)]
    [InlineData(RuntimeState.Ready, RuntimeState.Running)]
    [InlineData(RuntimeState.Staging, RuntimeState.Starting)]
    [InlineData(RuntimeState.Stopping, RuntimeState.Running)]
    public void IllegalTransitions_AreRefusedAndNotJournaled(RuntimeState from, RuntimeState to)
    {
        Assert.False(RuntimeStateMachine.IsAllowed(from, to));
    }

    [Fact]
    public void RefusedTransition_LeavesStateAndJournalUntouched()
    {
        var journal = new InMemoryRuntimeJournal();
        var sm = new RuntimeStateMachine(journal, "test");
        Assert.False(sm.TryTransition(RuntimeState.Running, "skip ahead"));
        Assert.Equal(RuntimeState.NotInstalled, sm.Current.State);
        Assert.Empty(journal.ReadTail(10));
    }

    [Fact]
    public void Failed_CarriesErrorAndLeavingFailedClearsIt()
    {
        var sm = new RuntimeStateMachine(new InMemoryRuntimeJournal(), "test");
        sm.TryTransition(RuntimeState.Failed, "boom", errorCode: "RUNTIME_ERROR", errorMessage: "x");
        Assert.Equal("RUNTIME_ERROR", sm.Current.ErrorCode);
        sm.TryTransition(RuntimeState.Staging, "retry");
        Assert.Null(sm.Current.ErrorCode);
    }

    [Fact]
    public void Observe_RecordsLegacyStateWithSourceAndDeduplicates()
    {
        var journal = new InMemoryRuntimeJournal();
        var sm = new RuntimeStateMachine(journal, "test");
        sm.Observe(RuntimeState.Running, HarnessState.Ready, "0.2.1", "legacy-bridge");
        sm.Observe(RuntimeState.Running, HarnessState.Ready, "0.2.1", "legacy-bridge");
        var e = Assert.Single(journal.ReadTail(10));
        Assert.Equal("observed:legacy-bridge", e.Reason);
    }

    [Fact]
    public void Observe_MissingLayerClearsTheStoredVersion()
    {
        var journal = new InMemoryRuntimeJournal();
        var sm = new RuntimeStateMachine(journal, "test");
        sm.Observe(RuntimeState.Ready, HarnessState.Stopped, "0.2.1", "disk");
        sm.Observe(RuntimeState.Ready, HarnessState.Stopped, null, "disk");
        sm.Observe(RuntimeState.Ready, HarnessState.Stopped, null, "disk");
        Assert.Null(sm.Current.DshVersion);
        Assert.Equal(2, journal.ReadTail(10).Count);
    }

    [Fact]
    public void FileJournal_SurvivesRestart_AndInterruptedOperationBecomesFailed()
    {
        var path = Path.Combine(_dir, "j.jsonl");
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-10T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var sm = new RuntimeStateMachine(new FileRuntimeJournal(path), "test", time);
        sm.TryTransition(RuntimeState.Ready, "installed");
        sm.TryTransition(RuntimeState.Starting, "start");

        // Process dies mid-start; a new process reads the journal.
        var after = new RuntimeStateMachine(new FileRuntimeJournal(path), "test", time);
        Assert.Equal(RuntimeState.Failed, after.Current.State);
        Assert.Equal("RUNTIME_INTERRUPTED", after.Current.ErrorCode);
        Assert.Equal(1, after.Current.Generation);
        Assert.Equal("recovered-after-restart", new FileRuntimeJournal(path).ReadLast()!.Reason);
    }

    [Fact]
    public void FileJournal_RestoresStableState()
    {
        var path = Path.Combine(_dir, "j.jsonl");
        var sm = new RuntimeStateMachine(new FileRuntimeJournal(path), "test");
        sm.TryTransition(RuntimeState.Ready, "installed");
        Assert.Equal(RuntimeState.Ready, new RuntimeStateMachine(new FileRuntimeJournal(path), "test").Current.State);
    }

    [Fact]
    public void FileJournal_SkipsTornLastLine()
    {
        var path = Path.Combine(_dir, "j.jsonl");
        var journal = new FileRuntimeJournal(path);
        new RuntimeStateMachine(journal, "test").TryTransition(RuntimeState.Ready, "installed");
        File.AppendAllText(path, "{\"at\":\"2026-11-10T00:00:00Z\",\"generation\":1,\"fr");
        Assert.Equal(RuntimeState.Ready, journal.ReadLast()!.To);
    }

    [Fact]
    public void FileJournal_CompactsAndKeepsNewest()
    {
        var path = Path.Combine(_dir, "j.jsonl");
        var journal = new FileRuntimeJournal(path);
        var sm = new RuntimeStateMachine(journal, new string('x', 600));
        for (var i = 0; i < 1200; i++)
        {
            sm.TryTransition(i % 2 == 0 ? RuntimeState.Staging : RuntimeState.Ready, "n" + i);
        }
        Assert.True(new FileInfo(path).Length <= FileRuntimeJournal.CompactAtBytes + 4096);
        Assert.Equal("n1199", journal.ReadLast()!.Reason);
        Assert.True(journal.ReadTail(int.MaxValue).Count <= FileRuntimeJournal.KeepLines + 600);
    }
}
