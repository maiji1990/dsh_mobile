using DshMobile.Application.Ui;
using DshMobile.Runtime;

namespace DshMobile.Application.Tests;

/// <summary>ADR-0021: the home status card is a pure function of the journal and the running / last operation.</summary>
public sealed class HomeStatusTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

    private static RuntimeSnapshot Snap(RuntimeState state, HarnessState harness = HarnessState.Stopped, string? code = null, string? version = "0.2.1-rc.5") =>
        new(state, harness, 1, version, code, code is null ? null : "why", At);

    private static OperationOutcome Outcome(string op, bool ok, string code = "OK") => new(op, ok, code, ok ? null : "message", At);

    [Theory]
    [InlineData(RuntimeState.NotInstalled, HarnessState.Stopped, HomePhase.NotInstalled, HomeAction.ImportRuntime, HomeAction.None)]
    [InlineData(RuntimeState.Staging, HarnessState.Stopped, HomePhase.Busy, HomeAction.None, HomeAction.None)]
    [InlineData(RuntimeState.Ready, HarnessState.Stopped, HomePhase.Stopped, HomeAction.Start, HomeAction.None)]
    [InlineData(RuntimeState.Starting, HarnessState.Starting, HomePhase.Starting, HomeAction.None, HomeAction.Stop)]
    [InlineData(RuntimeState.Recovering, HarnessState.Stopped, HomePhase.Starting, HomeAction.None, HomeAction.Stop)]
    [InlineData(RuntimeState.Running, HarnessState.Ready, HomePhase.Ready, HomeAction.OpenDsh, HomeAction.Stop)]
    [InlineData(RuntimeState.Running, HarnessState.Running, HomePhase.WaitingForWebUi, HomeAction.None, HomeAction.Stop)]
    [InlineData(RuntimeState.Running, HarnessState.Starting, HomePhase.WaitingForWebUi, HomeAction.None, HomeAction.Stop)]
    [InlineData(RuntimeState.Running, HarnessState.Failed, HomePhase.Failed, HomeAction.Start, HomeAction.Stop)]
    [InlineData(RuntimeState.Running, HarnessState.Stopped, HomePhase.Failed, HomeAction.Start, HomeAction.Stop)]
    [InlineData(RuntimeState.Stopping, HarnessState.Ready, HomePhase.Stopping, HomeAction.None, HomeAction.None)]
    [InlineData(RuntimeState.Failed, HarnessState.Stopped, HomePhase.Failed, HomeAction.Start, HomeAction.None)]
    public void Journal_MapsToOneCard(RuntimeState state, HarnessState harness, HomePhase phase, HomeAction primary, HomeAction secondary)
    {
        var s = HomeStatus.From(Snap(state, harness), null, null);

        Assert.Equal(phase, s.Phase);
        Assert.Equal(primary, s.Primary);
        Assert.Equal(secondary, s.Secondary);
        Assert.Equal("0.2.1-rc.5", s.DshVersion);
    }

    [Fact]
    public void EveryJournalState_HasACard()
    {
        foreach (var state in Enum.GetValues<RuntimeState>())
        {
            foreach (var harness in Enum.GetValues<HarnessState>())
            {
                Assert.True(Enum.IsDefined(HomeStatus.From(Snap(state, harness), null, null).Phase));
            }
        }
    }

    [Fact]
    public void ReadyWithoutADshLayer_OffersTheDshImport_ADR0027()
    {
        // The runtime body is installed but the DSH layer is not (no longer bundled with the APK): the next step is
        // importing one, not a start that would fail on the missing layer.
        var s = HomeStatus.From(Snap(RuntimeState.Ready, version: null), null, null);
        Assert.Equal(HomePhase.Stopped, s.Phase);
        Assert.Equal(HomeAction.ImportDsh, s.Primary);
        Assert.Null(s.DshVersion);
    }

    [Theory]
    [InlineData(HomeStatus.StartOperation, HomePhase.Starting)]
    [InlineData(HomeStatus.StopOperation, HomePhase.Stopping)]
    [InlineData(HomeStatus.ImportOperation, HomePhase.Busy)]
    [InlineData(HomeStatus.ImportDshOperation, HomePhase.Busy)]
    [InlineData("diagnostics", HomePhase.Busy)]
    public void RunningOperation_WinsOverTheJournal_AndOffersNoButtons(string operation, HomePhase phase)
    {
        var s = HomeStatus.From(Snap(RuntimeState.Ready), operation, Outcome("start", ok: false));

        Assert.Equal(phase, s.Phase);
        Assert.Equal(HomeAction.None, s.Primary);
        Assert.Equal(HomeAction.None, s.Secondary);
        Assert.Equal(operation, s.Operation);
        Assert.Null(s.FailedOperation); // stale while something runs
    }

    [Fact]
    public void FailedJournal_KeepsItsCode_AndRetriesTheImportThatFailed()
    {
        var afterImport = HomeStatus.From(Snap(RuntimeState.Failed, code: "RUNTIME_IMPORT_UNVERIFIED"), null, Outcome(HomeStatus.ImportOperation, ok: false, "RUNTIME_IMPORT_UNVERIFIED"));
        var afterDshImport = HomeStatus.From(Snap(RuntimeState.Failed, code: "DSH_BUNDLE_INVALID"), null, Outcome(HomeStatus.ImportDshOperation, ok: false, "DSH_BUNDLE_INVALID"));
        var afterStart = HomeStatus.From(Snap(RuntimeState.Failed, code: "DSH_EXITED"), null, Outcome(HomeStatus.StartOperation, ok: false, "DSH_EXITED"));

        Assert.Equal(("RUNTIME_IMPORT_UNVERIFIED", HomeAction.ImportRuntime), (afterImport.ErrorCode, afterImport.Primary));
        Assert.Equal(("DSH_BUNDLE_INVALID", HomeAction.ImportDsh), (afterDshImport.ErrorCode, afterDshImport.Primary));
        Assert.Equal(("DSH_EXITED", HomeAction.Start), (afterStart.ErrorCode, afterStart.Primary));
        // The journal already shows the failure; the card does not repeat it as a separate notice.
        Assert.Null(afterImport.FailedOperation);
        Assert.Null(afterStart.FailedOperation);
    }

    [Fact]
    public void FailedOperation_TheJournalDoesNotShow_IsReportedUnderTheCard()
    {
        var refused = Outcome(HomeStatus.StartOperation, ok: false, "INVALID_TRANSITION");

        var s = HomeStatus.From(Snap(RuntimeState.Ready), null, refused);

        Assert.Equal(HomePhase.Stopped, s.Phase);
        Assert.Same(refused, s.FailedOperation);
        Assert.Null(HomeStatus.From(Snap(RuntimeState.Ready), null, Outcome("start", ok: true)).FailedOperation);
    }
}
