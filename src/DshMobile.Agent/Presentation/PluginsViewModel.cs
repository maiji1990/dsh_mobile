using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;

namespace DshMobile.Agent.Presentation;

/// <summary>原生插件目录与 L0/L1 管理页不可变状态快照（PR-07, N-07-02 / ADR-0033）。</summary>
public sealed record PluginsUiState(
    bool IsLoading,
    bool IsMutating,
    bool IsRestarting,
    bool IsHostReady,
    bool CanListPlugins,
    bool RequiresDshRestart,
    string SearchQuery,
    IReadOnlyList<PluginEntryView> Entries,
    string? FailureCode,
    string? DiagnosticSummary,
    string? StatusKey);

/// <summary>原生插件页向业务层派发的强类型动作契约（仅 L0 只读清单 + L1 装卸/启停，无 L2 Schema）。</summary>
public interface IPluginsActions
{
    Task RefreshAsync(bool includeCliPackages = true, CancellationToken ct = default);
    void SetSearchQuery(string? query);
    Task<Outcome<PluginMutationResult>> InstallPluginAsync(string packageSpec, CancellationToken ct = default);
    Task<Outcome<PluginMutationResult>> UninstallPluginAsync(string packageName, CancellationToken ct = default);
    Task<Outcome<PluginMutationResult>> SetPluginEnabledAsync(string entryId, string moduleName, bool enabled, CancellationToken ct = default);
    Task<Outcome<Unit>> RestartDshAsync(CancellationToken ct = default);
    void ClearFeedback();
}

/// <summary>
/// 纯托管插件目录与受控管理 ViewModel（PR-07, N-07-01 ～ N-07-02）：
/// 协调 <see cref="PluginInventoryService"/> 与连接状态，确保失败恢复不断链、错误信息全脱敏、零异常消息透传。
/// </summary>
public sealed class PluginsViewModel : IPluginsActions, IAsyncDisposable
{
    private readonly IDshGateway _gateway;
    private readonly PluginInventoryService _service;
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _opGate = new(1, 1);

    private IReadOnlyList<PluginEntryView> _allEntries = [];
    private PluginsUiState _state;
    private int _disposed;

    public PluginsViewModel(IDshGateway gateway, PluginInventoryService service)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _state = new PluginsUiState(
            IsLoading: false,
            IsMutating: false,
            IsRestarting: false,
            IsHostReady: false,
            CanListPlugins: gateway.Capabilities.PluginInventory,
            RequiresDshRestart: service.RequiresDshRestart,
            SearchQuery: string.Empty,
            Entries: [],
            FailureCode: null,
            DiagnosticSummary: null,
            StatusKey: null);
    }

    public PluginsUiState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public event Action<PluginsUiState>? StateChanged;

    /// <summary>宿主停机时刷新连接标志，保留既有列表与待重启提示状态。</summary>
    public void NotifyHostStopping()
    {
        PublishState(s => s with
        {
            IsHostReady = false,
            IsLoading = false,
        });
    }

    /// <summary>宿主完成新一轮启动并握手成功后调用，清除「需要重启 DSH 生效」提示。</summary>
    public void NotifyHostStarted()
    {
        _service.NotifyHostRestarted();
        PublishState(s => s with
        {
            IsHostReady = true,
            RequiresDshRestart = _service.RequiresDshRestart,
        });
    }

    public void ClearFeedback()
    {
        PublishState(s => s with
        {
            FailureCode = null,
            DiagnosticSummary = null,
            StatusKey = null,
        });
    }

    public async Task RefreshAsync(bool includeCliPackages = true, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PublishState(s => s with
            {
                IsLoading = true,
                FailureCode = null,
                DiagnosticSummary = null,
            });
            await RefreshCoreAsync(includeCliPackages, statusKeyOnSuccess: null, ct).ConfigureAwait(false);
        }
        finally
        {
            _opGate.Release();
        }
    }

    public void SetSearchQuery(string? query)
    {
        var normalized = query ?? string.Empty;
        lock (_stateLock)
        {
            if (string.Equals(_state.SearchQuery, normalized, StringComparison.Ordinal))
            {
                return;
            }
            _state = _state with { SearchQuery = normalized };
        }
        RebuildFilteredState(
            isLoading: State.IsLoading,
            isMutating: State.IsMutating,
            isRestarting: State.IsRestarting,
            isHostReady: State.IsHostReady,
            canList: State.CanListPlugins,
            failureCode: State.FailureCode,
            diagnosticSummary: State.DiagnosticSummary,
            statusKey: State.StatusKey);
    }

    public async Task<Outcome<PluginMutationResult>> InstallPluginAsync(string packageSpec, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var trimmedSpec = packageSpec?.Trim(' ');
        if (!PluginPackageValidator.IsValidInstallSpec(trimmedSpec))
        {
            PublishState(s => s with
            {
                IsMutating = false,
                FailureCode = DshFailureCode.InvalidState.ToString(),
                DiagnosticSummary = "plugin.invalid_package_spec",
                StatusKey = null,
            });
            return Outcome<PluginMutationResult>.Fail(DshFailureCode.InvalidState, "plugin.invalid_package_spec");
        }

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PublishState(s => s with { IsMutating = true, FailureCode = null, DiagnosticSummary = null, StatusKey = null });
            var res = await _service.InstallPluginAsync(trimmedSpec, ct).ConfigureAwait(false);
            if (!res.IsOk)
            {
                var failure = res.Error!;
                PublishState(s => s with
                {
                    IsMutating = false,
                    RequiresDshRestart = _service.RequiresDshRestart,
                    FailureCode = failure.Code.ToString(),
                    DiagnosticSummary = failure.DiagnosticKey,
                    StatusKey = null,
                });
                return res;
            }

            await RefreshCoreAsync(includeCliPackages: true, statusKeyOnSuccess: "plugins.status.installed", ct).ConfigureAwait(false);
            return res;
        }
        finally
        {
            PublishState(s => s.IsMutating ? s with { IsMutating = false } : s);
            _opGate.Release();
        }
    }

    public async Task<Outcome<PluginMutationResult>> UninstallPluginAsync(string packageName, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var trimmed = packageName?.Trim(' ');
        if (!PluginPackageValidator.IsValidPackageName(trimmed) || PluginPackageValidator.IsProtectedPackage(trimmed!))
        {
            PublishState(s => s with
            {
                IsMutating = false,
                FailureCode = DshFailureCode.InvalidState.ToString(),
                DiagnosticSummary = "plugin.invalid_package_name",
                StatusKey = null,
            });
            return Outcome<PluginMutationResult>.Fail(DshFailureCode.InvalidState, "plugin.invalid_package_name");
        }

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PublishState(s => s with { IsMutating = true, FailureCode = null, DiagnosticSummary = null, StatusKey = null });
            var res = await _service.UninstallPluginAsync(trimmed, ct).ConfigureAwait(false);
            if (!res.IsOk)
            {
                var failure = res.Error!;
                PublishState(s => s with
                {
                    IsMutating = false,
                    RequiresDshRestart = _service.RequiresDshRestart,
                    FailureCode = failure.Code.ToString(),
                    DiagnosticSummary = failure.DiagnosticKey,
                    StatusKey = null,
                });
                return res;
            }

            await RefreshCoreAsync(includeCliPackages: true, statusKeyOnSuccess: "plugins.status.uninstalled", ct).ConfigureAwait(false);
            return res;
        }
        finally
        {
            PublishState(s => s.IsMutating ? s with { IsMutating = false } : s);
            _opGate.Release();
        }
    }

    public async Task<Outcome<PluginMutationResult>> SetPluginEnabledAsync(
        string entryId,
        string moduleName,
        bool enabled,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!PluginPackageValidator.IsValidEntryId(entryId?.Trim()) ||
            PluginPackageValidator.IsProtectedEntry(entryId!.Trim(), moduleName))
        {
            PublishState(s => s with
            {
                IsMutating = false,
                FailureCode = DshFailureCode.InvalidState.ToString(),
                DiagnosticSummary = "plugin.protected_entry",
                StatusKey = null,
            });
            return Outcome<PluginMutationResult>.Fail(DshFailureCode.InvalidState, "plugin.protected_entry");
        }

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PublishState(s => s with { IsMutating = true, FailureCode = null, DiagnosticSummary = null, StatusKey = null });
            var res = await _service.SetPluginEnabledAsync(entryId, moduleName, enabled, ct).ConfigureAwait(false);
            if (!res.IsOk)
            {
                var failure = res.Error!;
                PublishState(s => s with
                {
                    IsMutating = false,
                    RequiresDshRestart = _service.RequiresDshRestart,
                    FailureCode = failure.Code.ToString(),
                    DiagnosticSummary = failure.DiagnosticKey,
                    StatusKey = null,
                });
                return res;
            }

            // 本地内存先行反映目标开关态（并保留重启提示），随后若宿主在线再拉取 L0 校准。
            lock (_stateLock)
            {
                _allEntries = _allEntries
                    .Select(e => string.Equals(e.EntryId, entryId.Trim(), StringComparison.Ordinal)
                        ? e with { Enabled = enabled }
                        : e)
                    .ToArray();
            }
            RebuildFilteredState(
                isLoading: false,
                isMutating: false,
                isRestarting: false,
                isHostReady: State.IsHostReady,
                canList: State.CanListPlugins,
                failureCode: null,
                diagnosticSummary: null,
                statusKey: enabled ? "plugins.status.enabled" : "plugins.status.disabled");
            return res;
        }
        finally
        {
            PublishState(s => s.IsMutating ? s with { IsMutating = false } : s);
            _opGate.Release();
        }
    }

    public async Task<Outcome<Unit>> RestartDshAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PublishState(s => s with { IsRestarting = true, FailureCode = null, DiagnosticSummary = null, StatusKey = null });
            var res = await _service.RestartDshHostAsync(ct).ConfigureAwait(false);
            if (!res.IsOk)
            {
                var failure = res.Error!;
                PublishState(s => s with
                {
                    IsRestarting = false,
                    RequiresDshRestart = _service.RequiresDshRestart,
                    FailureCode = failure.Code.ToString(),
                    DiagnosticSummary = failure.DiagnosticKey,
                    StatusKey = null,
                });
                return res;
            }

            await RefreshCoreAsync(includeCliPackages: true, statusKeyOnSuccess: "plugins.status.restarted", ct).ConfigureAwait(false);
            return Outcome<Unit>.Ok(Unit.Value);
        }
        finally
        {
            PublishState(s => s.IsRestarting ? s with { IsRestarting = false } : s);
            _opGate.Release();
        }
    }

    private async Task RefreshCoreAsync(bool includeCliPackages, string? statusKeyOnSuccess, CancellationToken ct)
    {
        if (!_gateway.Capabilities.PluginInventory)
        {
            var probed = await _gateway.ProbeCapabilitiesAsync(ct).ConfigureAwait(false);
            if (!probed.IsOk)
            {
                var failure = probed.Error!;
                PublishState(s => s with
                {
                    IsLoading = false,
                    IsMutating = false,
                    IsRestarting = false,
                    IsHostReady = false,
                    RequiresDshRestart = _service.RequiresDshRestart,
                    FailureCode = failure.Code.ToString(),
                    DiagnosticSummary = failure.DiagnosticKey,
                });
                return;
            }
            if (!probed.Value!.PluginInventory)
            {
                PublishState(s => s with
                {
                    IsLoading = false,
                    IsMutating = false,
                    IsRestarting = false,
                    IsHostReady = true,
                    CanListPlugins = false,
                    RequiresDshRestart = _service.RequiresDshRestart,
                    FailureCode = DshFailureCode.Unsupported.ToString(),
                    DiagnosticSummary = "plugins.unsupported",
                });
                return;
            }
        }

        var invOutcome = await _service.LoadInventoryAsync(includeCliPackages, ct).ConfigureAwait(false);
        if (!invOutcome.IsOk)
        {
            var failure = invOutcome.Error!;
            PublishState(s => s with
            {
                IsLoading = false,
                IsMutating = false,
                IsRestarting = false,
                IsHostReady = true,
                CanListPlugins = _gateway.Capabilities.PluginInventory,
                RequiresDshRestart = _service.RequiresDshRestart,
                FailureCode = failure.Code.ToString(),
                DiagnosticSummary = failure.DiagnosticKey,
            });
            return;
        }

        var snapshot = invOutcome.Value!;
        lock (_stateLock)
        {
            _allEntries = snapshot.Entries;
        }
        RebuildFilteredState(
            isLoading: false,
            isMutating: false,
            isRestarting: false,
            isHostReady: true,
            canList: true,
            failureCode: null,
            diagnosticSummary: snapshot.DiagnosticWarning,
            statusKey: statusKeyOnSuccess);
    }

    private void RebuildFilteredState(
        bool isLoading,
        bool isMutating,
        bool isRestarting,
        bool isHostReady,
        bool canList,
        string? failureCode,
        string? diagnosticSummary,
        string? statusKey)
    {
        PluginsUiState snapshot;
        lock (_stateLock)
        {
            var query = _state.SearchQuery.Trim();
            IReadOnlyList<PluginEntryView> filtered = string.IsNullOrEmpty(query)
                ? _allEntries
                : _allEntries.Where(e =>
                        e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        e.ModuleName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        e.PackageName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        e.EntryId.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

            snapshot = _state with
            {
                IsLoading = isLoading,
                IsMutating = isMutating,
                IsRestarting = isRestarting,
                IsHostReady = isHostReady,
                CanListPlugins = canList,
                RequiresDshRestart = _service.RequiresDshRestart,
                Entries = filtered,
                FailureCode = failureCode,
                DiagnosticSummary = diagnosticSummary,
                StatusKey = statusKey,
            };
            _state = snapshot;
        }
        StateChanged?.Invoke(snapshot);
    }

    private void PublishState(Func<PluginsUiState, PluginsUiState> transform)
    {
        PluginsUiState snapshot;
        lock (_stateLock)
        {
            snapshot = transform(_state);
            _state = snapshot;
        }
        StateChanged?.Invoke(snapshot);
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
