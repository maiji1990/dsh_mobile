namespace DshMobile.Agent.Domain;

/// <summary>Session lifecycle, listing, state inspection, and history replay facet (N-02-02).</summary>
public interface ISessionGateway
{
    Task<Outcome<IReadOnlyList<SessionSummaryView>>> ListSessionsAsync(CancellationToken ct = default);
    Task<Outcome<string>> CreateOrResumeSessionAsync(CreateOrResumeSessionRequest request, CancellationToken ct = default);
    Task<Outcome<SessionStateView>> GetSessionStateAsync(string sessionId, CancellationToken ct = default);
    Task<Outcome<SessionPageView>> LoadSessionEventsAsync(string sessionId, long? throughSeq = null, long? beforeSeq = null, int? maxMessages = null, CancellationToken ct = default);
    Task<Outcome<SessionWaitResultView>> WaitForSessionAsync(string sessionId, TimeSpan? timeout = null, long? afterSeq = null, CancellationToken ct = default);
    Task<Outcome<string>> RenameSessionAsync(string sessionId, string title, CancellationToken ct = default);
    Task<Outcome<string>> ForkSessionAsync(string sessionId, long? atSeq = null, CancellationToken ct = default);
    Task<Outcome<IReadOnlyList<string>>> ArchiveSessionAsync(string sessionId, bool stopActivity = false, CancellationToken ct = default);
    Task<Outcome<IReadOnlyList<string>>> UnarchiveSessionAsync(string sessionId, CancellationToken ct = default);
    Task<Outcome<IReadOnlyList<string>>> GetArchivedSessionIdsAsync(CancellationToken ct = default);
}

/// <summary>Prompt dispatch, native slash commands, queue control, and active turn cancellation facet (N-02-02).</summary>
public interface IPromptGateway
{
    Task<Outcome<Unit>> SendPromptAsync(PromptSendRequest request, CancellationToken ct = default);
    Task<Outcome<IReadOnlyList<SlashCommandDescriptorView>>> ListCommandsAsync(string sessionId, CancellationToken ct = default);
    Task<Outcome<SlashCommandExecutionView?>> ExecuteCommandAsync(string sessionId, string commandLine, IReadOnlyList<PromptAttachmentImage>? images = null, CancellationToken ct = default);
    Task<Outcome<Unit>> CancelTurnAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>DSH guest-tool approval waterfall responder facet (strictly isolated from Relay ui_*).</summary>
public interface IApprovalGateway
{
    Task<Outcome<Unit>> ReplyApprovalAsync(string clientId, string eventId, ApprovalDecision decision, CancellationToken ct = default);
}

/// <summary>User-questions waterfall and continued-answer facet.</summary>
public interface IQuestionGateway
{
    Task<Outcome<Unit>> ReplyOpenQuestionAsync(string clientId, string eventId, UserQuestionAnswerBatch answer, CancellationToken ct = default);
    Task<Outcome<Unit>> RejectOpenQuestionAsync(string clientId, string eventId, string reasonKey = "question.cancelled", CancellationToken ct = default);
    Task<Outcome<bool>> ReplyContinuedQuestionAsync(string sessionId, string callId, UserQuestionAnswerBatch answer, CancellationToken ct = default);
}

/// <summary>Model catalog & session model selection facet.</summary>
public interface IProviderGateway
{
    Task<Outcome<ModelCatalogView>> GetModelCatalogAsync(CancellationToken ct = default);
    Task<Outcome<ModelSelectionSpec>> SelectSessionModelAsync(string sessionId, ModelSelectionSpec selection, CancellationToken ct = default);
}

/// <summary>Credential status & secret injection facet (never exposes secret values back).</summary>
public interface ICredentialGateway
{
    Task<Outcome<IReadOnlyList<string>>> DiscoverCredentialRefsAsync(CancellationToken ct = default);
    Task<Outcome<IReadOnlyDictionary<string, CredentialStatusView>>> DescribeCredentialsAsync(IReadOnlyList<string> refs, CancellationToken ct = default);
    Task<Outcome<Unit>> SetCredentialAsync(string credentialRef, string value, CancellationToken ct = default);
    Task<Outcome<Unit>> UnsetCredentialAsync(string credentialRef, CancellationToken ct = default);
}

/// <summary>Read-only Host plugin inventory facet.</summary>
public interface IPluginGateway
{
    Task<Outcome<PluginInventoryView>> GetPluginInventoryAsync(CancellationToken ct = default);
}

/// <summary>Live session follow stream & forwarded Host event stream facet.</summary>
public interface IEventGateway
{
    event Action<ForwardedGatewayEvent>? ForwardedEvent;
    Task<Outcome<IAsyncDisposable>> FollowSessionAsync(string sessionId, Func<SessionFollowUpdate, ValueTask> onUpdate, int? maxMessages = null, bool activate = true, CancellationToken ct = default);
    Task<Outcome<IAsyncDisposable>> FollowWorkspaceAsync(Func<WorkspaceFollowFrame, ValueTask> onFrame, CancellationToken ct = default);
}

/// <summary>Composite protocol-agnostic gateway exposing capabilities and all 8 Facets.</summary>
public interface IDshGateway :
    ISessionGateway,
    IPromptGateway,
    IApprovalGateway,
    IQuestionGateway,
    IProviderGateway,
    ICredentialGateway,
    IPluginGateway,
    IEventGateway
{
    DshCapabilities Capabilities { get; }
    Task<Outcome<DshCapabilities>> ProbeCapabilitiesAsync(CancellationToken ct = default);
}

/// <summary>
/// Lazy default implementation returning <see cref="DshFailureCode.Unavailable"/> for all Facets (N-02-02).
/// Protocol adapters override supported Facets and inherit uniform fail-closed defaults for unsupported ones.
/// </summary>
public abstract class DefaultDshGateway : IDshGateway
{
    public virtual DshCapabilities Capabilities => DshCapabilities.Empty;

    public virtual event Action<ForwardedGatewayEvent>? ForwardedEvent
    {
        add { }
        remove { }
    }

    protected static Task<Outcome<T>> Unavailable<T>() =>
        Task.FromResult(Outcome<T>.Fail(DshFailureCode.Unavailable));

    public virtual Task<Outcome<DshCapabilities>> ProbeCapabilitiesAsync(CancellationToken ct = default) => Unavailable<DshCapabilities>();
    public virtual Task<Outcome<IReadOnlyList<SessionSummaryView>>> ListSessionsAsync(CancellationToken ct = default) => Unavailable<IReadOnlyList<SessionSummaryView>>();
    public virtual Task<Outcome<string>> CreateOrResumeSessionAsync(CreateOrResumeSessionRequest request, CancellationToken ct = default) => Unavailable<string>();
    public virtual Task<Outcome<SessionStateView>> GetSessionStateAsync(string sessionId, CancellationToken ct = default) => Unavailable<SessionStateView>();
    public virtual Task<Outcome<SessionPageView>> LoadSessionEventsAsync(string sessionId, long? throughSeq = null, long? beforeSeq = null, int? maxMessages = null, CancellationToken ct = default) => Unavailable<SessionPageView>();
    public virtual Task<Outcome<SessionWaitResultView>> WaitForSessionAsync(string sessionId, TimeSpan? timeout = null, long? afterSeq = null, CancellationToken ct = default) => Unavailable<SessionWaitResultView>();
    public virtual Task<Outcome<string>> RenameSessionAsync(string sessionId, string title, CancellationToken ct = default) => Unavailable<string>();
    public virtual Task<Outcome<string>> ForkSessionAsync(string sessionId, long? atSeq = null, CancellationToken ct = default) => Unavailable<string>();
    public virtual Task<Outcome<IReadOnlyList<string>>> ArchiveSessionAsync(string sessionId, bool stopActivity = false, CancellationToken ct = default) => Unavailable<IReadOnlyList<string>>();
    public virtual Task<Outcome<IReadOnlyList<string>>> UnarchiveSessionAsync(string sessionId, CancellationToken ct = default) => Unavailable<IReadOnlyList<string>>();
    public virtual Task<Outcome<IReadOnlyList<string>>> GetArchivedSessionIdsAsync(CancellationToken ct = default) => Unavailable<IReadOnlyList<string>>();
    public virtual Task<Outcome<Unit>> SendPromptAsync(PromptSendRequest request, CancellationToken ct = default) => Unavailable<Unit>();
    public virtual Task<Outcome<IReadOnlyList<SlashCommandDescriptorView>>> ListCommandsAsync(string sessionId, CancellationToken ct = default) => Unavailable<IReadOnlyList<SlashCommandDescriptorView>>();
    public virtual Task<Outcome<SlashCommandExecutionView?>> ExecuteCommandAsync(string sessionId, string commandLine, IReadOnlyList<PromptAttachmentImage>? images = null, CancellationToken ct = default) => Unavailable<SlashCommandExecutionView?>();
    public virtual Task<Outcome<Unit>> CancelTurnAsync(string sessionId, CancellationToken ct = default) => Unavailable<Unit>();
    public virtual Task<Outcome<Unit>> ReplyApprovalAsync(string clientId, string eventId, ApprovalDecision decision, CancellationToken ct = default) => Unavailable<Unit>();
    public virtual Task<Outcome<Unit>> ReplyOpenQuestionAsync(string clientId, string eventId, UserQuestionAnswerBatch answer, CancellationToken ct = default) => Unavailable<Unit>();
    public virtual Task<Outcome<Unit>> RejectOpenQuestionAsync(string clientId, string eventId, string reasonKey = "question.cancelled", CancellationToken ct = default) => Unavailable<Unit>();
    public virtual Task<Outcome<bool>> ReplyContinuedQuestionAsync(string sessionId, string callId, UserQuestionAnswerBatch answer, CancellationToken ct = default) => Unavailable<bool>();
    public virtual Task<Outcome<ModelCatalogView>> GetModelCatalogAsync(CancellationToken ct = default) => Unavailable<ModelCatalogView>();
    public virtual Task<Outcome<ModelSelectionSpec>> SelectSessionModelAsync(string sessionId, ModelSelectionSpec selection, CancellationToken ct = default) => Unavailable<ModelSelectionSpec>();
    public virtual Task<Outcome<IReadOnlyList<string>>> DiscoverCredentialRefsAsync(CancellationToken ct = default) => Unavailable<IReadOnlyList<string>>();
    public virtual Task<Outcome<IReadOnlyDictionary<string, CredentialStatusView>>> DescribeCredentialsAsync(IReadOnlyList<string> refs, CancellationToken ct = default) => Unavailable<IReadOnlyDictionary<string, CredentialStatusView>>();
    public virtual Task<Outcome<Unit>> SetCredentialAsync(string credentialRef, string value, CancellationToken ct = default) => Unavailable<Unit>();
    public virtual Task<Outcome<Unit>> UnsetCredentialAsync(string credentialRef, CancellationToken ct = default) => Unavailable<Unit>();
    public virtual Task<Outcome<PluginInventoryView>> GetPluginInventoryAsync(CancellationToken ct = default) => Unavailable<PluginInventoryView>();
    public virtual Task<Outcome<IAsyncDisposable>> FollowSessionAsync(string sessionId, Func<SessionFollowUpdate, ValueTask> onUpdate, int? maxMessages = null, bool activate = true, CancellationToken ct = default) => Unavailable<IAsyncDisposable>();
    public virtual Task<Outcome<IAsyncDisposable>> FollowWorkspaceAsync(Func<WorkspaceFollowFrame, ValueTask> onFrame, CancellationToken ct = default) => Unavailable<IAsyncDisposable>();
}
