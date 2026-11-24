using Android.Content.Res;
using Android.Graphics;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;
using DshMobile.Agent.Presentation;
using DshMobile.Agent.Rendering;
using DshMobile.App.Ui;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Google.Android.Material.TextField;

namespace DshMobile.App.Chat;

/// <summary>聊天时间线列表项统一封装（会话行 + 审批/问答卡片），含稳定 <see cref="ItemId"/>。</summary>
internal abstract record ChatTimelineItem(string ItemId);

internal sealed record ChatRowTimelineItem(ChatRow Row, long SnapshotRevision)
    : ChatTimelineItem(Row.ItemId);

internal sealed record PendingRequestTimelineItem(PendingInteractionCard Card)
    : ChatTimelineItem($"req:{Card.EventId}");

/// <summary>
/// 多 <c>ViewType</c> 原生聊天列表适配器（N-04-03）：
/// 使用 XML 模板（<c>item_chat_*.xml</c>）与 <see cref="DiffUtil"/> 局部刷新，支持流式 Markdown 后台异步解析与版本守卫。
/// </summary>
internal sealed class ChatTimelineAdapter : RecyclerView.Adapter
{
    private const int TypeUser = 1;
    private const int TypeAssistantMarkdown = 2;
    private const int TypeReasoning = 3;
    private const int TypeToolCall = 4;
    private const int TypeCommand = 5;
    private const int TypeNotice = 6;
    private const int TypeApproval = 7;
    private const int TypeQuestion = 8;

    private readonly MarkdownRenderCache _mdCache;
    private readonly Action<Action> _postToUi;
    private readonly Action<string, ApprovalDecision> _onApproval;
    private readonly Action<string> _onUndoApproval;
    private readonly Action<string, UserQuestionAnswerBatch> _onAnswerQuestion;
    private readonly Action<string> _onCancelQuestion;
    private readonly Action<string, bool>? _onOpenWorkspaceFile;
    private readonly HashSet<string> _expandedItems = new(StringComparer.Ordinal);
    private List<ChatTimelineItem> _items = [];

    public ChatTimelineAdapter(
        MarkdownRenderCache mdCache,
        Action<Action> postToUi,
        Action<string, ApprovalDecision> onApproval,
        Action<string> onUndoApproval,
        Action<string, UserQuestionAnswerBatch> onAnswerQuestion,
        Action<string> onCancelQuestion,
        Action<string, bool>? onOpenWorkspaceFile = null)
    {
        _mdCache = mdCache;
        _postToUi = postToUi;
        _onApproval = onApproval;
        _onUndoApproval = onUndoApproval;
        _onAnswerQuestion = onAnswerQuestion;
        _onCancelQuestion = onCancelQuestion;
        _onOpenWorkspaceFile = onOpenWorkspaceFile;
        HasStableIds = true;
    }

    public int FindPendingRequestPosition(string eventId) =>
        _items.FindIndex(i => i is PendingRequestTimelineItem p && string.Equals(p.Card.EventId, eventId, StringComparison.Ordinal));

    public override int ItemCount => _items.Count;

    public override long GetItemId(int position) =>
        StableHash64(_items[position].ItemId);

    public override int GetItemViewType(int position) => _items[position] switch
    {
        ChatRowTimelineItem { Row: UserMessageRow } => TypeUser,
        ChatRowTimelineItem { Row: MarkdownBlockRow } => TypeAssistantMarkdown,
        ChatRowTimelineItem { Row: ReasoningBlockRow } => TypeReasoning,
        ChatRowTimelineItem { Row: ToolCallRow } => TypeToolCall,
        ChatRowTimelineItem { Row: CommandRow } => TypeCommand,
        PendingRequestTimelineItem { Card.Kind: PendingRequestKind.Approval } => TypeApproval,
        PendingRequestTimelineItem { Card.Kind: PendingRequestKind.UserQuestion } => TypeQuestion,
        _ => TypeNotice,
    };

    public void Submit(IReadOnlyList<ChatTimelineItem> nextItems)
    {
        var oldList = _items;
        var nextList = nextItems.ToList();
        var diff = DiffUtil.CalculateDiff(new TimelineDiffCallback(oldList, nextList));
        _items = nextList;
        diff.DispatchUpdatesTo(this);
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var inflater = LayoutInflater.From(parent.Context)!;
        return viewType switch
        {
            TypeUser => new UserMessageHolder(inflater.Inflate(Resource.Layout.item_chat_user, parent, false)!),
            TypeAssistantMarkdown => new AssistantBlockHolder(inflater.Inflate(Resource.Layout.item_chat_assistant_markdown, parent, false)!, _mdCache, _postToUi),
            TypeReasoning => new ReasoningHolder(inflater.Inflate(Resource.Layout.item_chat_reasoning, parent, false)!, _expandedItems),
            TypeToolCall => new ToolCallHolder(inflater.Inflate(Resource.Layout.item_chat_tool_call, parent, false)!, _expandedItems, _onOpenWorkspaceFile),
            TypeCommand => new CommandHolder(inflater.Inflate(Resource.Layout.item_chat_command, parent, false)!),
            TypeApproval => new ApprovalHolder(inflater.Inflate(Resource.Layout.item_chat_approval, parent, false)!, _onApproval, _onUndoApproval),
            TypeQuestion => new QuestionHolder(inflater.Inflate(Resource.Layout.item_chat_question, parent, false)!, _onAnswerQuestion, _onCancelQuestion),
            _ => new NoticeHolder(inflater.Inflate(Resource.Layout.item_chat_notice, parent, false)!),
        };
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var item = _items[position];
        switch (holder)
        {
            case UserMessageHolder h when item is ChatRowTimelineItem { Row: UserMessageRow u }:
                h.Bind(u);
                break;
            case AssistantBlockHolder h when item is ChatRowTimelineItem { Row: MarkdownBlockRow m } r:
                var showAvatar = position == 0 || _items[position - 1] is not ChatRowTimelineItem { Row: MarkdownBlockRow prev } || prev.MessageId != m.MessageId;
                h.Bind(m, r.SnapshotRevision, showAvatar);
                break;
            case ReasoningHolder h when item is ChatRowTimelineItem { Row: ReasoningBlockRow r }:
                h.Bind(r);
                break;
            case ToolCallHolder h when item is ChatRowTimelineItem { Row: ToolCallRow t }:
                h.Bind(t);
                break;
            case CommandHolder h when item is ChatRowTimelineItem { Row: CommandRow c }:
                h.Bind(c);
                break;
            case NoticeHolder h when item is ChatRowTimelineItem { Row: NoticeRow n }:
                h.BindNotice(n);
                break;
            case NoticeHolder h when item is ChatRowTimelineItem { Row: UnknownEventRow u }:
                h.BindUnknown(u);
                break;
            case ApprovalHolder h when item is PendingRequestTimelineItem { Card: var card }:
                h.Bind(card);
                break;
            case QuestionHolder h when item is PendingRequestTimelineItem { Card: var card }:
                h.Bind(card);
                break;
        }
    }

    private static long StableHash64(string value)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }
        return unchecked((long)hash);
    }

    private sealed class TimelineDiffCallback(
        IReadOnlyList<ChatTimelineItem> oldItems,
        IReadOnlyList<ChatTimelineItem> newItems) : DiffUtil.Callback
    {
        public override int OldListSize => oldItems.Count;
        public override int NewListSize => newItems.Count;

        public override bool AreItemsTheSame(int oldItemPosition, int newItemPosition) =>
            string.Equals(oldItems[oldItemPosition].ItemId, newItems[newItemPosition].ItemId, StringComparison.Ordinal);

        public override bool AreContentsTheSame(int oldItemPosition, int newItemPosition)
        {
            var oldItem = oldItems[oldItemPosition];
            var newItem = newItems[newItemPosition];
            if (oldItem is ChatRowTimelineItem oRow && newItem is ChatRowTimelineItem nRow)
            {
                return oRow.Row == nRow.Row;
            }
            if (oldItem is PendingRequestTimelineItem oReq && newItem is PendingRequestTimelineItem nReq)
            {
                return oReq.Card == nReq.Card;
            }
            return false;
        }
    }

    private sealed class UserMessageHolder(View itemView) : RecyclerView.ViewHolder(itemView)
    {
        private readonly TextView _text = itemView.FindViewById<TextView>(Resource.Id.chat_user_text)!;
        private readonly TextView _meta = itemView.FindViewById<TextView>(Resource.Id.chat_user_meta)!;

        public void Bind(UserMessageRow row)
        {
            _text.Text = row.Text;
            var ctx = ItemView.Context!;
            if (row.IsPending)
            {
                _meta.Visibility = ViewStates.Visible;
                _meta.SetText(Resource.String.chat_user_pending);
            }
            else if (row.ImageCount > 0)
            {
                _meta.Visibility = ViewStates.Visible;
                _meta.Text = Rows.Fmt(ctx, Resource.String.chat_user_images_badge, row.ImageCount);
            }
            else
            {
                _meta.Visibility = ViewStates.Gone;
            }
        }
    }

    private sealed class AssistantBlockHolder(
        View itemView,
        MarkdownRenderCache mdCache,
        Action<Action> postToUi) : RecyclerView.ViewHolder(itemView)
    {
        private readonly ImageView _avatar = itemView.FindViewById<ImageView>(Resource.Id.chat_assistant_avatar)!;
        private readonly LinearLayout _segments = itemView.FindViewById<LinearLayout>(Resource.Id.chat_assistant_segments)!;
        private string? _boundItemId;
        private long _boundRevision;
        private string? _boundFingerprint;
        private CancellationTokenSource? _parseCts;

        public void Bind(MarkdownBlockRow row, long revision, bool showAvatar)
        {
            _avatar.Visibility = showAvatar ? ViewStates.Visible : ViewStates.Invisible;
            var fp = MarkdownRenderCache.ComputeFingerprint(row.Markdown);
            if (_boundItemId == row.ItemId && _boundFingerprint == fp)
            {
                return;
            }

            _parseCts?.Cancel();
            _parseCts?.Dispose();
            _parseCts = null;

            _boundItemId = row.ItemId;
            _boundRevision = revision;
            _boundFingerprint = fp;

            if (mdCache.TryGetCached(row.ItemId, row.Markdown, revision, out var cached) && cached is not null)
            {
                ChatMarkdownBinder.BindSegments(_segments, cached.Segments, LayoutInflater.From(ItemView.Context)!);
                return;
            }

            // 先轻量占位文本，后台解析完成后按单调版本号防回写覆盖。
            var inflater = LayoutInflater.From(ItemView.Context)!;
            _segments.RemoveAllViews();
            var fallback = (TextView)inflater.Inflate(Resource.Layout.item_chat_md_text, _segments, false)!;
            fallback.Text = row.Markdown;
            _segments.AddView(fallback);

            var cts = new CancellationTokenSource();
            _parseCts = cts;
            var capturedItemId = row.ItemId;
            var capturedRev = revision;
            _ = Task.Run(async () =>
            {
                var parsed = await mdCache.ParseAsync(capturedItemId, capturedRev, row.Markdown, cts.Token).ConfigureAwait(false);
                if (parsed is null || cts.IsCancellationRequested)
                {
                    return;
                }
                postToUi(() =>
                {
                    if (_boundItemId != capturedItemId || _boundRevision != capturedRev || mdCache.IsStale(capturedItemId, capturedRev))
                    {
                        return;
                    }
                    ChatMarkdownBinder.BindSegments(_segments, parsed.Segments, LayoutInflater.From(ItemView.Context)!);
                });
            }, cts.Token);
        }
    }

    private sealed class ReasoningHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _body;
        private readonly ImageView _chevron;
        private readonly HashSet<string> _expanded;
        private string? _itemId;

        public ReasoningHolder(View itemView, HashSet<string> expanded) : base(itemView)
        {
            _expanded = expanded;
            _body = itemView.FindViewById<TextView>(Resource.Id.chat_reasoning_body)!;
            _chevron = itemView.FindViewById<ImageView>(Resource.Id.chat_reasoning_chevron)!;
            itemView.FindViewById(Resource.Id.chat_reasoning_header)!.Click += (_, _) =>
            {
                if (_itemId is null)
                {
                    return;
                }
                if (!_expanded.Add(_itemId))
                {
                    _expanded.Remove(_itemId);
                }
                ApplyExpanded(_expanded.Contains(_itemId));
            };
        }

        public void Bind(ReasoningBlockRow row)
        {
            _itemId = row.ItemId;
            _body.Text = row.Text;
            ApplyExpanded(_expanded.Contains(row.ItemId));
        }

        private void ApplyExpanded(bool isOpen)
        {
            _body.Visibility = isOpen ? ViewStates.Visible : ViewStates.Gone;
            _chevron.Rotation = isOpen ? 180f : 0f;
        }
    }

    private sealed class ToolCallHolder : RecyclerView.ViewHolder
    {
        private readonly ImageView _icon;
        private readonly TextView _title;
        private readonly TextView _phaseText;
        private readonly ImageView _statusIcon;
        private readonly ImageView _chevron;
        private readonly TextView _args;
        private readonly TextView _detailJson;
        private readonly TextView _output;
        private readonly View _workspaceBar;
        private readonly MaterialButton _openFileBtn;
        private readonly MaterialButton _openDiffBtn;
        private readonly HashSet<string> _expanded;
        private string? _itemId;
        private string? _workspacePath;
        private bool _hasDetailJson;
        private bool _hasOutput;

        public ToolCallHolder(
            View itemView,
            HashSet<string> expanded,
            Action<string, bool>? onOpenWorkspaceFile) : base(itemView)
        {
            _expanded = expanded;
            _icon = itemView.FindViewById<ImageView>(Resource.Id.chat_tool_icon)!;
            _title = itemView.FindViewById<TextView>(Resource.Id.chat_tool_title)!;
            _phaseText = itemView.FindViewById<TextView>(Resource.Id.chat_tool_phase_text)!;
            _statusIcon = itemView.FindViewById<ImageView>(Resource.Id.chat_tool_status_icon)!;
            _chevron = itemView.FindViewById<ImageView>(Resource.Id.chat_tool_chevron)!;
            _args = itemView.FindViewById<TextView>(Resource.Id.chat_tool_args)!;
            _detailJson = itemView.FindViewById<TextView>(Resource.Id.chat_tool_detail_json)!;
            _output = itemView.FindViewById<TextView>(Resource.Id.chat_tool_output)!;
            _workspaceBar = itemView.FindViewById(Resource.Id.chat_tool_workspace_bar)!;
            _openFileBtn = itemView.FindViewById<MaterialButton>(Resource.Id.chat_tool_open_file_btn)!;
            _openDiffBtn = itemView.FindViewById<MaterialButton>(Resource.Id.chat_tool_open_diff_btn)!;

            void ToggleExpand()
            {
                if (_itemId is null)
                {
                    return;
                }
                if (!_expanded.Add(_itemId))
                {
                    _expanded.Remove(_itemId);
                }
                ApplyExpanded(_expanded.Contains(_itemId));
            }

            itemView.FindViewById(Resource.Id.chat_tool_header)!.Click += (_, _) => ToggleExpand();
            _args.Click += (_, _) => ToggleExpand();

            _openFileBtn.Click += (_, _) =>
            {
                if (_workspacePath is { } path)
                {
                    onOpenWorkspaceFile?.Invoke(path, false);
                }
            };
            _openDiffBtn.Click += (_, _) =>
            {
                if (_workspacePath is { } path)
                {
                    onOpenWorkspaceFile?.Invoke(path, true);
                }
            };
        }

        public void Bind(ToolCallRow row)
        {
            _itemId = row.ItemId;
            var ctx = ItemView.Context!;
            var summary = ChatDisplayFormatter.FormatToolCallSummary(row.ToolName, row.ArgumentsJson);
            _title.Text = summary.HeaderLabel;
            _icon.SetImageResource(ResolveToolIcon(row.ToolName));

            if (string.IsNullOrWhiteSpace(summary.SummaryLine))
            {
                _args.Visibility = ViewStates.Gone;
                _args.Text = string.Empty;
            }
            else
            {
                _args.Visibility = ViewStates.Visible;
                _args.Text = summary.SummaryLine;
            }

            _hasDetailJson = !string.IsNullOrWhiteSpace(summary.DetailJson)
                && !string.Equals(summary.DetailJson, summary.SummaryLine, StringComparison.Ordinal);
            _detailJson.Text = summary.DetailJson ?? string.Empty;

            _hasOutput = !string.IsNullOrWhiteSpace(row.OutputSummary);
            _output.Text = row.OutputSummary ?? string.Empty;

            _workspacePath = WorkspacePathExtractor.TryExtractWorkspaceFilePath(row.ArgumentsJson);
            if (_workspacePath is { } safePath)
            {
                _workspaceBar.Visibility = ViewStates.Visible;
                _openFileBtn.Text = safePath;
                _openDiffBtn.Visibility = ViewStates.Visible;
            }
            else
            {
                _workspaceBar.Visibility = ViewStates.Gone;
            }

            var (phaseRes, iconRes, tintColor) = row.Phase switch
            {
                ToolPhase.Completed => (Resource.String.chat_tool_status_completed, Resource.Drawable.ic_check_circle, new Color(ctx.GetColor(Resource.Color.dshm_running))),
                ToolPhase.Error => (Resource.String.chat_tool_status_error, Resource.Drawable.ic_error, ResolveAttrColor(ctx, Resource.Attribute.colorError)),
                ToolPhase.Cancelled => (Resource.String.chat_tool_status_cancelled, Resource.Drawable.ic_close, ResolveAttrColor(ctx, Resource.Attribute.colorOutline)),
                ToolPhase.Running => (Resource.String.chat_tool_status_running, Resource.Drawable.ic_refresh, ResolveAttrColor(ctx, Resource.Attribute.colorPrimary)),
                _ => (Resource.String.chat_tool_status_pending, Resource.Drawable.ic_history, ResolveAttrColor(ctx, Resource.Attribute.colorOutline)),
            };
            _phaseText.SetText(phaseRes);
            _statusIcon.SetImageResource(iconRes);
            _statusIcon.ImageTintList = ColorStateList.ValueOf(tintColor);

            ApplyExpanded(_expanded.Contains(row.ItemId));
        }

        private void ApplyExpanded(bool isOpen)
        {
            _args.SetMaxLines(isOpen ? 6 : 2);
            _args.Ellipsize = isOpen ? null : Android.Text.TextUtils.TruncateAt.End;
            _detailJson.Visibility = isOpen && _hasDetailJson ? ViewStates.Visible : ViewStates.Gone;
            _output.Visibility = isOpen && _hasOutput ? ViewStates.Visible : ViewStates.Gone;
            _chevron.Rotation = isOpen ? 180f : 0f;
        }

        private static int ResolveToolIcon(string toolName)
        {
            var lower = toolName.Trim().ToLowerInvariant();
            if (lower.Contains("bash", StringComparison.Ordinal) || lower.Contains("shell", StringComparison.Ordinal) || lower.Contains("exec", StringComparison.Ordinal) || lower == "sh")
            {
                return Resource.Drawable.ic_terminal;
            }
            if (lower.Contains("read", StringComparison.Ordinal) || lower.Contains("view", StringComparison.Ordinal) || lower.Contains("cat", StringComparison.Ordinal))
            {
                return Resource.Drawable.ic_article;
            }
            if (lower.Contains("edit", StringComparison.Ordinal) || lower.Contains("write", StringComparison.Ordinal) || lower.Contains("patch", StringComparison.Ordinal))
            {
                return Resource.Drawable.ic_code;
            }
            if (lower.Contains("search", StringComparison.Ordinal) || lower.Contains("grep", StringComparison.Ordinal) || lower.Contains("glob", StringComparison.Ordinal) || lower.Contains("find", StringComparison.Ordinal))
            {
                return Resource.Drawable.ic_search;
            }
            return Resource.Drawable.ic_build;
        }
    }

    private sealed class CommandHolder(View itemView) : RecyclerView.ViewHolder(itemView)
    {
        private readonly TextView _title = itemView.FindViewById<TextView>(Resource.Id.chat_command_title)!;
        private readonly TextView _result = itemView.FindViewById<TextView>(Resource.Id.chat_command_result)!;

        public void Bind(CommandRow row)
        {
            _title.Text = string.IsNullOrWhiteSpace(row.Args) ? row.Name : $"{row.Name} {row.Args}";
            if (string.IsNullOrWhiteSpace(row.ResultText))
            {
                _result.Visibility = ViewStates.Gone;
            }
            else
            {
                _result.Visibility = ViewStates.Visible;
                _result.Text = row.ResultText;
            }
        }
    }

    private sealed class NoticeHolder(View itemView) : RecyclerView.ViewHolder(itemView)
    {
        private readonly ImageView _icon = itemView.FindViewById<ImageView>(Resource.Id.chat_notice_icon)!;
        private readonly TextView _text = itemView.FindViewById<TextView>(Resource.Id.chat_notice_text)!;

        public void BindNotice(NoticeRow row)
        {
            var ctx = ItemView.Context!;
            _icon.SetImageResource(row.IsError ? Resource.Drawable.ic_error : Resource.Drawable.ic_info);
            _icon.ImageTintList = ColorStateList.ValueOf(ResolveAttrColor(ctx, row.IsError ? Resource.Attribute.colorError : Resource.Attribute.colorPrimary));
            _text.Text = $"{row.Code}: {row.DiagnosticKey}";
        }

        public void BindUnknown(UnknownEventRow row)
        {
            var ctx = ItemView.Context!;
            _icon.SetImageResource(Resource.Drawable.ic_info);
            _icon.ImageTintList = ColorStateList.ValueOf(ResolveAttrColor(ctx, Resource.Attribute.colorOutline));
            _text.Text = $"[{row.EventType}]";
        }
    }

    private sealed class ApprovalHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _subtitle;
        private readonly TextView _missingWarning;
        private readonly TextView _args;
        private readonly View _actions;
        private readonly View _holdBar;
        private readonly TextView _holdText;
        private readonly TextView _status;
        private string? _eventId;

        public ApprovalHolder(
            View itemView,
            Action<string, ApprovalDecision> onApproval,
            Action<string> onUndo) : base(itemView)
        {
            _subtitle = itemView.FindViewById<TextView>(Resource.Id.chat_approval_subtitle)!;
            _missingWarning = itemView.FindViewById<TextView>(Resource.Id.chat_approval_missing_warning)!;
            _args = itemView.FindViewById<TextView>(Resource.Id.chat_approval_args)!;
            _actions = itemView.FindViewById(Resource.Id.chat_approval_actions)!;
            _holdBar = itemView.FindViewById(Resource.Id.chat_approval_hold_bar)!;
            _holdText = itemView.FindViewById<TextView>(Resource.Id.chat_approval_hold_text)!;
            _status = itemView.FindViewById<TextView>(Resource.Id.chat_approval_status)!;

            itemView.FindViewById<MaterialButton>(Resource.Id.chat_approval_reject)!.Click += (_, _) =>
            {
                if (_eventId is not null)
                {
                    onApproval(_eventId, ApprovalDecision.Rejected);
                }
            };
            itemView.FindViewById<MaterialButton>(Resource.Id.chat_approval_allow)!.Click += (_, _) =>
            {
                if (_eventId is not null)
                {
                    onApproval(_eventId, ApprovalDecision.AllowedOnce);
                }
            };
            itemView.FindViewById<MaterialButton>(Resource.Id.chat_approval_undo)!.Click += (_, _) =>
            {
                if (_eventId is not null)
                {
                    onUndo(_eventId);
                }
            };
        }

        public void Bind(PendingInteractionCard card)
        {
            _eventId = card.EventId;
            var ctx = ItemView.Context!;
            _subtitle.Text = Rows.Fmt(ctx, Resource.String.chat_approval_subtitle, card.ToolName ?? "tool");
            _missingWarning.Visibility = card.ArgumentsReady ? ViewStates.Gone : ViewStates.Visible;
            if (!string.IsNullOrWhiteSpace(card.SanitizedArgumentsSummary))
            {
                _args.Visibility = ViewStates.Visible;
                _args.Text = card.SanitizedArgumentsSummary;
            }
            else
            {
                _args.Visibility = ViewStates.Gone;
            }

            _actions.Visibility = card.State == PendingRequestState.Waiting ? ViewStates.Visible : ViewStates.Gone;
            _holdBar.Visibility = card.State == PendingRequestState.Held ? ViewStates.Visible : ViewStates.Gone;
            if (card.State == PendingRequestState.Held)
            {
                _holdText.SetText(card.HeldDecision == ApprovalDecision.AllowedOnce
                    ? Resource.String.chat_approval_held_allow
                    : Resource.String.chat_approval_held_reject);
            }

            if (card.State is PendingRequestState.Sending or PendingRequestState.TransportLost)
            {
                _status.Visibility = ViewStates.Visible;
                _status.SetText(card.State == PendingRequestState.Sending
                    ? Resource.String.chat_approval_sending
                    : Resource.String.chat_approval_lost);
            }
            else
            {
                _status.Visibility = ViewStates.Gone;
            }
        }
    }

    private sealed class QuestionHolder : RecyclerView.ViewHolder
    {
        private readonly LinearLayout _itemsContainer;
        private readonly View _actions;
        private readonly List<(UserQuestionItemView Item, ChipGroup Options, TextInputEditText Custom)> _boundInputs = [];
        private string? _eventId;

        public QuestionHolder(
            View itemView,
            Action<string, UserQuestionAnswerBatch> onAnswer,
            Action<string> onCancel) : base(itemView)
        {
            _itemsContainer = itemView.FindViewById<LinearLayout>(Resource.Id.chat_question_items)!;
            _actions = itemView.FindViewById(Resource.Id.chat_question_actions)!;

            itemView.FindViewById<MaterialButton>(Resource.Id.chat_question_cancel)!.Click += (_, _) =>
            {
                if (_eventId is not null)
                {
                    onCancel(_eventId);
                }
            };
            itemView.FindViewById<MaterialButton>(Resource.Id.chat_question_submit)!.Click += (_, _) =>
            {
                if (_eventId is null)
                {
                    return;
                }
                var answers = new List<UserQuestionAnswerItemView>(_boundInputs.Count);
                foreach (var (q, group, custom) in _boundInputs)
                {
                    var selected = new List<string>();
                    for (var i = 0; i < group.ChildCount; i++)
                    {
                        if (group.GetChildAt(i) is Chip { Checked: true } chip && !string.IsNullOrEmpty(chip.Text))
                        {
                            selected.Add(chip.Text);
                        }
                    }
                    var customText = string.IsNullOrWhiteSpace(custom.Text) ? null : custom.Text.Trim();
                    answers.Add(new UserQuestionAnswerItemView(q.Id, selected, customText));
                }
                onAnswer(_eventId, new UserQuestionAnswerBatch(answers));
            };
        }

        public void Bind(PendingInteractionCard card)
        {
            _eventId = card.EventId;
            _itemsContainer.RemoveAllViews();
            _boundInputs.Clear();

            var inflater = LayoutInflater.From(ItemView.Context)!;
            foreach (var q in card.Questions ?? [])
            {
                var row = inflater.Inflate(Resource.Layout.item_chat_question_item, _itemsContainer, false)!;
                row.FindViewById<TextView>(Resource.Id.question_item_prompt)!.Text = q.Question;
                var detail = row.FindViewById<TextView>(Resource.Id.question_item_detail)!;
                if (string.IsNullOrWhiteSpace(q.Detail))
                {
                    detail.Visibility = ViewStates.Gone;
                }
                else
                {
                    detail.Visibility = ViewStates.Visible;
                    detail.Text = q.Detail;
                }

                var chipGroup = row.FindViewById<ChipGroup>(Resource.Id.question_item_options)!;
                chipGroup.SingleSelection = !q.MultiSelect;
                foreach (var opt in q.Options)
                {
                    var chip = (Chip)inflater.Inflate(Resource.Layout.item_chat_question_option, chipGroup, false)!;
                    chip.Text = opt.Label;
                    chipGroup.AddView(chip);
                }

                var custom = row.FindViewById<TextInputEditText>(Resource.Id.question_item_custom)!;
                _itemsContainer.AddView(row);
                _boundInputs.Add((q, chipGroup, custom));
            }

            _actions.Visibility = card.State == PendingRequestState.Waiting ? ViewStates.Visible : ViewStates.Gone;
        }
    }

    private static Color ResolveAttrColor(Android.Content.Context ctx, int attrId)
    {
        var tv = new TypedValue();
        return ctx.Theme?.ResolveAttribute(attrId, tv, true) == true
            ? new Color(tv.Data)
            : new Color(ctx.GetColor(Android.Resource.Color.Black));
    }
}
