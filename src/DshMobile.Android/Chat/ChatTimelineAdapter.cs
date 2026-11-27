using System.Globalization;
using Android.Animation;
using Android.Content;
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
using Google.Android.Material.Dialog;
using Google.Android.Material.Snackbar;

namespace DshMobile.App.Chat;

/// <summary>聊天时间线列表项统一封装（会话行），含稳定 <see cref="ItemId"/>。</summary>
internal abstract record ChatTimelineItem(string ItemId);

internal sealed record ChatRowTimelineItem(ChatRow Row, long SnapshotRevision)
    : ChatTimelineItem(Row.ItemId);

/// <summary>
/// 多 <c>ViewType</c> 原生聊天列表适配器（N-04-03）：
/// 使用 XML 模板（<c>item_chat_*.xml</c>）与 <see cref="DiffUtil"/> 局部刷新，支持流式 Markdown 后台异步解析与版本守卫。
/// 审批 / 问答卡由固定 RequestSlot 承载，本适配器不重复渲染待处理请求。
/// </summary>
internal sealed class ChatTimelineAdapter : RecyclerView.Adapter
{
    private const int TypeUser = 1;
    private const int TypeAssistantMarkdown = 2;
    private const int TypeReasoning = 3;
    private const int TypeToolCall = 4;
    private const int TypeCommand = 5;
    private const int TypeNotice = 6;
    private const int TypeToolGroup = 9;
    private const int TypeReplyFooter = 10;
    private const int TypeLiveStatus = 11;

    private readonly MarkdownRenderCache _mdCache;
    private readonly Action<Action> _postToUi;
    private readonly Action<string, bool>? _onOpenWorkspaceFile;
    private readonly HashSet<string> _expandedItems = new(StringComparer.Ordinal);
    private List<ChatTimelineItem> _items = [];

    public ChatTimelineAdapter(
        MarkdownRenderCache mdCache,
        Action<Action> postToUi,
        Action<string, bool>? onOpenWorkspaceFile = null)
    {
        _mdCache = mdCache;
        _postToUi = postToUi;
        _onOpenWorkspaceFile = onOpenWorkspaceFile;
        HasStableIds = true;
    }

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
        ToolGroupTimelineItem => TypeToolGroup,
        ReplyFooterTimelineItem => TypeReplyFooter,
        LiveStatusTimelineItem => TypeLiveStatus,
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
            TypeToolGroup => new ToolGroupHolder(
                inflater.Inflate(Resource.Layout.item_chat_tool_group, parent, false)!,
                _expandedItems,
                _onOpenWorkspaceFile),
            TypeReplyFooter => new ReplyFooterHolder(inflater.Inflate(Resource.Layout.item_chat_reply_footer, parent, false)!),
            TypeLiveStatus => new LiveStatusHolder(inflater.Inflate(Resource.Layout.item_chat_live_status, parent, false)!),
            _ => new NoticeHolder(inflater.Inflate(Resource.Layout.item_chat_notice, parent, false)!, _expandedItems),
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
                h.Bind(m, r.SnapshotRevision);
                break;
            case ReasoningHolder h when item is ChatRowTimelineItem { Row: ReasoningBlockRow r }:
                h.Bind(r, r.DurationMs);
                break;
            case ToolCallHolder h when item is ChatRowTimelineItem { Row: ToolCallRow t }:
                h.Bind(t);
                break;
            case ToolGroupHolder h when item is ToolGroupTimelineItem g:
                h.Bind(g);
                break;
            case ReplyFooterHolder h when item is ReplyFooterTimelineItem f:
                h.Bind(f);
                break;
            case LiveStatusHolder h when item is LiveStatusTimelineItem l:
                h.Bind(l);
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
            if (oldItem is ToolGroupTimelineItem oldGroup && newItem is ToolGroupTimelineItem newGroup)
            {
                // 工具组内容必须逐项比较；集合引用在每次投影后都会变化，不能依赖引用相等。
                return oldGroup.TurnSeq == newGroup.TurnSeq
                    && oldGroup.HasActive == newGroup.HasActive
                    && oldGroup.HasError == newGroup.HasError
                    && oldGroup.HasEditOperation == newGroup.HasEditOperation
                    && oldGroup.Tools.SequenceEqual(newGroup.Tools);
            }
            if (oldItem is ReplyFooterTimelineItem oldFooter && newItem is ReplyFooterTimelineItem newFooter)
            {
                return oldFooter.DurationMs == newFooter.DurationMs
                    && string.Equals(oldFooter.CopyText, newFooter.CopyText, StringComparison.Ordinal);
            }
            if (oldItem is LiveStatusTimelineItem oldLive && newItem is LiveStatusTimelineItem newLive)
            {
                return oldLive.Kind == newLive.Kind;
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
        private readonly LinearLayout _segments = itemView.FindViewById<LinearLayout>(Resource.Id.chat_assistant_segments)!;
        private string? _boundItemId;
        private long _boundRevision;
        private string? _boundFingerprint;
        private CancellationTokenSource? _parseCts;

        public void Bind(MarkdownBlockRow row, long revision)
        {
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
        private readonly TextView _title;
        private readonly View _bodyContainer;
        private readonly TextView _body;
        private readonly ImageView _chevron;
        private readonly HashSet<string> _expanded;
        private string? _itemId;

        public ReasoningHolder(View itemView, HashSet<string> expanded) : base(itemView)
        {
            _expanded = expanded;
            _title = itemView.FindViewById<TextView>(Resource.Id.chat_reasoning_title)!;
            _bodyContainer = itemView.FindViewById(Resource.Id.chat_reasoning_body_container)!;
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

        /// <summary>
        /// 绑定思考块。UI-S1 阶段 <see cref="ReasoningBlockRow"/> 尚未透传真实耗时，
        /// 因此这里只按 <see cref="ReasoningBlockRow.IsSettled"/> 显示“思考中…”或“思考过程”；
        /// UI-S2 透传耗时后直接传入 <paramref name="elapsedMs"/> 即可展示真实秒数，不伪造任何数据。
        /// </summary>
        public void Bind(ReasoningBlockRow row, long? elapsedMs = null)
        {
            _itemId = row.ItemId;
            _title.Text = FormatTitle(row.IsSettled, elapsedMs);
            _body.Text = row.Text;
            ApplyExpanded(_expanded.Contains(row.ItemId));
        }

        private string FormatTitle(bool isSettled, long? elapsedMs)
        {
            var ctx = ItemView.Context!;
            if (elapsedMs is > 0)
            {
                var seconds = Math.Max(1, elapsedMs.Value / 1000);
                return Rows.Fmt(
                    ctx,
                    isSettled ? Resource.String.chat_reasoning_done_fmt : Resource.String.chat_reasoning_active_fmt,
                    seconds);
            }
            return ctx.GetString(isSettled ? Resource.String.chat_reasoning_title : Resource.String.chat_reasoning_active);
        }

        private void ApplyExpanded(bool isOpen)
        {
            _bodyContainer.Visibility = isOpen ? ViewStates.Visible : ViewStates.Gone;
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

            // Polish-P2：即使旧单步卡片被复用，也只允许 18dp 细线单色图标；
            // 正常态统一 colorOnSurfaceVariant，仅失败步使用琥珀关注色，不再出现实心绿勾 / 红色圆点。
            var (phaseRes, iconRes, tintColor) = row.Phase switch
            {
                ToolPhase.Completed => (Resource.String.chat_tool_status_completed, Resource.Drawable.ic_check_circle, ResolveAttrColor(ctx, Resource.Attribute.colorOnSurfaceVariant)),
                ToolPhase.Error => (Resource.String.chat_tool_status_error, Resource.Drawable.ic_error, new Color(ctx.GetColor(Resource.Color.dshm_attention))),
                ToolPhase.Cancelled => (Resource.String.chat_tool_status_cancelled, Resource.Drawable.ic_close, ResolveAttrColor(ctx, Resource.Attribute.colorOnSurfaceVariant)),
                ToolPhase.Running => (Resource.String.chat_tool_status_running, Resource.Drawable.ic_refresh, ResolveAttrColor(ctx, Resource.Attribute.colorOnSurfaceVariant)),
                _ => (Resource.String.chat_tool_status_pending, Resource.Drawable.ic_history, ResolveAttrColor(ctx, Resource.Attribute.colorOnSurfaceVariant)),
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
    }

    /// <summary>
    /// 工具分组：运行中 / 出错时展开最新 3 步，已完成时默认折叠为单行汇总；
    /// 底部复用去重后的工作区文件胶囊与可选 Diff 入口。
    /// </summary>
    private sealed class ToolGroupHolder : RecyclerView.ViewHolder
    {
        private readonly ImageView _icon;
        private readonly TextView _summary;
        private readonly TextView _duration;
        private readonly ImageView _chevron;
        private readonly View _earlierRow;
        private readonly View _stepsWrap;
        private readonly LinearLayout _steps;
        private readonly View _filesScroll;
        private readonly LinearLayout _filesContainer;
        private readonly HashSet<string> _expanded;
        private readonly Action<string, bool>? _onOpenWorkspaceFile;
        private readonly List<ToolStepView> _stepViews = [];
        private ToolGroupTimelineItem? _group;
        private string? _expandKey;
        private string? _collapseKey;

        public ToolGroupHolder(
            View itemView,
            HashSet<string> expanded,
            Action<string, bool>? onOpenWorkspaceFile) : base(itemView)
        {
            _expanded = expanded;
            _onOpenWorkspaceFile = onOpenWorkspaceFile;
            _icon = itemView.FindViewById<ImageView>(Resource.Id.chat_tool_group_icon)!;
            _summary = itemView.FindViewById<TextView>(Resource.Id.chat_tool_group_summary)!;
            _duration = itemView.FindViewById<TextView>(Resource.Id.chat_tool_group_duration)!;
            _chevron = itemView.FindViewById<ImageView>(Resource.Id.chat_tool_group_chevron)!;
            _earlierRow = itemView.FindViewById(Resource.Id.chat_tool_group_earlier_row)!;
            _stepsWrap = itemView.FindViewById(Resource.Id.chat_tool_group_steps_wrap)!;
            _steps = itemView.FindViewById<LinearLayout>(Resource.Id.chat_tool_group_steps)!;
            _filesScroll = itemView.FindViewById<View>(Resource.Id.chat_tool_group_files_scroll)!;
            _filesContainer = itemView.FindViewById<LinearLayout>(Resource.Id.chat_tool_group_files_container)!;

            // 头部始终切换整个工具组的展开 / 折叠，成功组与含失败步骤的组行为一致。
            itemView.FindViewById(Resource.Id.chat_tool_group_header)!.Click += (_, _) => ToggleGroup();
        }

        public void Bind(ToolGroupTimelineItem group)
        {
            _group = group;
            _expandKey = $"group:{group.ItemId}";
            _collapseKey = $"group-collapsed:{group.ItemId}";

            var ctx = ItemView.Context!;
            _summary.Text = ChatTimelineProjector.BuildGroupSummaryText(group.CategoryCounts);
            _icon.SetImageResource(ResolveGroupIcon(group));
            var tint = group.HasError
                ? new Color(ctx.GetColor(Resource.Color.dshm_attention))
                : ResolveAttrColor(ctx, Resource.Attribute.colorOnSurfaceVariant);
            _icon.ImageTintList = ColorStateList.ValueOf(tint);

            if (ChatTimelineProjector.SumToolDurationsMs(group.Tools) is { } totalDurationMs)
            {
                _duration.Visibility = ViewStates.Visible;
                _duration.Text = Rows.Fmt(ctx, Resource.String.chat_tool_duration_fmt, FormatDurationText(totalDurationMs));
            }
            else
            {
                _duration.Visibility = ViewStates.Gone;
                _duration.Text = string.Empty;
            }

            RenderFiles(group, ctx);
            ApplyGroupState();
        }

        private void ToggleGroup()
        {
            if (_group is null || _expandKey is null || _collapseKey is null)
            {
                return;
            }

            // 运行中 / 出错组默认展开，使用“折叠标记”反转默认值；已完成组默认折叠。
            var defaultOpen = _group.HasActive || _group.HasError;
            ToggleKey(defaultOpen ? _collapseKey : _expandKey);
            ApplyGroupState();
        }

        private void ToggleKey(string key)
        {
            if (!_expanded.Add(key))
            {
                _expanded.Remove(key);
            }
        }

        private void ApplyGroupState()
        {
            if (_group is null || _expandKey is null || _collapseKey is null)
            {
                return;
            }

            var total = _group.Tools.Count;
            var defaultOpen = _group.HasActive || _group.HasError;
            var isOpen = defaultOpen
                ? !_expanded.Contains(_collapseKey)
                : _expanded.Contains(_expandKey);

            _stepsWrap.Visibility = isOpen ? ViewStates.Visible : ViewStates.Gone;
            _steps.Visibility = isOpen ? ViewStates.Visible : ViewStates.Gone;
            // 头部已统一为整组展开 / 折叠，旧的“更早步骤”分段入口不再需要。
            _earlierRow.Visibility = ViewStates.Gone;
            _chevron.Visibility = ViewStates.Visible;
            _chevron.Rotation = isOpen ? 180f : 0f;
            RenderSteps(isOpen ? total : 0);
        }

        private void RenderSteps(int count)
        {
            if (_group is null)
            {
                return;
            }

            var inflater = LayoutInflater.From(ItemView.Context)!;
            while (_stepViews.Count < count)
            {
                var stepView = inflater.Inflate(Resource.Layout.item_chat_tool_step, _steps, false)!;
                _stepViews.Add(new ToolStepView(stepView, _expanded));
                _steps.AddView(stepView);
            }

            while (_stepViews.Count > count)
            {
                var last = _stepViews[^1];
                _stepViews.RemoveAt(_stepViews.Count - 1);
                _steps.RemoveView(last.Root);
            }

            for (var i = 0; i < count; i++)
            {
                _stepViews[i].Bind(_group.Tools[i]);
            }
        }

        private void RenderFiles(ToolGroupTimelineItem group, Context ctx)
        {
            _filesContainer.RemoveAllViews();
            if (group.WorkspaceFiles.Count == 0)
            {
                _filesScroll.Visibility = ViewStates.Gone;
                return;
            }

            var inflater = LayoutInflater.From(ctx)!;
            foreach (var path in group.WorkspaceFiles)
            {
                var capsule = inflater.Inflate(Resource.Layout.item_chat_tool_file_capsule, _filesContainer, false)!;
                var nameButton = capsule.FindViewById<MaterialButton>(Resource.Id.chat_tool_file_capsule_name)!;
                nameButton.Text = path;
                nameButton.Click += (_, _) => _onOpenWorkspaceFile?.Invoke(path, false);

                var diffButton = capsule.FindViewById<MaterialButton>(Resource.Id.chat_tool_file_capsule_diff)!;
                if (group.HasEditOperation)
                {
                    diffButton.Visibility = ViewStates.Visible;
                    diffButton.Click += (_, _) => _onOpenWorkspaceFile?.Invoke(path, true);
                }
                else
                {
                    diffButton.Visibility = ViewStates.Gone;
                }

                _filesContainer.AddView(capsule);
            }
            _filesScroll.Visibility = ViewStates.Visible;
        }

        private static int ResolveGroupIcon(ToolGroupTimelineItem group)
        {
            if (group.CategoryCounts.TryGetValue(ToolCategory.Edit, out var edits) && edits > 0)
            {
                return Resource.Drawable.ic_code;
            }
            if (group.CategoryCounts.TryGetValue(ToolCategory.Command, out var commands) && commands > 0)
            {
                return Resource.Drawable.ic_terminal;
            }
            if (group.CategoryCounts.TryGetValue(ToolCategory.Search, out var searches) && searches > 0)
            {
                return Resource.Drawable.ic_search;
            }
            if (group.CategoryCounts.TryGetValue(ToolCategory.Read, out var reads) && reads > 0)
            {
                return Resource.Drawable.ic_article;
            }
            return Resource.Drawable.ic_build;
        }
    }

    /// <summary>
    /// 单个工具步骤：统一 18dp 单色线性图标 + 中文摘要 + 真实耗时；
    /// 展开后把参数 / 命令与输出合并进同一张 14dp 圆角终端卡片。
    /// </summary>
    private sealed class ToolStepView
    {
        private const int VisibleOutputLines = 12;

        private readonly View _root;
        private readonly ImageView _statusIcon;
        private readonly TextView _summary;
        private readonly TextView _duration;
        private readonly ImageView _chevron;
        private readonly View _detail;
        private readonly TextView _detailText;
        private readonly MaterialButton _fullOutputButton;
        private readonly HashSet<string> _expanded;
        private string? _stepKey;
        private string _fullOutput = string.Empty;
        private bool _hasDetail;

        public ToolStepView(View root, HashSet<string> expanded)
        {
            _root = root;
            _expanded = expanded;
            _statusIcon = root.FindViewById<ImageView>(Resource.Id.chat_tool_step_status_icon)!;
            _summary = root.FindViewById<TextView>(Resource.Id.chat_tool_step_summary)!;
            _duration = root.FindViewById<TextView>(Resource.Id.chat_tool_step_duration)!;
            _chevron = root.FindViewById<ImageView>(Resource.Id.chat_tool_step_chevron)!;
            _detail = root.FindViewById(Resource.Id.chat_tool_step_detail)!;
            _detailText = root.FindViewById<TextView>(Resource.Id.chat_tool_step_detail_text)!;
            _fullOutputButton = root.FindViewById<MaterialButton>(Resource.Id.chat_tool_step_full_output_btn)!;

            root.FindViewById(Resource.Id.chat_tool_step_header)!.Click += (_, _) => ToggleExpanded();
            _fullOutputButton.Click += (_, _) => ShowFullOutput();
        }

        public View Root => _root;

        public void Bind(ToolCallRow tool)
        {
            var callId = string.IsNullOrWhiteSpace(tool.CallId) ? tool.ItemId : tool.CallId;
            _stepKey = $"step:{callId}";

            var ctx = Root.Context!;
            var isError = tool.Phase == ToolPhase.Error || tool.IsError;
            _statusIcon.SetImageResource(ResolveToolIcon(tool.ToolName));
            _statusIcon.ImageTintList = ColorStateList.ValueOf(isError
                ? new Color(ctx.GetColor(Resource.Color.dshm_attention))
                : ResolveAttrColor(ctx, Resource.Attribute.colorOnSurfaceVariant));
            _summary.Text = ChatTimelineProjector.FormatHumanReadableToolStep(tool);

            if (tool.DurationMs is { } durationMs && durationMs >= 100)
            {
                _duration.Visibility = ViewStates.Visible;
                _duration.Text = FormatDurationText(durationMs);
            }
            else
            {
                _duration.Visibility = ViewStates.Gone;
                _duration.Text = string.Empty;
            }

            var args = ChatTimelineProjector.FormatBeautifiedToolArguments(tool);
            _fullOutput = tool.OutputSummary ?? string.Empty;
            var normalizedOutput = NormalizeNewLines(_fullOutput);
            var truncatedOutput = string.IsNullOrWhiteSpace(normalizedOutput)
                ? string.Empty
                : ChatTimelineProjector.TruncateLines(normalizedOutput, VisibleOutputLines);

            var sections = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(args))
            {
                sections.Add(args);
            }
            if (!string.IsNullOrWhiteSpace(truncatedOutput))
            {
                sections.Add(truncatedOutput);
            }
            var detailText = string.Join("\n\n", sections);
            _hasDetail = detailText.Length > 0;
            if (_hasDetail)
            {
                // 终端卡片只高亮 `$ ` 命令行，stdout / stderr 保持中性原色。
                ChatMarkdownBinder.ApplyTerminalSyntax(ctx, _detailText, detailText);
            }
            else
            {
                _detailText.Text = string.Empty;
            }

            _fullOutputButton.Visibility = !string.IsNullOrWhiteSpace(normalizedOutput)
                && !string.Equals(truncatedOutput, normalizedOutput, StringComparison.Ordinal)
                ? ViewStates.Visible
                : ViewStates.Gone;

            ApplyExpanded();
        }

        private void ToggleExpanded()
        {
            if (_stepKey is null)
            {
                return;
            }

            if (!_expanded.Add(_stepKey))
            {
                _expanded.Remove(_stepKey);
            }
            ApplyExpanded();
        }

        private void ApplyExpanded()
        {
            var isOpen = _stepKey is not null && _expanded.Contains(_stepKey);
            _detail.Visibility = isOpen && _hasDetail ? ViewStates.Visible : ViewStates.Gone;
            _chevron.Rotation = isOpen ? 180f : 0f;
        }

        private void ShowFullOutput()
        {
            if (string.IsNullOrWhiteSpace(_fullOutput))
            {
                return;
            }

            var ctx = Root.Context!;
            var content = LayoutInflater.From(ctx)!.Inflate(Resource.Layout.dialog_scrollable_text, null, false)!;
            var textView = content.FindViewById<TextView>(Resource.Id.dialog_scrollable_text)!;
            textView.Typeface = Typeface.Monospace;
            textView.SetTextSize(ComplexUnitType.Sp, 12f);
            ChatMarkdownBinder.ApplyTerminalSyntax(ctx, textView, _fullOutput);

            new MaterialAlertDialogBuilder(ctx)
                .SetTitle(Resource.String.chat_tool_full_output)!
                .SetView(content)!
                .SetPositiveButton(Android.Resource.String.Ok, (_, _) => { })!
                .Show();
        }
    }

    /// <summary>助手 Turn 末尾 Footer：真实耗时与复制回复，无有效数据时隐藏对应元素。</summary>
    private sealed class ReplyFooterHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _meta;
        private readonly MaterialButton _copy;
        private string _copyText = string.Empty;

        public ReplyFooterHolder(View itemView) : base(itemView)
        {
            _meta = itemView.FindViewById<TextView>(Resource.Id.chat_reply_footer_meta)!;
            _copy = itemView.FindViewById<MaterialButton>(Resource.Id.chat_reply_footer_copy)!;
            _copy.Click += (_, _) => CopyReply();
        }

        public void Bind(ReplyFooterTimelineItem item)
        {
            var ctx = ItemView.Context!;
            if (item.DurationMs is { } durationMs && durationMs > 0)
            {
                _meta.Visibility = ViewStates.Visible;
                _meta.Text = Rows.Fmt(
                    ctx,
                    Resource.String.chat_tool_duration_fmt,
                    FormatDurationText(durationMs));
            }
            else
            {
                _meta.Visibility = ViewStates.Gone;
                _meta.Text = string.Empty;
            }

            _copyText = item.CopyText ?? string.Empty;
            _copy.Visibility = string.IsNullOrWhiteSpace(_copyText) ? ViewStates.Gone : ViewStates.Visible;
        }

        private void CopyReply()
        {
            if (string.IsNullOrWhiteSpace(_copyText))
            {
                return;
            }

            var ctx = ItemView.Context!;
            var clipboard = (ClipboardManager?)ctx.GetSystemService(Context.ClipboardService);
            clipboard?.PrimaryClip = ClipData.NewPlainText("reply", _copyText);
            Snackbar.Make(ItemView, Resource.String.chat_reply_copied, Snackbar.LengthShort).Show();
        }
    }

    /// <summary>流式 Live 状态行：本地化文案 + 呼吸圆点，仅在附着时播放动画。</summary>
    private sealed class LiveStatusHolder : RecyclerView.ViewHolder
    {
        private readonly View _dot;
        private static readonly float[] BreathingAlphaRange = [0.35f, 1f];

        private readonly TextView _text;
        private ObjectAnimator? _breathingAnimator;
        private bool _bound;
        private bool _attached;

        public LiveStatusHolder(View itemView) : base(itemView)
        {
            _dot = itemView.FindViewById(Resource.Id.chat_live_status_dot)!;
            _text = itemView.FindViewById<TextView>(Resource.Id.chat_live_status_text)!;
            _attached = itemView.IsAttachedToWindow;
            itemView.ViewAttachedToWindow += (_, _) => Attach();
            itemView.ViewDetachedFromWindow += (_, _) => Detach();
        }

        public void Bind(LiveStatusTimelineItem item)
        {
            _bound = true;
            _text.SetText(item.Kind == LiveStatusKind.Thinking
                ? Resource.String.chat_live_thinking
                : Resource.String.chat_live_executing);
            StartBreathing();
        }

        public void Attach()
        {
            _attached = true;
            StartBreathing();
        }

        public void Detach()
        {
            _attached = false;
            StopBreathing();
        }

        private void StartBreathing()
        {
            if (!_bound || !_attached || _breathingAnimator is not null)
            {
                return;
            }

            var animator = ObjectAnimator.OfFloat(_dot, "alpha", BreathingAlphaRange);
            if (animator is null)
            {
                return;
            }
            animator.SetDuration(900);
            animator.RepeatCount = ValueAnimator.Infinite;
            animator.RepeatMode = ValueAnimatorRepeatMode.Reverse;
            animator.Start();
            _breathingAnimator = animator;
        }

        private void StopBreathing()
        {
            if (_breathingAnimator is null)
            {
                return;
            }

            _breathingAnimator.Cancel();
            _breathingAnimator.Dispose();
            _breathingAnimator = null;
            _dot.Alpha = 1f;
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

    private sealed class NoticeHolder : RecyclerView.ViewHolder
    {
        private readonly ImageView _icon;
        private readonly TextView _text;
        private readonly ImageView _chevron;
        private readonly TextView _detail;
        private readonly HashSet<string> _expanded;
        private string? _itemId;

        public NoticeHolder(View itemView, HashSet<string> expanded) : base(itemView)
        {
            _expanded = expanded;
            _icon = itemView.FindViewById<ImageView>(Resource.Id.chat_notice_icon)!;
            _text = itemView.FindViewById<TextView>(Resource.Id.chat_notice_text)!;
            _chevron = itemView.FindViewById<ImageView>(Resource.Id.chat_notice_chevron)!;
            _detail = itemView.FindViewById<TextView>(Resource.Id.chat_notice_detail)!;
            itemView.FindViewById(Resource.Id.chat_notice_header)!.Click += (_, _) =>
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

        public void BindNotice(NoticeRow row)
        {
            var ctx = ItemView.Context!;
            _icon.SetImageResource(row.IsError ? Resource.Drawable.ic_error : Resource.Drawable.ic_info);
            // 普通错误不再使用高饱和红色图标，统一用琥珀关注色 / 中性色降噪。
            var tint = row.IsError
                ? new Color(ctx.GetColor(Resource.Color.dshm_attention))
                : ResolveAttrColor(ctx, Resource.Attribute.colorOnSurfaceVariant);
            _icon.ImageTintList = ColorStateList.ValueOf(tint);
            Bind(
                row.ItemId,
                ChatNoticeFormatter.Describe(ctx, row.Code, row.DiagnosticKey, row.IsError),
                ChatNoticeFormatter.BuildTechnicalDetail(row.Code, row.DiagnosticKey));
        }

        public void BindUnknown(UnknownEventRow row)
        {
            var ctx = ItemView.Context!;
            _icon.SetImageResource(Resource.Drawable.ic_info);
            _icon.ImageTintList = ColorStateList.ValueOf(ResolveAttrColor(ctx, Resource.Attribute.colorOnSurfaceVariant));
            Bind(
                row.ItemId,
                ctx.GetString(Resource.String.chat_notice_unknown_event),
                $"[{row.EventType}]");
        }

        private void Bind(string itemId, string friendlyText, string technicalDetail)
        {
            _itemId = itemId;
            _text.Text = friendlyText;
            _detail.Text = technicalDetail;
            ApplyExpanded(_expanded.Contains(itemId));
        }

        private void ApplyExpanded(bool isOpen)
        {
            _detail.Visibility = isOpen ? ViewStates.Visible : ViewStates.Gone;
            _chevron.Rotation = isOpen ? 180f : 0f;
        }
    }

    /// <summary>工具名到统一 18dp 单色线性图标的映射；仅失败步骤由调用方额外使用关注色。</summary>
    private static int ResolveToolIcon(string toolName)
    {
        var lower = toolName.Trim().ToLowerInvariant();
        if (lower.Contains("bash", StringComparison.Ordinal)
            || lower.Contains("shell", StringComparison.Ordinal)
            || lower.Contains("exec", StringComparison.Ordinal)
            || lower == "sh")
        {
            return Resource.Drawable.ic_terminal;
        }
        if (lower.Contains("read", StringComparison.Ordinal)
            || lower.Contains("view", StringComparison.Ordinal)
            || lower.Contains("cat", StringComparison.Ordinal))
        {
            return Resource.Drawable.ic_article;
        }
        if (lower.Contains("edit", StringComparison.Ordinal)
            || lower.Contains("write", StringComparison.Ordinal)
            || lower.Contains("patch", StringComparison.Ordinal))
        {
            return Resource.Drawable.ic_code;
        }
        if (lower.Contains("search", StringComparison.Ordinal)
            || lower.Contains("grep", StringComparison.Ordinal)
            || lower.Contains("glob", StringComparison.Ordinal)
            || lower.Contains("find", StringComparison.Ordinal))
        {
            return Resource.Drawable.ic_search;
        }
        return Resource.Drawable.ic_build;
    }

    /// <summary>统一换行符，保证代码 / 输出行数与截断判断一致。</summary>
    private static string NormalizeNewLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>
    /// 真实耗时的人类可读格式：不足 10 秒保留一位小数（0.3s / 1.5s），
    /// 10 秒及以上显示整秒（12s）。调用方负责仅在真实数据有效时展示。
    /// </summary>
    private static string FormatDurationText(long elapsedMs)
    {
        if (elapsedMs >= 10_000)
        {
            return (elapsedMs / 1000).ToString(CultureInfo.InvariantCulture) + "s";
        }
        return (elapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";
    }

    private static Color ResolveAttrColor(Android.Content.Context ctx, int attrId)
    {
        var tv = new TypedValue();
        return ctx.Theme?.ResolveAttribute(attrId, tv, true) == true
            ? new Color(tv.Data)
            : new Color(ctx.GetColor(Android.Resource.Color.Black));
    }
}
