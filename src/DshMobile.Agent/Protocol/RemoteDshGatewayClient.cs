using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Transport;

namespace DshMobile.Agent.Protocol;

/// <summary>
/// Route B <see cref="IDshGateway"/> adapter over <see cref="DshRemoteConnection"/> (ADR-0033, N-02-02, N-02-03).
/// Translates HTTP `/api/*` RPCs and `/api/remote.mux` streams into strongly typed <see cref="Outcome{T}"/> calls
/// with SourceGen serialization, strict schema payloads, and zero leaked exception strings.
/// </summary>
public sealed class RemoteDshGatewayClient : DefaultDshGateway, IDisposable
{
    private readonly DshRemoteConnection _connection;
    private readonly Func<string> _timeZoneProvider;
    private readonly ImageUploadLimitsSpec _imageLimits;
    private readonly Lock _capLock = new();
    private DshCapabilities _capabilities = DshCapabilities.Empty;
    private int _disposed;

    public RemoteDshGatewayClient(
        DshRemoteConnection connection,
        Func<string>? timeZoneProvider = null,
        ImageUploadLimitsSpec? imageLimits = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _timeZoneProvider = timeZoneProvider ?? (() => IanaTimeZoneResolver.Resolve());
        _imageLimits = imageLimits ?? ImageUploadLimitsSpec.Default;
        _connection.ForwardedEventReceived += HandleForwardedEventAsync;
    }

    public override DshCapabilities Capabilities
    {
        get
        {
            lock (_capLock)
            {
                return _capabilities;
            }
        }
    }

    public override event Action<ForwardedGatewayEvent>? ForwardedEvent;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _connection.ForwardedEventReceived -= HandleForwardedEventAsync;
    }

    public override async Task<Outcome<DshCapabilities>> ProbeCapabilitiesAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            var gen = await _connection.ConnectAsync(ct).ConfigureAwait(false);
            var hasGen = !string.IsNullOrEmpty(gen.ClientId);

            // Dynamic probing over live Remote endpoints instead of guessing versions (N-02-02).
            var listProbe = await CallRawAsync("session/list", RemoteProtocolCodec.SerializeSessionList(), ct).ConfigureAwait(false);
            var modelProbe = await CallRawAsync("session/modelCatalog", RemoteWireCodec.EmptyArgs, ct).ConfigureAwait(false);
            var credProbe = await _connection.CallAsync(
                "credentials/describe",
                JsonSerializer.SerializeToElement(new WireCredentialsDescribeArgs([]), AgentProtocolJsonContext.Default.WireCredentialsDescribeArgs),
                ct).ConfigureAwait(false);
            var pluginProbe = await _connection.CallAsync("pluginInventory/list", RemoteWireCodec.EmptyArgs, ct).ConfigureAwait(false);

            var caps = new DshCapabilities(
                Prompt: hasGen,
                PromptImages: hasGen && _imageLimits.MaxImagesPerMessage > 0,
                SlashCommands: hasGen,
                StreamingEvents: hasGen,
                Approvals: hasGen,
                UserQuestions: hasGen,
                ResumeKnownSession: listProbe.IsOk,
                ListSessions: listProbe.IsOk,
                HistoryReplay: listProbe.IsOk,
                CancelSession: hasGen,
                SwitchModelInSession: modelProbe.IsOk,
                ManageCredentials: credProbe.Ok,
                ServerPromptIdempotency: hasGen,
                PluginInventory: pluginProbe.Ok,
                PluginMutation: false,
                GitOperations: false);

            lock (_capLock)
            {
                _capabilities = caps;
            }
            return Outcome<DshCapabilities>.Ok(caps);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Outcome<DshCapabilities>.Fail(DshFailureMapper.FromException(ex));
        }
    }

    public override async Task<Outcome<IReadOnlyList<SessionSummaryView>>> ListSessionsAsync(CancellationToken ct = default)
    {
        var rpc = await CallRawAsync("session/list", RemoteProtocolCodec.SerializeSessionList(), ct).ConfigureAwait(false);
        if (!rpc.IsOk)
        {
            return Outcome<IReadOnlyList<SessionSummaryView>>.Fail(rpc.Error!);
        }

        var dtoOutcome = DeserializeValue(rpc.Value, AgentProtocolJsonContext.Default.WireSessionListValue, "session_list.decode");
        if (!dtoOutcome.IsOk)
        {
            return Outcome<IReadOnlyList<SessionSummaryView>>.Fail(dtoOutcome.Error!);
        }
        var dto = dtoOutcome.Value!;

        var mapped = dto.Items.Select(s => new SessionSummaryView(
            s.SessionId,
            s.UpdatedAt,
            s.Running,
            s.Blank,
            s.AgentAvailable,
            s.ParentSessionId,
            s.Origin,
            s.Cwd,
            s.FormatStatus)).ToArray();

        return Outcome<IReadOnlyList<SessionSummaryView>>.Ok(mapped);
    }

    public override async Task<Outcome<string>> CreateOrResumeSessionAsync(CreateOrResumeSessionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var args = RemoteProtocolCodec.SerializeSessionCreate(request);
        var rpc = await CallRawAsync("session/create", args, ct).ConfigureAwait(false);
        if (!rpc.IsOk)
        {
            return Outcome<string>.Fail(rpc.Error!);
        }

        var dtoOutcome = DeserializeValue(rpc.Value, AgentProtocolJsonContext.Default.WireSessionCreateValue, "session_create.decode");
        if (!dtoOutcome.IsOk)
        {
            return Outcome<string>.Fail(dtoOutcome.Error!);
        }
        var dto = dtoOutcome.Value!;
        if (string.IsNullOrEmpty(dto.SessionId))
        {
            return Outcome<string>.Fail(DshFailureCode.ProtocolMismatch, "session_create.missing_id");
        }
        return Outcome<string>.Ok(dto.SessionId);
    }

    public override async Task<Outcome<SessionStateView>> GetSessionStateAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        var args = JsonSerializer.SerializeToElement(
            new WireSessionStateArgs(new WireSessionIdRequest(sessionId)),
            AgentProtocolJsonContext.Default.WireSessionStateArgs);
        var rpc = await CallRawAsync("session/state", args, ct).ConfigureAwait(false);
        if (!rpc.IsOk)
        {
            return Outcome<SessionStateView>.Fail(rpc.Error!);
        }

        var dtoOutcome = DeserializeValue(rpc.Value, AgentProtocolJsonContext.Default.WireSessionStateValue, "session_state.decode");
        return dtoOutcome.Map(RemoteProtocolCodec.MapState);
    }

    public override async Task<Outcome<SessionPageView>> LoadSessionEventsAsync(
        string sessionId,
        long? throughSeq = null,
        long? beforeSeq = null,
        int? maxMessages = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        var args = JsonSerializer.SerializeToElement(
            new WireSessionPageArgs(new WireSessionPageRequest(
                new WireSessionAddress("session", sessionId),
                throughSeq,
                beforeSeq,
                maxMessages)),
            AgentProtocolJsonContext.Default.WireSessionPageArgs);
        var rpc = await CallRawAsync("session/events", args, ct).ConfigureAwait(false);
        if (!rpc.IsOk)
        {
            return Outcome<SessionPageView>.Fail(rpc.Error!);
        }

        var dtoOutcome = DeserializeValue(rpc.Value, AgentProtocolJsonContext.Default.WireSessionEventsPage, "session_events.decode");
        return dtoOutcome.Map(dto => new SessionPageView(
            dto.Records.Select(r => RemoteProtocolCodec.MapWireEvent(r.Event)).ToArray(),
            dto.HasMore,
            dto.Cursor));
    }

    public override async Task<Outcome<SessionWaitResultView>> WaitForSessionAsync(
        string sessionId,
        TimeSpan? timeout = null,
        long? afterSeq = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        int? timeoutMs = timeout is { } ts ? Math.Max(1, (int)ts.TotalMilliseconds) : null;
        var args = JsonSerializer.SerializeToElement(
            new WireSessionWaitArgs(new WireSessionWaitRequest(sessionId, timeoutMs, afterSeq)),
            AgentProtocolJsonContext.Default.WireSessionWaitArgs);
        var rpc = await CallRawAsync("session/wait", args, ct).ConfigureAwait(false);
        if (!rpc.IsOk)
        {
            return Outcome<SessionWaitResultView>.Fail(rpc.Error!);
        }

        var dtoOutcome = DeserializeValue(rpc.Value, AgentProtocolJsonContext.Default.WireSessionWaitValue, "session_wait.decode");
        if (!dtoOutcome.IsOk)
        {
            return Outcome<SessionWaitResultView>.Fail(dtoOutcome.Error!);
        }
        var dto = dtoOutcome.Value!;
        // Defend against historical `{}` empty wait return (`N-02-03`): never mistake empty `{}` for idle completion.
        if (string.IsNullOrWhiteSpace(dto.Reason))
        {
            return Outcome<SessionWaitResultView>.Ok(new SessionWaitResultView(SessionWaitOutcomeKind.AmbiguousEmptyResponse));
        }

        var kind = dto.Reason switch
        {
            "idle" => SessionWaitOutcomeKind.Idle,
            "input-required" => SessionWaitOutcomeKind.InputRequired,
            "timeout" => SessionWaitOutcomeKind.Timeout,
            _ => SessionWaitOutcomeKind.AmbiguousEmptyResponse,
        };
        var state = dto.State is not null ? RemoteProtocolCodec.MapState(dto.State) : null;
        var events = dto.Output?.Records.Select(r => RemoteProtocolCodec.MapWireEvent(r.Event)).ToArray();
        return Outcome<SessionWaitResultView>.Ok(new SessionWaitResultView(
            kind,
            state,
            events,
            dto.Output?.Truncated ?? false));
    }

    public override async Task<Outcome<string>> RenameSessionAsync(string sessionId, string title, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(title);
        var args = JsonSerializer.SerializeToElement(
            new WireSessionRenameArgs(new WireSessionRenameRequest(sessionId, title)),
            AgentProtocolJsonContext.Default.WireSessionRenameArgs);
        var rpc = await CallRawAsync("session/rename", args, ct).ConfigureAwait(false);
        if (!rpc.IsOk)
        {
            return Outcome<string>.Fail(rpc.Error!);
        }

        var dtoOutcome = DeserializeValue(rpc.Value, AgentProtocolJsonContext.Default.WireSessionRenameValue, "session_rename.decode");
        return dtoOutcome.Map(dto => dto.Title);
    }

    public override async Task<Outcome<string>> ForkSessionAsync(string sessionId, long? atSeq = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        var args = JsonSerializer.SerializeToElement(
            new WireSessionForkArgs(new WireSessionForkRequest(sessionId, atSeq)),
            AgentProtocolJsonContext.Default.WireSessionForkArgs);
        var rpc = await CallRawAsync("session/fork", args, ct).ConfigureAwait(false);
        if (!rpc.IsOk)
        {
            return Outcome<string>.Fail(rpc.Error!);
        }

        var dtoOutcome = DeserializeValue(rpc.Value, AgentProtocolJsonContext.Default.WireSessionForkValue, "session_fork.decode");
        if (!dtoOutcome.IsOk)
        {
            return Outcome<string>.Fail(dtoOutcome.Error!);
        }
        return string.IsNullOrEmpty(dtoOutcome.Value!.SessionId)
            ? Outcome<string>.Fail(DshFailureCode.ProtocolMismatch, "session_fork.missing_id")
            : Outcome<string>.Ok(dtoOutcome.Value.SessionId);
    }

    public override async Task<Outcome<Unit>> SendPromptAsync(PromptSendRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Outcome<Unit>.Fail(DshFailureCode.InvalidState, "prompt.missing_ids");
        }
        var hasText = !string.IsNullOrWhiteSpace(request.Text);
        var hasImages = request.Images is { Count: > 0 };
        if (!request.AllowEmptyContinuation && !hasText && !hasImages)
        {
            return Outcome<Unit>.Fail(DshFailureCode.InvalidState, "prompt.empty_content");
        }
        if (ImageAttachmentValidator.Validate(request.Images, _imageLimits) is { } imgErr)
        {
            return Outcome<Unit>.Fail(imgErr);
        }

        string tz;
        try
        {
            var requestedTz = string.IsNullOrWhiteSpace(request.ClientTimeZone)
                ? _timeZoneProvider()
                : request.ClientTimeZone;
            tz = IanaTimeZoneResolver.Resolve(requestedTz);
        }
        catch (ArgumentException)
        {
            return Outcome<Unit>.Fail(DshFailureCode.InvalidState, "prompt.invalid_time_zone");
        }

        var args = RemoteProtocolCodec.SerializeSessionPrompt(request, tz);
        var rpc = await CallRawAsync("session/prompt", args, ct).ConfigureAwait(false);
        if (!rpc.IsOk)
        {
            return Outcome<Unit>.Fail(rpc.Error!);
        }

        var dtoOutcome = DeserializeValue(rpc.Value, AgentProtocolJsonContext.Default.WireSessionPromptValue, "session_prompt.decode");
        if (!dtoOutcome.IsOk)
        {
            return Outcome<Unit>.Fail(dtoOutcome.Error!);
        }
        return dtoOutcome.Value!.Accepted
            ? Outcome<Unit>.Ok(Unit.Value)
            : Outcome<Unit>.Fail(DshFailureCode.ProtocolMismatch, "session_prompt.not_accepted");
    }

    public override async Task<Outcome<IReadOnlyList<SlashCommandDescriptorView>>> ListCommandsAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        var args = JsonSerializer.SerializeToElement(
            new WireCommandsListArgs(sessionId),
            AgentProtocolJsonContext.Default.WireCommandsListArgs);
        var rpc = await CallRawAsync("commands/list", args, ct).ConfigureAwait(false);
        if (!rpc.IsOk)
        {
            return Outcome<IReadOnlyList<SlashCommandDescriptorView>>.Fail(rpc.Error!);
        }

        var itemsOutcome = DeserializeValue(rpc.Value, AgentProtocolJsonContext.Default.WireCommandDescriptorArray, "commands_list.decode");
        if (!itemsOutcome.IsOk)
        {
            return Outcome<IReadOnlyList<SlashCommandDescriptorView>>.Fail(itemsOutcome.Error!);
        }
        var items = itemsOutcome.Value!;

        var views = items.Select(c => new SlashCommandDescriptorView(
            c.Name,
            c.Description,
            c.Input?.Hint,
            c.Input?.Attachments ?? false,
            c.DefinitionId)).ToArray();
        return Outcome<IReadOnlyList<SlashCommandDescriptorView>>.Ok(views);
    }

    public override async Task<Outcome<SlashCommandExecutionView?>> ExecuteCommandAsync(
        string sessionId,
        string commandLine,
        IReadOnlyList<PromptAttachmentImage>? images = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        if (string.IsNullOrWhiteSpace(commandLine) || !commandLine.TrimStart().StartsWith('/'))
        {
            return Outcome<SlashCommandExecutionView?>.Fail(DshFailureCode.InvalidState, "command.invalid_syntax");
        }
        if (ImageAttachmentValidator.Validate(images, _imageLimits) is { } imgErr)
        {
            return Outcome<SlashCommandExecutionView?>.Fail(imgErr);
        }

        var args = RemoteProtocolCodec.SerializeCommandsExecute(sessionId, commandLine, images);
        var rpc = await CallRawAsync("commands/execute", args, ct).ConfigureAwait(false);
        if (!rpc.IsOk)
        {
            return Outcome<SlashCommandExecutionView?>.Fail(rpc.Error!);
        }
        if (rpc.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Outcome<SlashCommandExecutionView?>.Ok(null);
        }

        var dtoOutcome = DeserializeValue(rpc.Value, AgentProtocolJsonContext.Default.WireCommandExecutionDto, "command_exec.decode");
        if (!dtoOutcome.IsOk)
        {
            return Outcome<SlashCommandExecutionView?>.Fail(dtoOutcome.Error!);
        }
        var dto = dtoOutcome.Value!;
        return Outcome<SlashCommandExecutionView?>.Ok(new SlashCommandExecutionView(
            dto.CommandId,
            dto.Result.Kind == "success",
            dto.Result.Text,
            dto.Result.SourceEventSeq));
    }

    public override async Task<Outcome<Unit>> CancelTurnAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        var args = JsonSerializer.SerializeToElement(
            new WireSessionCancelArgs(new WireSessionIdRequest(sessionId)),
            AgentProtocolJsonContext.Default.WireSessionCancelArgs);
        var rpc = await CallRawAsync("session/cancel", args, ct).ConfigureAwait(false);
        if (!rpc.IsOk)
        {
            return Outcome<Unit>.Fail(rpc.Error!);
        }
        var dtoOutcome = DeserializeValue(rpc.Value, AgentProtocolJsonContext.Default.WireSessionCancelValue, "session_cancel.decode");
        if (!dtoOutcome.IsOk)
        {
            return Outcome<Unit>.Fail(dtoOutcome.Error!);
        }
        return dtoOutcome.Value!.Accepted
            ? Outcome<Unit>.Ok(Unit.Value)
            : Outcome<Unit>.Fail(DshFailureCode.ProtocolMismatch, "session_cancel.not_accepted");
    }

    public override async Task<Outcome<Unit>> ReplyApprovalAsync(
        string clientId,
        string eventId,
        ApprovalDecision decision,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentException.ThrowIfNullOrEmpty(eventId);
        var wireValue = RemoteProtocolCodec.MapApprovalDecisionWireValue(decision);
        var valEl = JsonSerializer.SerializeToElement(wireValue, AgentProtocolJsonContext.Default.String);
        return await SendEventOutcomeAsync(clientId, eventId, new RemoteEventOutcome("result", valEl), ct).ConfigureAwait(false);
    }

    public override async Task<Outcome<Unit>> ReplyOpenQuestionAsync(
        string clientId,
        string eventId,
        UserQuestionAnswerBatch answer,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentException.ThrowIfNullOrEmpty(eventId);
        ArgumentNullException.ThrowIfNull(answer);

        var dto = new WireAskUserQuestionAnswer(answer.Answers
            .Select(a => new WireAskUserQuestionAnswerItem(a.Id, a.Selected, a.Custom))
            .ToArray());
        var valEl = JsonSerializer.SerializeToElement(dto, AgentProtocolJsonContext.Default.WireAskUserQuestionAnswer);
        return await SendEventOutcomeAsync(clientId, eventId, new RemoteEventOutcome("result", valEl), ct).ConfigureAwait(false);
    }

    public override async Task<Outcome<Unit>> RejectOpenQuestionAsync(
        string clientId,
        string eventId,
        string reasonKey = "question.cancelled",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentException.ThrowIfNullOrEmpty(eventId);
        var outcome = new RemoteEventOutcome("rejected", Error: new RemoteEventRejection("UserQuestionCancelled", reasonKey, "E_QUESTION_CANCELLED"));
        return await SendEventOutcomeAsync(clientId, eventId, outcome, ct).ConfigureAwait(false);
    }

    public override async Task<Outcome<bool>> ReplyContinuedQuestionAsync(
        string sessionId,
        string callId,
        UserQuestionAnswerBatch answer,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentNullException.ThrowIfNull(answer);

        var args = JsonSerializer.SerializeToElement(
            new WireUserQuestionsAnswerArgs(
                sessionId,
                callId,
                new WireAskUserQuestionAnswer(answer.Answers
                    .Select(a => new WireAskUserQuestionAnswerItem(a.Id, a.Selected, a.Custom))
                    .ToArray())),
            AgentProtocolJsonContext.Default.WireUserQuestionsAnswerArgs);

        var rpc = await CallRawAsync("userQuestions/answer", args, ct).ConfigureAwait(false);
        if (!rpc.IsOk)
        {
            return Outcome<bool>.Fail(rpc.Error!);
        }
        return rpc.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? Outcome<bool>.Ok(rpc.Value.GetBoolean())
            : Outcome<bool>.Ok(true);
    }

    public override async Task<Outcome<ModelCatalogView>> GetModelCatalogAsync(CancellationToken ct = default)
    {
        var rpc = await CallRawAsync("session/modelCatalog", RemoteWireCodec.EmptyArgs, ct).ConfigureAwait(false);
        if (!rpc.IsOk)
        {
            return Outcome<ModelCatalogView>.Fail(rpc.Error!);
        }

        var dtoOutcome = DeserializeValue(rpc.Value, AgentProtocolJsonContext.Default.WireModelCatalog, "model_catalog.decode");
        if (!dtoOutcome.IsOk)
        {
            return Outcome<ModelCatalogView>.Fail(dtoOutcome.Error!);
        }
        var dto = dtoOutcome.Value!;

        var groups = dto.Groups.Select(g => new ModelProviderGroupView(
            g.Id,
            g.Name,
            g.Models.Select(m => new ModelInfoView(
                m.Id,
                m.Name,
                m.Description,
                (m.Reasoning?.Efforts ?? []).Select(e => new ModelEffortView(e.Id, e.Name, e.Description)).ToArray(),
                m.Reasoning?.DefaultEffort)).ToArray())).ToArray();

        var failures = dto.Failures.Select(f => new ModelProviderFailureView(f.Id, f.Name, f.Message)).ToArray();

        return Outcome<ModelCatalogView>.Ok(new ModelCatalogView(
            new ModelSelectionSpec(dto.Default.Provider, dto.Default.Model, dto.Default.ReasoningEffort),
            dto.RoutableProviders,
            groups,
            failures));
    }

    public override async Task<Outcome<ModelSelectionSpec>> SelectSessionModelAsync(
        string sessionId,
        ModelSelectionSpec selection,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(selection);
        var args = JsonSerializer.SerializeToElement(
            new WireSessionSelectModelArgs(new WireSessionSelectModelRequest(
                sessionId,
                selection.Provider,
                selection.Model,
                selection.ReasoningEffort)),
            AgentProtocolJsonContext.Default.WireSessionSelectModelArgs);

        var rpc = await CallRawAsync("session/selectModel", args, ct).ConfigureAwait(false);
        if (!rpc.IsOk)
        {
            return Outcome<ModelSelectionSpec>.Fail(rpc.Error!);
        }

        var dtoOutcome = DeserializeValue(rpc.Value, AgentProtocolJsonContext.Default.WireSessionSelectModelValue, "select_model.decode");
        return dtoOutcome.Map(dto => new ModelSelectionSpec(
            dto.Selected.Provider,
            dto.Selected.Model,
            dto.Selected.ReasoningEffort));
    }

    public override async Task<Outcome<IReadOnlyDictionary<string, CredentialStatusView>>> DescribeCredentialsAsync(
        IReadOnlyList<string> refs,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(refs);
        var args = JsonSerializer.SerializeToElement(
            new WireCredentialsDescribeArgs(refs),
            AgentProtocolJsonContext.Default.WireCredentialsDescribeArgs);

        var rpc = await CallRawAsync("credentials/describe", args, ct).ConfigureAwait(false);
        if (!rpc.IsOk)
        {
            return Outcome<IReadOnlyDictionary<string, CredentialStatusView>>.Fail(rpc.Error!);
        }

        var dtoOutcome = DeserializeValue(rpc.Value, AgentProtocolJsonContext.Default.DictionaryStringWireCredentialInfo, "credentials_describe.decode");
        if (!dtoOutcome.IsOk)
        {
            return Outcome<IReadOnlyDictionary<string, CredentialStatusView>>.Fail(dtoOutcome.Error!);
        }
        var map = dtoOutcome.Value!;

        var views = new Dictionary<string, CredentialStatusView>(map.Count, StringComparer.Ordinal);
        foreach (var (k, v) in map)
        {
            views[k] = new CredentialStatusView(k, v.Configured, v.Source, v.Writable, v.Masked);
        }
        return Outcome<IReadOnlyDictionary<string, CredentialStatusView>>.Ok(views);
    }

    public override async Task<Outcome<Unit>> SetCredentialAsync(string credentialRef, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(credentialRef);
        ArgumentNullException.ThrowIfNull(value);
        var args = JsonSerializer.SerializeToElement(
            new WireCredentialsSetArgs(credentialRef, value),
            AgentProtocolJsonContext.Default.WireCredentialsSetArgs);
        var rpc = await CallRawAsync("credentials/set", args, ct).ConfigureAwait(false);
        return rpc.IsOk ? Outcome<Unit>.Ok(Unit.Value) : Outcome<Unit>.Fail(rpc.Error!);
    }

    public override async Task<Outcome<Unit>> UnsetCredentialAsync(string credentialRef, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(credentialRef);
        var args = JsonSerializer.SerializeToElement(
            new WireCredentialsUnsetArgs(credentialRef),
            AgentProtocolJsonContext.Default.WireCredentialsUnsetArgs);
        var rpc = await CallRawAsync("credentials/unset", args, ct).ConfigureAwait(false);
        return rpc.IsOk ? Outcome<Unit>.Ok(Unit.Value) : Outcome<Unit>.Fail(rpc.Error!);
    }

    public override async Task<Outcome<PluginInventoryView>> GetPluginInventoryAsync(CancellationToken ct = default)
    {
        var rpc = await CallRawAsync("pluginInventory/list", RemoteWireCodec.EmptyArgs, ct).ConfigureAwait(false);
        if (!rpc.IsOk)
        {
            return Outcome<PluginInventoryView>.Fail(rpc.Error!);
        }

        var dtoOutcome = DeserializeValue(rpc.Value, AgentProtocolJsonContext.Default.WirePluginInventorySnapshot, "plugin_inventory.decode");
        if (!dtoOutcome.IsOk)
        {
            return Outcome<PluginInventoryView>.Fail(dtoOutcome.Error!);
        }
        var dto = dtoOutcome.Value!;

        var entries = dto.Entries.Select(e => new PluginInventoryEntryView(
            e.EntryId,
            e.ModuleName,
            e.Enabled,
            e.FiberPhase,
            RemoteProtocolCodec.ResolveLocalizedText(e.Meta?.Name) ?? RemoteProtocolCodec.ResolveLocalizedText(e.Meta?.Description),
            e.Diagnostic)).ToArray();

        return Outcome<PluginInventoryView>.Ok(new PluginInventoryView(dto.ManagementAvailable ?? false, entries));
    }

    public override async Task<Outcome<IAsyncDisposable>> FollowSessionAsync(
        string sessionId,
        Func<SessionFollowUpdate, ValueTask> onUpdate,
        int? maxMessages = null,
        bool activate = true,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(onUpdate);
        try
        {
            var args = JsonSerializer.SerializeToElement(
                new WireSessionFollowArgs(new WireSessionFollowRequest(
                    new WireSessionAddress("session", sessionId),
                    maxMessages,
                    activate,
                    AssistantStream: true)),
                AgentProtocolJsonContext.Default.WireSessionFollowArgs);

            var handle = await _connection.OpenStreamAsync(
                "session/follow",
                args,
                async frame =>
                {
                    if (frame.Type == "item" && frame.Value is { } val)
                    {
                        var decoded = RemoteProtocolCodec.DecodeFollowFrame(val);
                        await onUpdate(decoded).ConfigureAwait(false);
                    }
                },
                ct).ConfigureAwait(false);

            return Outcome<IAsyncDisposable>.Ok(handle);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Outcome<IAsyncDisposable>.Fail(DshFailureMapper.FromException(ex));
        }
    }

    private static Outcome<T> DeserializeValue<T>(
        JsonElement element,
        JsonTypeInfo<T> typeInfo,
        string diagnosticKey)
    {
        try
        {
            var value = element.Deserialize(typeInfo);
            return value is null
                ? Outcome<T>.Fail(DshFailureCode.ProtocolMismatch, diagnosticKey)
                : Outcome<T>.Ok(value);
        }
        catch (JsonException)
        {
            return Outcome<T>.Fail(DshFailureCode.ProtocolMismatch, diagnosticKey);
        }
    }

    private async Task<Outcome<JsonElement>> CallRawAsync(string endpoint, JsonElement args, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            var outcome = await _connection.CallAsync(endpoint, args, ct).ConfigureAwait(false);
            if (!outcome.Ok)
            {
                return Outcome<JsonElement>.Fail(DshFailureMapper.FromRpcError(outcome.Error));
            }
            return Outcome<JsonElement>.Ok(outcome.Value ?? default);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Outcome<JsonElement>.Fail(DshFailureMapper.FromException(ex));
        }
    }

    private async Task<Outcome<Unit>> SendEventOutcomeAsync(
        string clientId,
        string eventId,
        RemoteEventOutcome outcome,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            var res = await _connection.SendEventResultAsync(clientId, eventId, outcome, ct).ConfigureAwait(false);
            if (!res.Ok)
            {
                return Outcome<Unit>.Fail(DshFailureMapper.FromRpcError(res.Error));
            }
            return Outcome<Unit>.Ok(Unit.Value);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Outcome<Unit>.Fail(DshFailureMapper.FromException(ex));
        }
    }

    private ValueTask HandleForwardedEventAsync(RemoteForwardedEventPayload payload)
    {
        if (ForwardedEvent is { } handler)
        {
            var mapped = RemoteProtocolCodec.DecodeForwardedEvent(payload);
            handler(mapped);
        }
        return ValueTask.CompletedTask;
    }
}
