namespace DshMobile.Agent.Domain;

/// <summary>
/// Dynamic runtime capability matrix discovered via handshake and endpoint probing (never version-number guesses).
/// UI checks these flags exclusively instead of branching on transport internals (ADR-0033, N-02-02).
/// </summary>
public sealed record DshCapabilities(
    bool Prompt,
    bool PromptImages,
    bool SlashCommands,
    bool StreamingEvents,
    bool Approvals,
    bool UserQuestions,
    bool ResumeKnownSession,
    bool ListSessions,
    bool HistoryReplay,
    bool CancelSession,
    bool SwitchModelInSession,
    bool ManageCredentials,
    bool ServerPromptIdempotency,
    bool PluginInventory,
    bool PluginMutation,
    bool GitOperations,
    bool ArchiveSessions = false,
    bool PluginBundles = false,
    bool PluginSettings = false)
{
    public static readonly DshCapabilities Empty = new(
        Prompt: false,
        PromptImages: false,
        SlashCommands: false,
        StreamingEvents: false,
        Approvals: false,
        UserQuestions: false,
        ResumeKnownSession: false,
        ListSessions: false,
        HistoryReplay: false,
        CancelSession: false,
        SwitchModelInSession: false,
        ManageCredentials: false,
        ServerPromptIdempotency: false,
        PluginInventory: false,
        PluginMutation: false,
        GitOperations: false,
        ArchiveSessions: false,
        PluginBundles: false,
        PluginSettings: false);
}
