using Android.Views;
using Android.Widget;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;
using DshMobile.App.Ui;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Google.Android.Material.TextField;

namespace DshMobile.App.Chat;

/// <summary>
/// UI-S3 非模态固定审批 / 问答槽控制器：
/// 一次完整展开当前选中卡片，多卡时顶部切换；中间参数 / 问题区独立滚动并限制为窗口高度 40%，
/// 底部动作行与 Hold-to-undo 撤销条永远固定在滚动区之外。不弹出对话框、不抢输入焦点。
/// </summary>
internal sealed class ChatRequestSlotController
{
    private readonly View _root;
    private readonly LinearLayout _switchRow;
    private readonly TextView _switchText;
    private readonly TextView _status;
    private readonly ScrollView _scroll;
    private readonly LinearLayout _questions;
    private readonly TextView _description;
    private readonly TextView _missingWarning;
    private readonly TextView _args;
    private readonly View _approvalActions;
    private readonly View _questionActions;
    private readonly View _holdBar;
    private readonly TextView _holdText;
    private readonly TextView _stateText;
    private readonly Action<string, ApprovalDecision> _onApproval;
    private readonly Action<string> _onUndo;
    private readonly Action<string, UserQuestionAnswerBatch> _onAnswer;
    private readonly Action<string> _onCancelQuestion;
    private readonly List<QuestionInputBinding> _questionInputs = [];

    private List<PendingInteractionCard> _cards = [];
    private int _selectedIndex;
    private string? _selectedEventId;
    private string? _boundEventId;
    private IReadOnlyList<UserQuestionItemView>? _boundQuestions;

    public ChatRequestSlotController(
        View root,
        Action<string, ApprovalDecision> onApproval,
        Action<string> onUndo,
        Action<string, UserQuestionAnswerBatch> onAnswer,
        Action<string> onCancelQuestion)
    {
        _root = root;
        _onApproval = onApproval;
        _onUndo = onUndo;
        _onAnswer = onAnswer;
        _onCancelQuestion = onCancelQuestion;

        _switchRow = root.FindViewById<LinearLayout>(Resource.Id.chat_request_slot_switch_row)!;
        _switchText = root.FindViewById<TextView>(Resource.Id.chat_request_slot_switch_text)!;
        _status = root.FindViewById<TextView>(Resource.Id.chat_request_slot_status)!;
        _scroll = root.FindViewById<ScrollView>(Resource.Id.chat_request_slot_scroll)!;
        _questions = root.FindViewById<LinearLayout>(Resource.Id.chat_request_slot_questions)!;
        _description = root.FindViewById<TextView>(Resource.Id.chat_request_slot_description)!;
        _missingWarning = root.FindViewById<TextView>(Resource.Id.chat_request_slot_missing_warning)!;
        _args = root.FindViewById<TextView>(Resource.Id.chat_request_slot_args)!;
        _approvalActions = root.FindViewById(Resource.Id.chat_request_slot_approval_actions)!;
        _questionActions = root.FindViewById(Resource.Id.chat_request_slot_question_actions)!;
        _holdBar = root.FindViewById(Resource.Id.chat_request_slot_hold_bar)!;
        _holdText = root.FindViewById<TextView>(Resource.Id.chat_request_slot_hold_text)!;
        _stateText = root.FindViewById<TextView>(Resource.Id.chat_request_slot_state)!;

        _switchRow.Click += (_, _) => SelectNextCard();

        root.FindViewById<MaterialButton>(Resource.Id.chat_request_slot_reject)!.Click += (_, _) =>
        {
            if (_selectedEventId is not null)
            {
                _onApproval(_selectedEventId, ApprovalDecision.Rejected);
            }
        };
        root.FindViewById<MaterialButton>(Resource.Id.chat_request_slot_allow)!.Click += (_, _) =>
        {
            if (_selectedEventId is not null)
            {
                _onApproval(_selectedEventId, ApprovalDecision.AllowedOnce);
            }
        };
        root.FindViewById<MaterialButton>(Resource.Id.chat_request_slot_undo)!.Click += (_, _) =>
        {
            if (_selectedEventId is not null)
            {
                _onUndo(_selectedEventId);
            }
        };
        root.FindViewById<MaterialButton>(Resource.Id.chat_request_slot_question_cancel)!.Click += (_, _) =>
        {
            if (_selectedEventId is not null)
            {
                _onCancelQuestion(_selectedEventId);
            }
        };
        root.FindViewById<MaterialButton>(Resource.Id.chat_request_slot_question_submit)!.Click += (_, _) =>
        {
            if (_selectedEventId is not null)
            {
                _onAnswer(_selectedEventId, BuildAnswerBatch());
            }
        };
    }

    /// <summary>
    /// 绑定当前会话的待处理请求；<paramref name="focusEventId"/> 用于通知 / 收件箱跳转时直接聚焦对应卡片。
    /// </summary>
    public void Bind(IReadOnlyList<PendingInteractionCard> cards, string? focusEventId)
    {
        var visible = new List<PendingInteractionCard>(cards.Count);
        foreach (var card in cards)
        {
            if (card.State is not (PendingRequestState.Settled or PendingRequestState.Cancelled))
            {
                visible.Add(card);
            }
        }

        _cards = visible;
        if (visible.Count == 0)
        {
            _root.Visibility = ViewStates.Gone;
            return;
        }

        _root.Visibility = ViewStates.Visible;
        var focusIndex = string.IsNullOrWhiteSpace(focusEventId)
            ? -1
            : visible.FindIndex(c => string.Equals(c.EventId, focusEventId, StringComparison.Ordinal));
        if (focusIndex >= 0)
        {
            _selectedIndex = focusIndex;
        }
        else if (_selectedEventId is { } previous)
        {
            var kept = visible.FindIndex(c => string.Equals(c.EventId, previous, StringComparison.Ordinal));
            _selectedIndex = kept >= 0 ? kept : Math.Clamp(_selectedIndex, 0, visible.Count - 1);
        }
        else
        {
            _selectedIndex = Math.Clamp(_selectedIndex, 0, visible.Count - 1);
        }

        _selectedEventId = visible[_selectedIndex].EventId;
        RenderSwitchRow(visible.Count);
        BindCard(visible[_selectedIndex]);
        ApplyScrollHeightLimit();
    }

    private void SelectNextCard()
    {
        if (_cards.Count <= 1)
        {
            return;
        }
        _selectedIndex = (_selectedIndex + 1) % _cards.Count;
        _selectedEventId = _cards[_selectedIndex].EventId;
        RenderSwitchRow(_cards.Count);
        BindCard(_cards[_selectedIndex]);
        ApplyScrollHeightLimit();
    }

    private void RenderSwitchRow(int count)
    {
        if (count <= 1)
        {
            _switchRow.Visibility = ViewStates.Gone;
            return;
        }

        _switchRow.Visibility = ViewStates.Visible;
        _switchText.Text = Rows.Fmt(
            _root.Context!,
            Resource.String.chat_request_slot_switch_fmt,
            _selectedIndex + 1,
            count);
    }

    private void BindCard(PendingInteractionCard card)
    {
        var ctx = _root.Context!;
        var eventChanged = !string.Equals(_boundEventId, card.EventId, StringComparison.Ordinal);
        _boundEventId = card.EventId;
        if (eventChanged)
        {
            // 切换到另一张卡时回到内容顶部，避免沿用上一张卡片的滚动位置。
            _scroll.ScrollTo(0, 0);
        }
        var isApproval = card.Kind == PendingRequestKind.Approval;
        var toolName = string.IsNullOrWhiteSpace(card.ToolName) ? "tool" : card.ToolName!;

        _status.Text = isApproval
            ? Rows.Fmt(ctx, Resource.String.chat_request_slot_waiting_approval_fmt, toolName)
            : ctx.GetString(Resource.String.chat_request_slot_waiting_question);
        _description.Text = isApproval
            ? Rows.Fmt(ctx, Resource.String.chat_request_slot_desc_approval_fmt, toolName)
            : ctx.GetString(Resource.String.chat_request_slot_desc_question);

        _missingWarning.Visibility = isApproval && !card.ArgumentsReady
            ? ViewStates.Visible
            : ViewStates.Gone;

        var summary = isApproval ? card.SanitizedArgumentsSummary : null;
        if (!string.IsNullOrWhiteSpace(summary))
        {
            _args.Visibility = ViewStates.Visible;
            _args.Text = summary;
        }
        else
        {
            _args.Visibility = ViewStates.Gone;
            _args.Text = string.Empty;
        }

        var isQuestion = !isApproval;
        _questions.Visibility = isQuestion ? ViewStates.Visible : ViewStates.Gone;
        if (isQuestion)
        {
            BindQuestions(card);
        }
        else
        {
            _questions.RemoveAllViews();
            _questionInputs.Clear();
            _boundQuestions = null;
        }

        var waiting = card.State == PendingRequestState.Waiting;
        _approvalActions.Visibility = isApproval && waiting ? ViewStates.Visible : ViewStates.Gone;
        _questionActions.Visibility = isQuestion && waiting ? ViewStates.Visible : ViewStates.Gone;

        _holdBar.Visibility = isApproval && card.State == PendingRequestState.Held
            ? ViewStates.Visible
            : ViewStates.Gone;
        if (card.State == PendingRequestState.Held)
        {
            _holdText.SetText(card.HeldDecision == ApprovalDecision.AllowedOnce
                ? Resource.String.chat_approval_held_allow
                : Resource.String.chat_approval_held_reject);
        }

        if (card.State is PendingRequestState.Sending or PendingRequestState.TransportLost)
        {
            _stateText.Visibility = ViewStates.Visible;
            if (card.State == PendingRequestState.TransportLost)
            {
                _stateText.SetText(Resource.String.chat_approval_lost);
            }
            else
            {
                _stateText.SetText(isApproval
                    ? Resource.String.chat_approval_sending
                    : Resource.String.chat_question_sending);
            }
        }
        else
        {
            _stateText.Visibility = ViewStates.Gone;
            _stateText.Text = string.Empty;
        }
    }

    private void BindQuestions(PendingInteractionCard card)
    {
        var questions = card.Questions ?? [];
        if (_boundEventId == card.EventId && QuestionsEqual(_boundQuestions, questions))
        {
            return;
        }

        _questions.RemoveAllViews();
        _questionInputs.Clear();
        _boundQuestions = questions;

        var inflater = LayoutInflater.From(_root.Context)!;
        foreach (var question in questions)
        {
            var row = inflater.Inflate(Resource.Layout.item_chat_question_item, _questions, false)!;
            row.FindViewById<TextView>(Resource.Id.question_item_prompt)!.Text = question.Question;
            var detail = row.FindViewById<TextView>(Resource.Id.question_item_detail)!;
            if (string.IsNullOrWhiteSpace(question.Detail))
            {
                detail.Visibility = ViewStates.Gone;
            }
            else
            {
                detail.Visibility = ViewStates.Visible;
                detail.Text = question.Detail;
            }

            var chipGroup = row.FindViewById<ChipGroup>(Resource.Id.question_item_options)!;
            chipGroup.SingleSelection = !question.MultiSelect;
            foreach (var option in question.Options)
            {
                var chip = (Chip)inflater.Inflate(Resource.Layout.item_chat_question_option, chipGroup, false)!;
                chip.Text = option.Label;
                chipGroup.AddView(chip);
            }

            var custom = row.FindViewById<TextInputEditText>(Resource.Id.question_item_custom)!;
            _questions.AddView(row);
            _questionInputs.Add(new QuestionInputBinding(question.Id, chipGroup, custom));
        }
    }

    private UserQuestionAnswerBatch BuildAnswerBatch()
    {
        var answers = new List<UserQuestionAnswerItemView>(_questionInputs.Count);
        foreach (var binding in _questionInputs)
        {
            var selected = new List<string>();
            for (var i = 0; i < binding.Options.ChildCount; i++)
            {
                if (binding.Options.GetChildAt(i) is Chip { Checked: true } chip
                    && !string.IsNullOrEmpty(chip.Text))
                {
                    selected.Add(chip.Text);
                }
            }
            var customText = string.IsNullOrWhiteSpace(binding.Custom.Text)
                ? null
                : binding.Custom.Text!.Trim();
            answers.Add(new UserQuestionAnswerItemView(binding.QuestionId, selected, customText));
        }
        return new UserQuestionAnswerBatch(answers);
    }

    /// <summary>中间滚动区最大高度限制为当前窗口高度的 40%，保证固定按钮行永远留在屏幕内。</summary>
    private void ApplyScrollHeightLimit()
    {
        var maxHeight = ResolveMaxScrollHeight();
        if (maxHeight <= 0)
        {
            return;
        }

        if (_scroll.LayoutParameters is { } layoutParams && layoutParams.Height != maxHeight)
        {
            layoutParams.Height = maxHeight;
            _scroll.LayoutParameters = layoutParams;
        }

        _scroll.Post(() =>
        {
            if (_scroll.GetChildAt(0) is not { } content || content.Height <= 0)
            {
                return;
            }

            var target = Math.Min(content.Height, maxHeight);
            if (_scroll.LayoutParameters is { } current && current.Height != target)
            {
                current.Height = target;
                _scroll.LayoutParameters = current;
            }
        });
    }

    private int ResolveMaxScrollHeight()
    {
        var windowHeight = _root.RootView?.Height ?? 0;
        if (windowHeight <= 0)
        {
            windowHeight = _root.Resources?.DisplayMetrics?.HeightPixels ?? 0;
        }
        return windowHeight <= 0 ? 0 : (int)Math.Round(windowHeight * 0.40f);
    }

    private static bool QuestionsEqual(
        IReadOnlyList<UserQuestionItemView>? left,
        IReadOnlyList<UserQuestionItemView>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }
        for (var i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }
        return true;
    }

    private sealed record QuestionInputBinding(
        string QuestionId,
        ChipGroup Options,
        TextInputEditText Custom);
}
