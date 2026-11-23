using DshMobile.Agent.Domain;
using DshMobile.Agent.Protocol;

namespace DshMobile.Agent.Presentation;

/// <summary>单条凭据状态视图（只含脱敏展示，绝不携带明文密钥，N-05-01）。</summary>
public sealed record CredentialEntryView(
    string Ref,
    string DisplayName,
    bool Configured,
    string? Source,
    bool Writable,
    string? Masked,
    bool IsDefaultPrimary);

/// <summary>原生凭据与 Provider 配置页的不可变状态快照（N-05-01）。</summary>
public sealed record CredentialsUiState(
    bool IsLoading,
    bool IsSaving,
    bool CanManageCredentials,
    IReadOnlyList<CredentialEntryView> Entries,
    string? FailureCode,
    string? StatusKey)
{
    public bool HasAnyConfigured => Entries.Any(e => e.Configured);
}

/// <summary>凭据配置界面向业务层派发的强类型动作契约（N-05-01）。</summary>
public interface ICredentialActions
{
    Task RefreshAsync(CancellationToken ct = default);
    Task SaveCredentialAsync(string credentialRef, string rawValue, CancellationToken ct = default);
    Task ClearCredentialAsync(string credentialRef, CancellationToken ct = default);
}

/// <summary>
/// 纯托管凭据配置 ViewModel（N-05-01）：严格经官方 <see cref="ICredentialGateway"/> 查询与写入，
/// 不回显明文、不写日志、不落入恢复态。
/// </summary>
public sealed class CredentialsViewModel : ICredentialActions, IAsyncDisposable
{
    private const int MaxDescribeRefs = 64;

    private static readonly string[] FallbackRefs =
    [
        "DEEPSEEK_API_KEY",
        "OPENAI_API_KEY",
        "ANTHROPIC_API_KEY",
        "GEMINI_API_KEY",
        "OPENROUTER_API_KEY",
    ];

    private readonly IDshGateway _gateway;
    private readonly string? _customRefsPath;
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _opGate = new(1, 1);
    private readonly HashSet<string> _customRefs = new(StringComparer.Ordinal);
    private readonly List<string> _customRefsOrder = [];

    private CredentialsUiState _state;
    private int _disposed;

    public CredentialsViewModel(IDshGateway gateway, string? customRefsPath = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _customRefsPath = string.IsNullOrWhiteSpace(customRefsPath) ? null : customRefsPath;
        LoadPersistedCustomRefs();
        _state = new CredentialsUiState(
            IsLoading: false,
            IsSaving: false,
            CanManageCredentials: gateway.Capabilities.ManageCredentials,
            Entries: [],
            FailureCode: null,
            StatusKey: null);
    }

    public CredentialsUiState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public event Action<CredentialsUiState>? StateChanged;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PublishState(s => s with { IsLoading = true, FailureCode = null });
            await RefreshInternalAsync(statusKeyOnSuccess: null, ct).ConfigureAwait(false);
        }
        finally
        {
            _opGate.Release();
        }
    }

    public async Task SaveCredentialAsync(string credentialRef, string rawValue, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(credentialRef) || string.IsNullOrWhiteSpace(rawValue))
        {
            PublishState(s => s with { FailureCode = DshFailureCode.InvalidState.ToString() });
            return;
        }

        var trimmedRef = credentialRef.Trim();
        var secret = rawValue.Trim();
        if (!RemoteDshGatewayClient.IsValidCredentialRef(trimmedRef))
        {
            PublishState(s => s with { FailureCode = DshFailureCode.InvalidState.ToString() });
            return;
        }

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var capsOutcome = await EnsureCredentialCapabilityAsync(ct).ConfigureAwait(false);
            if (!capsOutcome.IsOk)
            {
                PublishState(s => s with
                {
                    IsSaving = false,
                    CanManageCredentials = false,
                    FailureCode = capsOutcome.Error!.DiagnosticKey ?? capsOutcome.Error.Code.ToString(),
                });
                return;
            }

            PublishState(s => s with { IsSaving = true, FailureCode = null, StatusKey = null });
            var setOutcome = await _gateway.SetCredentialAsync(trimmedRef, secret, ct).ConfigureAwait(false);
            if (!setOutcome.IsOk)
            {
                PublishState(s => s with
                {
                    IsSaving = false,
                    FailureCode = setOutcome.Error!.DiagnosticKey ?? setOutcome.Error.Code.ToString(),
                });
                return;
            }

            // F16：保存自定义引用成功后立即纳入已知引用清单并持久化引用名（绝不写密钥值），刷新即可查看与清除。
            AddCustomRef(trimmedRef, persist: true);
            await RefreshInternalAsync(statusKeyOnSuccess: "credentials.status.saved", ct).ConfigureAwait(false);
        }
        finally
        {
            _opGate.Release();
        }
    }

    public async Task ClearCredentialAsync(string credentialRef, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(credentialRef))
        {
            PublishState(s => s with { FailureCode = DshFailureCode.InvalidState.ToString() });
            return;
        }

        var trimmedRef = credentialRef.Trim();
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var capsOutcome = await EnsureCredentialCapabilityAsync(ct).ConfigureAwait(false);
            if (!capsOutcome.IsOk)
            {
                PublishState(s => s with
                {
                    IsSaving = false,
                    CanManageCredentials = false,
                    FailureCode = capsOutcome.Error!.DiagnosticKey ?? capsOutcome.Error.Code.ToString(),
                });
                return;
            }

            PublishState(s => s with { IsSaving = true, FailureCode = null, StatusKey = null });
            var unsetOutcome = await _gateway.UnsetCredentialAsync(trimmedRef, ct).ConfigureAwait(false);
            if (!unsetOutcome.IsOk)
            {
                PublishState(s => s with
                {
                    IsSaving = false,
                    FailureCode = unsetOutcome.Error!.DiagnosticKey ?? unsetOutcome.Error.Code.ToString(),
                });
                return;
            }

            await RefreshInternalAsync(statusKeyOnSuccess: "credentials.status.cleared", ct).ConfigureAwait(false);
        }
        finally
        {
            _opGate.Release();
        }
    }

    private async Task<Outcome<DshCapabilities>> EnsureCredentialCapabilityAsync(CancellationToken ct)
    {
        if (_gateway.Capabilities.ManageCredentials)
        {
            return Outcome<DshCapabilities>.Ok(_gateway.Capabilities);
        }
        var probed = await _gateway.ProbeCapabilitiesAsync(ct).ConfigureAwait(false);
        if (!probed.IsOk)
        {
            return probed;
        }
        if (!probed.Value!.ManageCredentials)
        {
            return Outcome<DshCapabilities>.Fail(DshFailureCode.Unsupported, "credentials.unsupported");
        }
        return probed;
    }

    private async Task RefreshInternalAsync(string? statusKeyOnSuccess, CancellationToken ct)
    {
        var capsOutcome = await EnsureCredentialCapabilityAsync(ct).ConfigureAwait(false);
        if (!capsOutcome.IsOk)
        {
            PublishState(s => s with
            {
                IsLoading = false,
                IsSaving = false,
                CanManageCredentials = false,
                FailureCode = capsOutcome.Error!.DiagnosticKey ?? capsOutcome.Error.Code.ToString(),
            });
            return;
        }

        var discoverOutcome = await _gateway.DiscoverCredentialRefsAsync(ct).ConfigureAwait(false);
        var baseRefs = discoverOutcome.IsOk && discoverOutcome.Value is { Count: > 0 }
            ? discoverOutcome.Value
            : FallbackRefs;
        var mergedRefs = MergeRefsWithCustom(baseRefs);

        var descOutcome = await _gateway.DescribeCredentialsAsync(mergedRefs, ct).ConfigureAwait(false);
        if (!descOutcome.IsOk)
        {
            PublishState(s => s with
            {
                IsLoading = false,
                IsSaving = false,
                CanManageCredentials = true,
                FailureCode = descOutcome.Error!.DiagnosticKey ?? descOutcome.Error.Code.ToString(),
            });
            return;
        }

        var map = descOutcome.Value!;
        var entries = new List<CredentialEntryView>(mergedRefs.Count + map.Count);
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in mergedRefs)
        {
            if (added.Add(r))
            {
                map.TryGetValue(r, out var info);
                entries.Add(ToEntryView(r, info));
            }
        }
        foreach (var (r, info) in map)
        {
            if (added.Add(r))
            {
                entries.Add(ToEntryView(r, info));
            }
        }

        PublishState(s => s with
        {
            IsLoading = false,
            IsSaving = false,
            CanManageCredentials = true,
            Entries = entries,
            FailureCode = null,
            StatusKey = statusKeyOnSuccess,
        });
    }

    private List<string> MergeRefsWithCustom(IReadOnlyList<string> discovered)
    {
        var merged = new List<string>(discovered.Count + _customRefsOrder.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in discovered)
        {
            if (merged.Count >= MaxDescribeRefs)
            {
                break;
            }
            if (RemoteDshGatewayClient.IsValidCredentialRef(r) && seen.Add(r))
            {
                merged.Add(r);
            }
        }
        foreach (var custom in _customRefsOrder)
        {
            if (merged.Count >= MaxDescribeRefs)
            {
                break;
            }
            if (seen.Add(custom))
            {
                merged.Add(custom);
            }
        }
        return merged;
    }

    private void AddCustomRef(string refName, bool persist)
    {
        if (!RemoteDshGatewayClient.IsValidCredentialRef(refName))
        {
            return;
        }
        if (_customRefs.Add(refName))
        {
            _customRefsOrder.Add(refName);
            if (persist)
            {
                PersistCustomRefs();
            }
        }
    }

    private void LoadPersistedCustomRefs()
    {
        if (_customRefsPath is null || !File.Exists(_customRefsPath))
        {
            return;
        }
        try
        {
            foreach (var line in File.ReadLines(_customRefsPath))
            {
                var candidate = line.Trim();
                if (RemoteDshGatewayClient.IsValidCredentialRef(candidate) && _customRefs.Add(candidate))
                {
                    _customRefsOrder.Add(candidate);
                }
            }
        }
        catch (IOException ex)
        {
            _ = ex;
        }
        catch (UnauthorizedAccessException ex)
        {
            _ = ex;
        }
    }

    private void PersistCustomRefs()
    {
        if (_customRefsPath is null)
        {
            return;
        }
        try
        {
            var dir = Path.GetDirectoryName(_customRefsPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var tmp = $"{_customRefsPath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllLines(tmp, _customRefsOrder);
            File.Move(tmp, _customRefsPath, overwrite: true);
        }
        catch (IOException ex)
        {
            _ = ex;
        }
        catch (UnauthorizedAccessException ex)
        {
            _ = ex;
        }
    }

    private static CredentialEntryView ToEntryView(string credentialRef, CredentialStatusView? info)
    {
        var displayName = credentialRef switch
        {
            "DEEPSEEK_API_KEY" => "DeepSeek API Key",
            "OPENAI_API_KEY" => "OpenAI API Key",
            "ANTHROPIC_API_KEY" => "Anthropic API Key",
            "GEMINI_API_KEY" => "Google Gemini API Key",
            "OPENROUTER_API_KEY" => "OpenRouter API Key",
            _ => credentialRef,
        };
        return new CredentialEntryView(
            Ref: credentialRef,
            DisplayName: displayName,
            Configured: info?.Configured ?? false,
            Source: info?.Source,
            Writable: info?.Writable ?? true,
            Masked: info?.Masked,
            IsDefaultPrimary: string.Equals(credentialRef, "DEEPSEEK_API_KEY", StringComparison.Ordinal));
    }

    private void PublishState(Func<CredentialsUiState, CredentialsUiState> transform)
    {
        CredentialsUiState next;
        lock (_stateLock)
        {
            next = transform(_state);
            if (ReferenceEquals(next, _state) || next == _state)
            {
                return;
            }
            _state = next;
        }
        StateChanged?.Invoke(next);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }
        _opGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
