// Translated from DSHBox pilot/dshbox-adapter (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/dshbox/adapter/surfaces/ApprovalCardView.kt (what the card says and which answers it offers)
// Registered in docs/UPSTREAMS.md. The look is this app's own Material 3 card, not DSHBox's glass card (no brand assets).

using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.Interlock;
using Google.Android.Material.Button;
using Google.Android.Material.Card;

namespace DshMobile.App.Automation.Relay;

/// <summary>
/// The one approval card (ADR-0011), hosted in two places: inside the 手机操控 page while that page is in front, and in
/// the floating overlay while the assistant works in another app. One class so both say the same thing: the request,
/// its target and parameters (approving a capability without seeing its object would endorse any object), the risk,
/// how many others wait, the time left, and the same answers (session grant only where the prompt offers one).
/// Every button filters obscured touches, so another app's overlay cannot click through to it.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Owned by its host view; Stop() ends the countdown and the Java peers go with the view.")]
internal sealed class ApprovalCardView
{
    private readonly Handler _main = new(Looper.MainLooper!);
    private readonly ApprovalPrompt _prompt;
    private readonly TextView _headline;
    private readonly TextView _countdown;
    private readonly Context _context;
    private readonly Action? _onDeadline;
    private int _waiting;
    private bool _stopped;

    /// <param name="onDeadline">
    /// Called once on the main thread when the countdown reaches the prompt's deadline (upstream: the page card, the
    /// overlay and the notification all close the question through the same expiry path when their time is up). The
    /// overlay parks and expires on its own, so it passes none.
    /// </param>
    public ApprovalCardView(Context context, ApprovalPrompt prompt, int waitingCount, Action<InterlockChoice> onChoose, Action? onDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(onChoose);
        _prompt = prompt;
        _onDeadline = onDeadline;
        _waiting = waitingCount;
        _context = new ContextThemeWrapper(context, Resource.Style.Theme_DshMobile);
        var density = _context.Resources!.DisplayMetrics!.Density;
        int Dp(int v) => (int)(v * density);

        var body = new LinearLayout(_context) { Orientation = Orientation.Vertical };
        body.SetPadding(Dp(20), Dp(20), Dp(20), Dp(16));

        var title = new TextView(_context) { Text = _context.GetString(Resource.String.relay_card_title) };
        title.SetTextAppearance(Resource.Style.TextAppearance_Material3_LabelLarge);
        body.AddView(title);

        _headline = new TextView(_context);
        _headline.SetTextAppearance(Resource.Style.TextAppearance_Material3_TitleLarge);
        _headline.SetPadding(0, Dp(6), 0, 0);
        body.AddView(_headline);
        PaintHeadline();

        if (!string.IsNullOrWhiteSpace(prompt.TargetLabel))
        {
            body.AddView(Line(_context.GetString(Resource.String.relay_card_target), Resource.Style.TextAppearance_Material3_LabelMedium, Dp(12)));
            body.AddView(Line(prompt.TargetLabel!, Resource.Style.TextAppearance_Material3_BodyLarge, 0));
        }
        if (!string.IsNullOrWhiteSpace(prompt.ParamDetail))
        {
            body.AddView(Line(_context.GetString(Resource.String.relay_card_detail), Resource.Style.TextAppearance_Material3_LabelMedium, Dp(10)));
            body.AddView(Line(prompt.ParamDetail!, Resource.Style.TextAppearance_Material3_BodyLarge, 0));
        }
        body.AddView(Line(_context.GetString(prompt.Risk switch
        {
            RiskLevel.High => Resource.String.relay_card_risk_high,
            RiskLevel.Medium => Resource.String.relay_card_risk_medium,
            _ => Resource.String.relay_card_risk_low,
        }), Resource.Style.TextAppearance_Material3_BodyMedium, Dp(12)));
        _countdown = Line("", Resource.Style.TextAppearance_Material3_BodySmall, Dp(4));
        body.AddView(_countdown);

        var row = new LinearLayout(_context) { Orientation = Orientation.Horizontal };
        row.SetPadding(0, Dp(16), 0, 0);
        // Not the primary colour for "allow": the card must not suggest which answer to pick.
        row.AddView(Button(Resource.String.relay_approval_deny, InterlockChoice.Deny, onChoose, outlined: true),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { MarginEnd = Dp(8) });
        if (prompt.AllowsSessionGrant)
        {
            row.AddView(Button(Resource.String.relay_approval_allow_session, InterlockChoice.AllowSession, onChoose, outlined: true),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { MarginEnd = Dp(8) });
        }
        row.AddView(Button(Resource.String.relay_approval_allow_once, InterlockChoice.AllowOnce, onChoose, outlined: true),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        body.AddView(row);

        var card = new MaterialCardView(_context) { FilterTouchesWhenObscured = true };
        card.AddView(body);
        View = card;
    }

    /// <summary>The card; the host gives it a full-width slot and its own margins.</summary>
    public View View { get; }

    /// <summary>"另有 N 个请求排队" follows the queue while the card is up.</summary>
    public void SetPendingCount(int waiting)
    {
        _waiting = waiting;
        PaintHeadline();
    }

    /// <summary>Starts the once-a-second countdown (main thread).</summary>
    public void Start()
    {
        _stopped = false;
        Tick();
    }

    public void Stop()
    {
        _stopped = true;
        _main.RemoveCallbacksAndMessages(null);
    }

    private void Tick()
    {
        if (_stopped)
        {
            return;
        }
        var left = Math.Max(0L, _prompt.DeadlineAtMs - DshMobile.Runtime.Automation.Relay.Logging.RelayClock.MonotonicNow());
        _countdown.Text = _context.GetString(Resource.String.relay_card_countdown, (int)((left + 999) / 1000));
        if (left > 0)
        {
            _main.PostDelayed(Tick, Math.Min(1000L, left));
            return;
        }
        _stopped = true;
        _onDeadline?.Invoke();
    }

    private void PaintHeadline() =>
        _headline.Text = ApprovalNotificationContent.Text(_prompt.Capability, _waiting);

    private TextView Line(string text, int appearance, int topPadding)
    {
        var view = new TextView(_context) { Text = text };
        view.SetTextAppearance(appearance);
        view.SetPadding(0, topPadding, 0, 0);
        return view;
    }

    private MaterialButton Button(int text, InterlockChoice choice, Action<InterlockChoice> onChoose, bool outlined)
    {
        var button = outlined
            ? new MaterialButton(_context, null, Resource.Attribute.materialButtonOutlinedStyle)
            : new MaterialButton(_context);
        button.SetText(text);
        button.FilterTouchesWhenObscured = true;
        var answered = false;
        button.Click += (_, _) =>
        {
            if (answered)
            {
                return;
            }
            answered = true;
            onChoose(choice);
        };
        return button;
    }
}
