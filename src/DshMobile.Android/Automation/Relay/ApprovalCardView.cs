// Translated from DSHBox pilot/dshbox-adapter (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/dshbox/adapter/surfaces/ApprovalCardView.kt (what the card says and which answers it offers)
// Registered in docs/UPSTREAMS.md. 全 XML 声明（view_relay_approval_card.xml），保持防叠加点击与三项等权轮廓按钮。

using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.Interlock;
using Google.Android.Material.Button;

namespace DshMobile.App.Automation.Relay;

/// <summary>
/// 手机操控唯一审批卡（ADR-0011 / UI Slice 4）：
/// 承载页内顶槽与全局悬浮窗两种展示宿主，布局完全由 <c>view_relay_approval_card.xml</c> 声明，
/// 严格开启 <c>FilterTouchesWhenObscured</c> 与单次应答锁定，按钮不使用主色暗示默认“允许”。
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
    private bool _answered;

    public ApprovalCardView(Context context, ApprovalPrompt prompt, int waitingCount, Action<InterlockChoice> onChoose, Action? onDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(onChoose);
        _prompt = prompt;
        _onDeadline = onDeadline;
        _waiting = waitingCount;
        _context = new ContextThemeWrapper(context, Resource.Style.Theme_DshMobile);

        var card = LayoutInflater.From(_context)!.Inflate(Resource.Layout.view_relay_approval_card, null, false)!;
        card.FilterTouchesWhenObscured = true;

        _headline = card.FindViewById<TextView>(Resource.Id.relay_card_headline)!;
        PaintHeadline();

        if (!string.IsNullOrWhiteSpace(prompt.TargetLabel))
        {
            card.FindViewById(Resource.Id.relay_card_target_label)!.Visibility = ViewStates.Visible;
            var targetValue = card.FindViewById<TextView>(Resource.Id.relay_card_target_value)!;
            targetValue.Visibility = ViewStates.Visible;
            targetValue.Text = prompt.TargetLabel;
        }
        if (!string.IsNullOrWhiteSpace(prompt.ParamDetail))
        {
            card.FindViewById(Resource.Id.relay_card_detail_label)!.Visibility = ViewStates.Visible;
            var detailValue = card.FindViewById<TextView>(Resource.Id.relay_card_detail_value)!;
            detailValue.Visibility = ViewStates.Visible;
            detailValue.Text = prompt.ParamDetail;
        }

        card.FindViewById<TextView>(Resource.Id.relay_card_risk)!.SetText(prompt.Risk switch
        {
            RiskLevel.High => Resource.String.relay_card_risk_high,
            RiskLevel.Medium => Resource.String.relay_card_risk_medium,
            _ => Resource.String.relay_card_risk_low,
        });
        _countdown = card.FindViewById<TextView>(Resource.Id.relay_card_countdown)!;

        WireChoiceButton(card.FindViewById<MaterialButton>(Resource.Id.relay_card_btn_deny)!, InterlockChoice.Deny, onChoose);
        var sessionBtn = card.FindViewById<MaterialButton>(Resource.Id.relay_card_btn_allow_session)!;
        if (prompt.AllowsSessionGrant)
        {
            sessionBtn.Visibility = ViewStates.Visible;
            WireChoiceButton(sessionBtn, InterlockChoice.AllowSession, onChoose);
        }
        else
        {
            sessionBtn.Visibility = ViewStates.Gone;
        }
        WireChoiceButton(card.FindViewById<MaterialButton>(Resource.Id.relay_card_btn_allow_once)!, InterlockChoice.AllowOnce, onChoose);

        View = card;
    }

    public View View { get; }

    public void SetPendingCount(int waiting)
    {
        _waiting = waiting;
        PaintHeadline();
    }

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

    private void WireChoiceButton(MaterialButton button, InterlockChoice choice, Action<InterlockChoice> onChoose)
    {
        button.FilterTouchesWhenObscured = true;
        button.Click += (_, _) =>
        {
            if (_answered)
            {
                return;
            }
            _answered = true;
            onChoose(choice);
        };
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
}
