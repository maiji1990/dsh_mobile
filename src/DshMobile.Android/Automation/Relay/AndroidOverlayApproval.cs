// Translated from DSHBox pilot/dshbox-adapter (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/dshbox/adapter/surfaces/OverlayApproval.kt (OverlayApprovalPresenter: present / showAndWait /
//   awaitOnScreen / WindowOwner / FocusAwareLayout / withdraw / park)
// Registered in docs/UPSTREAMS.md.

using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.Interlock;
using DshMobile.Runtime.Automation.Relay.Logging;

namespace DshMobile.App.Automation.Relay;

/// <summary>
/// The floating approval card (ADR-0011, upstream <c>OverlayApprovalPresenter</c>): a TYPE_APPLICATION_OVERLAY window
/// over everything, so the user is asked on the spot while the assistant works in another app. The only condition is
/// the user's "display over other apps" grant; without it this returns UNABLE_TO_SHOW and the queue takes the next
/// route (the notification card). A granted overlay can still be hidden by the platform (a window with
/// HIDE_NON_SYSTEM_OVERLAY_WINDOWS on any screen): the only evidence is window focus, so the card that never gets focus
/// is withdrawn and reported UNABLE_TO_SHOW instead of letting the user stare at nothing until the call times out.
///
/// Security (ADR-0008 / ADR-0011): this window belongs to this app, so while it is listed Relay refuses every gesture,
/// node action and key on the host display (HostWindowRule); <see cref="OnScreen"/> makes the a11y platform count it
/// even when the system leaves it out of the window list. FLAG_SECURE keeps it out of screen capture, every button
/// filters obscured touches, and an allow is handed back only after the window is gone (the same reason as
/// RelayApprovalConfirmActivity: an approved write runs at once and must not meet our own window).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Process-lifetime singleton (CsRelay); its handler and lock live as long as the app.")]
internal sealed class AndroidOverlayApproval : IOverlayApprovalChannel
{
    /// <summary>Same as the queue's inline wait: the channel time one call gives up for a person to answer.</summary>
    private const long InlineApprovalWaitMs = InterlockQueue.InlineApprovalWaitMs;

    /// <summary>Upstream ON_SCREEN_BUDGET_MS: focus can come back a second or more after addView.</summary>
    private const long OnScreenBudgetMs = 4000;

    /// <summary>How long after removing the window an allow is handed back (RelayApprovalConfirmActivity.AnswerAfterCloseMs).</summary>
    private const int AnswerAfterCloseMs = 500;

    private static volatile bool s_onScreen;

    private readonly Context _context;
    private readonly Handler _main = new(Looper.MainLooper!);
    private readonly SemaphoreSlim _windowLock = new(1, 1);
    private readonly Action<string, string, string> _logUnavailable;
    private int _waiting;
    private volatile bool _closed;
    private WindowOwner? _current;
    private WindowOwner? _parked;

    /// <param name="logUnavailable">(capability, cause, at) → the relay log's OVERLAY_UNAVAILABLE line.</param>
    public AndroidOverlayApproval(Context context, Action<string, string, string> logUnavailable)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context.ApplicationContext ?? context;
        _logUnavailable = logUnavailable;
    }

    /// <summary>Whether a card of this channel is on screen right now (any instance; read by the a11y platform).</summary>
    public static bool OnScreen => s_onScreen;

    public Action<ApprovalPrompt, InterlockChoice>? OnParkedChoice { get; set; }

    public Action? OnHidden { get; set; }

    public bool CanShow() => Android.Provider.Settings.CanDrawOverlays(_context);

    public bool IsPresented() => s_onScreen && (_current is not null || _parked is not null);

    /// <summary>Upstream <c>withdraw</c>: the route moved away or the channel stops; idempotent, any thread.</summary>
    public void Withdraw()
    {
        _closed = true;
        s_onScreen = false;
        var owner = _parked ?? _current;
        if (owner is not null)
        {
            _main.Post(owner.Retire);
        }
    }

    public async Task<InterlockChoice> PresentAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (!CanShow())
        {
            return InterlockChoice.UnableToShow;
        }
        Interlocked.Increment(ref _waiting);
        PaintCounter();
        var queued = true;
        try
        {
            await _windowLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Interlocked.Decrement(ref _waiting);
                queued = false;
                PaintCounter();
                return await ShowAndWaitAsync(prompt, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _windowLock.Release();
            }
        }
        finally
        {
            if (queued)
            {
                Interlocked.Decrement(ref _waiting);
            }
            if (Volatile.Read(ref _waiting) < 0)
            {
                Volatile.Write(ref _waiting, 0);
            }
        }
    }

    private async Task<InterlockChoice> ShowAndWaitAsync(ApprovalPrompt prompt, CancellationToken ct)
    {
        // One card at a time: a parked card still waiting means "still waiting", not a second card on top of it.
        if (_parked is { } left)
        {
            if (!left.IsPastDeadline())
            {
                return InterlockChoice.Awaiting;
            }
            await OnMain(left.Retire).ConfigureAwait(false);
        }
        _closed = false;
        var choice = new TaskCompletionSource<InterlockChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handedOff = false;
        var clicked = false;
        WindowOwner? owner = null;
        var startedAt = RelayClock.MonotonicNow();
        var totalBudget = Math.Max(0L, Math.Min(InlineApprovalWaitMs, prompt.DeadlineAtMs - startedAt));
        try
        {
            owner = await OnMain(() =>
            {
                var wm = _context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>()!;
                WindowOwner? self = null;
                var card = new ApprovalCardView(_context, prompt, Volatile.Read(ref _waiting), selection =>
                {
                    // A late click (the caller already went with "still waiting") goes to the queue's mailbox for the
                    // next retry of the same call; either way the window closes now and the answer follows its removal.
                    var late = handedOff && !_closed;
                    clicked = true;
                    self?.Retire();
                    _main.PostDelayed(() =>
                    {
                        choice.TrySetResult(selection);
                        if (late)
                        {
                            OnParkedChoice?.Invoke(prompt, selection);
                        }
                    }, AnswerAfterCloseMs);
                });
                var root = new FocusAwareLayout(_context);
                var density = _context.Resources!.DisplayMetrics!.Density;
                root.AddView(card.View, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
                {
                    Gravity = GravityFlags.Top,
                    TopMargin = (int)(52 * density),
                    LeftMargin = (int)(16 * density),
                    RightMargin = (int)(16 * density),
                });
                self = new WindowOwner(this, wm, root, card);
                _current = self;
                root.FocusLost = self.ReportHiddenOnce;
                var lp = new WindowManagerLayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent,
                    WindowManagerTypes.ApplicationOverlay,
                    // Focusable (no NOT_FOCUSABLE): focus is the on-screen evidence and the buttons need it.
                    WindowManagerFlags.LayoutInScreen | WindowManagerFlags.LayoutNoLimits | WindowManagerFlags.Secure,
                    Format.Translucent)
                {
                    Gravity = GravityFlags.Top | GravityFlags.Start,
                };
                wm.AddView(root, lp);
                s_onScreen = true;
                card.Start();
                return self;
            }).ConfigureAwait(false);

            var onScreen = await AwaitOnScreenAsync(owner, Math.Min(OnScreenBudgetMs, totalBudget), ct).ConfigureAwait(false);
            if (!onScreen)
            {
                _logUnavailable(prompt.Capability, "hidden-by-platform", "no-focus");
                await OnMain(owner.Retire).ConfigureAwait(false);
                return InterlockChoice.UnableToShow;
            }
            s_onScreen = true;
            PaintCounter();
            var answerBudget = Math.Max(0L, totalBudget - (RelayClock.MonotonicNow() - startedAt));
            var finished = await Task.WhenAny(choice.Task, Task.Delay(TimeSpan.FromMilliseconds(answerBudget), ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (finished == choice.Task)
            {
                return await choice.Task.ConfigureAwait(false);
            }
            // Decided on the main thread in one step, where clicks are delivered: a click is either already counted
            // here (its answer follows the window's removal and is returned) or lands after the hand-off (late answer).
            var outcome = await OnMain(() =>
            {
                if (clicked)
                {
                    return (InterlockChoice?)null;
                }
                if (prompt.DeadlineAtMs - RelayClock.MonotonicNow() <= 0)
                {
                    owner.Retire();
                    return InterlockChoice.Expired;
                }
                if (!owner.IsOpen)
                {
                    return InterlockChoice.UnableToShow;
                }
                // Give the channel back, keep the card: the user can still answer and a retry picks it up.
                owner.ParkUntil(prompt.DeadlineAtMs);
                _parked = owner;
                handedOff = true;
                return InterlockChoice.Awaiting;
            }).ConfigureAwait(false);
            // A click that landed during the wait is only in the pending answer: never let it evaporate (a lost
            // "deny" would ask the same thing again).
            return outcome ?? await choice.Task.ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
        {
            if (owner is not null)
            {
                _main.Post(owner.Retire);
            }
            throw;
        }
#pragma warning disable CA1031 // window failures (grant revoked, window limit) hand the question to the next route
        catch (Exception e)
#pragma warning restore CA1031
        {
            if (owner is not null)
            {
                _main.Post(owner.Retire);
            }
            _logUnavailable(prompt.Capability, e.Message, e.TargetSite?.Name ?? "no-frame");
            return InterlockChoice.UnableToShow;
        }
    }

    /// <summary>Upstream <c>awaitOnScreen</c>: focus within a time budget (not a frame count; vsync stops when the screen is off).</summary>
    private async Task<bool> AwaitOnScreenAsync(WindowOwner owner, long budgetMs, CancellationToken ct)
    {
        var until = RelayClock.MonotonicNow() + budgetMs;
        while (true)
        {
            if (await OnMain(() => owner.HasFocus).ConfigureAwait(false))
            {
                return true;
            }
            if (RelayClock.MonotonicNow() >= until)
            {
                return false;
            }
            await Task.Delay(16, ct).ConfigureAwait(false);
        }
    }

    private void PaintCounter()
    {
        var owner = _current;
        if (owner is not null)
        {
            var waiting = Math.Max(0, Volatile.Read(ref _waiting));
            _main.Post(() => owner.Card.SetPendingCount(waiting));
        }
    }

    private Task<T> OnMain<T>(Func<T> work)
    {
        if (Looper.MyLooper() == Looper.MainLooper)
        {
            return Task.FromResult(work());
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _main.Post(() =>
        {
            try
            {
                tcs.TrySetResult(work());
            }
#pragma warning disable CA1031 // marshalled to the awaiting caller
            catch (Exception e)
#pragma warning restore CA1031
            {
                tcs.TrySetException(e);
            }
        });
        return tcs.Task;
    }

    private Task OnMain(Action work) => OnMain(() =>
    {
        work();
        return true;
    });

    /// <summary>Upstream <c>WindowOwner</c>: one window's removal, idempotent, parkable; main thread only.</summary>
    private sealed class WindowOwner(AndroidOverlayApproval channel, IWindowManager wm, FocusAwareLayout root, ApprovalCardView card)
    {
        private bool _closed;
        private bool _hiddenReported;
        private long _parkDeadlineMs;

        public ApprovalCardView Card { get; } = card;

        public bool IsOpen => !_closed;

        public bool HasFocus => !_closed && root.HasWindowFocus;

        public bool IsPastDeadline() => _parkDeadlineMs != 0 && RelayClock.MonotonicNow() >= _parkDeadlineMs;

        public void ParkUntil(long deadlineAtMs)
        {
            _parkDeadlineMs = deadlineAtMs;
            channel._main.PostDelayed(OnParkDeadline, Math.Max(0L, deadlineAtMs - RelayClock.MonotonicNow()));
        }

        private void OnParkDeadline()
        {
            if (!_closed)
            {
                Retire();
            }
        }

        /// <summary>Once, and never after removal (removing the window also moves focus).</summary>
        public void ReportHiddenOnce()
        {
            if (_closed || _hiddenReported)
            {
                return;
            }
            _hiddenReported = true;
            s_onScreen = false;
            channel.OnHidden?.Invoke();
        }

        public void Retire()
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            channel._main.RemoveCallbacks(OnParkDeadline);
            if (ReferenceEquals(channel._parked, this))
            {
                channel._parked = null;
            }
            if (ReferenceEquals(channel._current, this))
            {
                channel._current = null;
            }
            s_onScreen = channel._current is not null || channel._parked is not null;
            Card.Stop();
            try
            {
                wm.RemoveViewImmediate(root);
            }
            catch (Java.Lang.Exception)
            {
                // Already gone (the grant was revoked, the process is tearing down).
            }
        }
    }

    /// <summary>Upstream <c>FocusAwareLayout</c>: the only "still in front of the user" signal is window focus.</summary>
    private sealed class FocusAwareLayout(Context context) : FrameLayout(context)
    {
        public Action? FocusLost { get; set; }

        public override void OnWindowFocusChanged(bool hasWindowFocus)
        {
            base.OnWindowFocusChanged(hasWindowFocus);
            if (!hasWindowFocus)
            {
                FocusLost?.Invoke();
            }
        }
    }
}
