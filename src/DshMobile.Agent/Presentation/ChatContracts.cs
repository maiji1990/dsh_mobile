using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;

namespace DshMobile.Agent.Presentation;

/// <summary>纯原生聊天页状态不可变快照（N-04-01），由连接作用域内的 <see cref="ChatViewModel"/> 驱动。</summary>
public sealed record ChatUiState(
    SessionKey ActiveSession,
    string SessionTitle,
    string? ModelLabel,
    bool IsConnected,
    bool IsReconciling,
    bool IsSending,
    string? StatusKey,
    string? FailureCode,
    DshCapabilities Capabilities,
    IReadOnlyList<SlashCommandDescriptorView> SlashCommands,
    ChatSnapshot Transcript,
    IReadOnlyList<PendingInteractionCard> PendingRequests,
    OutboundSendEntry? LastSend,
    ModelSelectionSpec? CurrentModel = null,
    IReadOnlyList<ModelProviderGroupView>? ModelGroups = null,
    bool NeedsCredentialOnboarding = false,
    bool IsLoadingOlderHistory = false)
{
    public bool CanSend => IsConnected && Capabilities.Prompt && !IsSending;
}

/// <summary>聊天界面向业务层派发的强类型动作契约（N-04-01 / PR-05）。</summary>
public interface IChatActions
{
    Task LoadCachedSessionAsync(string sessionId, CancellationToken ct = default);
    Task<bool> LoadMostRecentCachedSessionAsync(CancellationToken ct = default);
    Task ConnectAndAttachAsync(string? preferredSessionId = null, CancellationToken ct = default);
    Task SendTextAsync(string text, CancellationToken ct = default);
    Task SendWithImagesAsync(string text, IReadOnlyList<PromptAttachmentImage> images, CancellationToken ct = default);
    Task RetryOrConfirmSendAsync(string clientOpId, PromptSendRequest originalRequest, bool userConfirmedResend, CancellationToken ct = default);
    Task RunSlashCommandAsync(string commandLine, IReadOnlyList<PromptAttachmentImage>? images = null, CancellationToken ct = default);
    Task AnswerApprovalAsync(string eventId, ApprovalDecision decision, CancellationToken ct = default);
    Task UndoApprovalAsync(string eventId, CancellationToken ct = default);
    Task AnswerQuestionAsync(string eventId, UserQuestionAnswerBatch answer, CancellationToken ct = default);
    Task CancelQuestionAsync(string eventId, CancellationToken ct = default);
    Task CancelSessionAsync(CancellationToken ct = default);
    Task RefreshHistoryAsync(CancellationToken ct = default);
    Task LoadOlderHistoryAsync(CancellationToken ct = default);
    Task SwitchOrCreateSessionAsync(string? sessionId = null, CancellationToken ct = default);
    Task RefreshModelCatalogAsync(CancellationToken ct = default);
    Task SelectModelAsync(ModelSelectionSpec selection, CancellationToken ct = default);
    Task RefreshCredentialReadinessAsync(CancellationToken ct = default);
    Task FlushPendingHoldsAsync(CancellationToken ct = default);
}
