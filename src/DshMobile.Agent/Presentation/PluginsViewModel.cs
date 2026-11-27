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
    string? StatusKey,
    PluginTabMode ActiveTab = PluginTabMode.Installed,
    PluginFilterMode FilterMode = PluginFilterMode.All,
    int TotalInstalledCount = 0,
    string CatalogQuery = "",
    bool IsSearchingCatalog = false,
    bool CanSearchCatalog = false,
    int CatalogTotalCount = 0,
    DshFailureCode? CatalogFailureCode = null,
    string? CatalogDiagnosticSummary = null,
    bool IsFullWebUiCompatEnabled = false,
    bool IsCheckingUpdates = false,
    int UpdatableCount = 0,
    long? LastUpdateCheckAtUnixMs = null,
    string? UpdateCheckStatusKey = null,
    string? UpdateCheckFailureCode = null,
    string? UpdateCheckDiagnostic = null,
    bool CanCheckUpdates = false)
{
    /// <summary>远端发现页搜索到的插件条目（已实时对齐本地已安装状态）。</summary>
    public IReadOnlyList<PluginCatalogEntryView> CatalogEntries { get; init; } = [];
}

/// <summary>原生插件页向业务层派发的强类型动作契约（仅 L0 只读清单 + L1 装卸/启停 + 远端发现搜索）。</summary>
public interface IPluginsActions
{
    Task RefreshAsync(bool includeCliPackages = true, CancellationToken ct = default);
    void SetSearchQuery(string? query);
    void SetActiveTab(PluginTabMode tab);
    void SetFilterMode(PluginFilterMode filter);
    void SetCatalogQuery(string? query);
    Task SearchCatalogAsync(string? query, CancellationToken ct = default);
    Task<Outcome<PluginMutationResult>> InstallPluginAsync(string packageSpec, CancellationToken ct = default);
    Task<Outcome<PluginMutationResult>> UninstallPluginAsync(string packageName, CancellationToken ct = default);
    Task<Outcome<PluginMutationResult>> SetPluginEnabledAsync(string entryId, string moduleName, bool enabled, CancellationToken ct = default);
    Task<Outcome<Unit>> RestartDshAsync(CancellationToken ct = default);

    /// <summary>检查用户第三方插件是否有新版本；失败时保留既有列表并记录脱敏诊断。</summary>
    Task CheckUpdatesAsync(CancellationToken ct = default);

    /// <summary>将指定插件升级到精确版本；成功后保留“需要重启 DSH 生效”提示。</summary>
    Task<Outcome<PluginMutationResult>> UpgradePluginAsync(string packageName, string targetVersion, CancellationToken ct = default);

    void ClearFeedback();
}

/// <summary>
/// 纯托管插件目录与受控管理 ViewModel（PR-07, N-07-01 ～ N-07-02）：
/// 协调 <see cref="PluginInventoryService"/>、远端发现搜索 <see cref="IPluginCatalogSearch"/> 与连接状态，
/// 确保本地多维搜索与排序准确、并发远端搜索可取消、失败恢复不断链、错误信息全脱敏、零异常消息透传。
/// </summary>
public sealed class PluginsViewModel : IPluginsActions, IAsyncDisposable
{
    /// <summary>当用户切入发现页且未输入关键词时的默认推荐搜索词。</summary>
    public const string DefaultCatalogDiscoveryQuery = NpmRegistryPluginSearchService.DefaultDiscoveryQuery;

    private const int DefaultCatalogSearchLimit = 25;

    private readonly IDshGateway _gateway;
    private readonly PluginInventoryService _service;
    private readonly IPluginCatalogSearch _catalogSearch;
    private readonly bool _canSearchCatalog;
    private readonly bool _canCheckUpdates;
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _opGate = new(1, 1);
    private readonly SemaphoreSlim _updateGate = new(1, 1);

    private IReadOnlyList<PluginEntryView> _allEntries = [];
    private IReadOnlyList<PluginCatalogEntryView> _rawCatalogEntries = [];
    private readonly Dictionary<string, string> _latestVersionsByPackage = new(StringComparer.Ordinal);
    private CancellationTokenSource? _catalogSearchCts;
    private CancellationTokenSource? _updateCts;
    private long _catalogSearchSequence;
    private long _updateSequence;
    private PluginsUiState _state;
    private int _disposed;

    public PluginsViewModel(
        IDshGateway gateway,
        PluginInventoryService service,
        IPluginCatalogSearch? catalogSearch = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _catalogSearch = catalogSearch ?? NoopPluginCatalogSearch.Instance;
        _canSearchCatalog = _catalogSearch is not NoopPluginCatalogSearch;
        _canCheckUpdates = _canSearchCatalog;
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
            StatusKey: null,
            ActiveTab: PluginTabMode.Installed,
            FilterMode: PluginFilterMode.All,
            TotalInstalledCount: 0,
            CatalogQuery: string.Empty,
            IsSearchingCatalog: false,
            CanSearchCatalog: _canSearchCatalog,
            CatalogTotalCount: 0,
            CatalogFailureCode: null,
            CatalogDiagnosticSummary: null,
            IsFullWebUiCompatEnabled: service.IsFullWebUiCompatEnabled,
            CanCheckUpdates: _canCheckUpdates);
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
            IsFullWebUiCompatEnabled = _service.IsFullWebUiCompatEnabled,
        });
    }

    public void ClearFeedback()
    {
        PublishState(s => s with
        {
            FailureCode = null,
            DiagnosticSummary = null,
            StatusKey = null,
            CatalogFailureCode = null,
            CatalogDiagnosticSummary = null,
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
        RebuildFilteredStateFromCurrentFlags();
    }

    public void SetActiveTab(PluginTabMode tab)
    {
        PluginsUiState? snapshot = null;
        lock (_stateLock)
        {
            if (_state.ActiveTab == tab)
            {
                return;
            }
            snapshot = _state with { ActiveTab = tab };
            _state = snapshot;
        }
        StateChanged?.Invoke(snapshot);
    }

    public void SetFilterMode(PluginFilterMode filter)
    {
        lock (_stateLock)
        {
            if (_state.FilterMode == filter)
            {
                return;
            }
            _state = _state with { FilterMode = filter };
        }
        RebuildFilteredStateFromCurrentFlags();
    }

    public void SetCatalogQuery(string? query)
    {
        var normalized = query ?? string.Empty;
        PluginsUiState? snapshot = null;
        lock (_stateLock)
        {
            if (string.Equals(_state.CatalogQuery, normalized, StringComparison.Ordinal))
            {
                return;
            }
            snapshot = _state with { CatalogQuery = normalized };
            _state = snapshot;
        }
        StateChanged?.Invoke(snapshot);
    }

    public async Task SearchCatalogAsync(string? query, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        string displayQuery;
        string effectiveQuery;
        long requestSequence;
        CancellationTokenSource? previousCts;
        CancellationTokenSource currentCts;

        lock (_stateLock)
        {
            displayQuery = query is not null
                ? query.Trim()
                : _state.CatalogQuery.Trim();
            effectiveQuery = string.IsNullOrEmpty(displayQuery)
                ? DefaultCatalogDiscoveryQuery
                : displayQuery;

            requestSequence = ++_catalogSearchSequence;
            previousCts = _catalogSearchCts;
            currentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _catalogSearchCts = currentCts;
        }

        CancelAndDisposeCts(previousCts);

        if (!_canSearchCatalog)
        {
            try
            {
                PublishCatalogResultIfLatest(requestSequence, state => state with
                {
                    CatalogQuery = displayQuery,
                    IsSearchingCatalog = false,
                    CatalogEntries = [],
                    CatalogTotalCount = 0,
                    CatalogFailureCode = DshFailureCode.Unsupported,
                    CatalogDiagnosticSummary = "plugins.catalog_search_unavailable",
                });
            }
            finally
            {
                ReleaseCatalogSearchCts(currentCts);
            }
            return;
        }

        PublishCatalogResultIfLatest(requestSequence, state => state with
        {
            CatalogQuery = displayQuery,
            IsSearchingCatalog = true,
            CatalogFailureCode = null,
            CatalogDiagnosticSummary = null,
        });

        try
        {
            var outcome = await _catalogSearch
                .SearchAsync(effectiveQuery, DefaultCatalogSearchLimit, currentCts.Token)
                .ConfigureAwait(false);

            if (!outcome.IsOk)
            {
                var failure = outcome.Error!;
                var redactedDiagnostic = string.IsNullOrWhiteSpace(failure.DiagnosticKey)
                    ? null
                    : SecretRedactor.RedactAndTruncate(failure.DiagnosticKey!, 240);

                PublishCatalogResultIfLatest(requestSequence, state => state with
                {
                    IsSearchingCatalog = false,
                    CatalogFailureCode = failure.Code,
                    CatalogDiagnosticSummary = redactedDiagnostic,
                });
                return;
            }

            var result = outcome.Value!;
            var warning = string.IsNullOrWhiteSpace(result.DiagnosticWarning)
                ? null
                : SecretRedactor.RedactAndTruncate(result.DiagnosticWarning!, 240);

            PluginsUiState? updatedSnapshot = null;
            lock (_stateLock)
            {
                if (requestSequence != _catalogSearchSequence)
                {
                    return;
                }
                _rawCatalogEntries = result.Items;
                var alignedEntries = AlignCatalogEntriesLocked(result.Items);
                updatedSnapshot = _state with
                {
                    IsSearchingCatalog = false,
                    CatalogEntries = alignedEntries,
                    CatalogTotalCount = result.TotalCount,
                    CatalogFailureCode = null,
                    CatalogDiagnosticSummary = warning,
                };
                _state = updatedSnapshot;
            }
            StateChanged?.Invoke(updatedSnapshot);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && currentCts.IsCancellationRequested)
        {
            // 被更新一轮远端搜索主动取代，保留最新请求的状态与结果。
        }
        catch (OperationCanceledException)
        {
            PublishCatalogResultIfLatest(requestSequence, state => state.IsSearchingCatalog
                ? state with { IsSearchingCatalog = false }
                : state);
            throw;
        }
        finally
        {
            ReleaseCatalogSearchCts(currentCts);
        }
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

    /// <summary>
    /// 检查用户第三方插件是否有新版本：并发防重入、取消上一轮检查，成功后更新最新版本缓存并重建列表；
    /// 失败时保留既有列表，只记录经脱敏的稳定失败码与诊断；调用方取消必须原样抛出。
    /// </summary>
    public async Task CheckUpdatesAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        if (!_canCheckUpdates)
        {
            PublishState(s => s with
            {
                IsCheckingUpdates = false,
                UpdatableCount = 0,
                UpdateCheckStatusKey = null,
                UpdateCheckFailureCode = DshFailureCode.Unsupported.ToString(),
                UpdateCheckDiagnostic = "plugins.update_check_unavailable",
            });
            return;
        }

        CancellationTokenSource currentCts;
        CancellationTokenSource? previousCts;
        long requestSequence;
        lock (_stateLock)
        {
            requestSequence = ++_updateSequence;
            previousCts = _updateCts;
            currentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _updateCts = currentCts;
        }
        CancelAndDisposeCts(previousCts);

        try
        {
            await _updateGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 等待门锁期间被调用方取消：没有取得门锁，但必须释放本轮创建的 CTS 后原样抛出。
            ReleaseUpdateCts(currentCts);
            throw;
        }

        try
        {
            if (requestSequence != Volatile.Read(ref _updateSequence))
            {
                // 已有更新的一轮检查接管，本轮不再发布任何结果。
                return;
            }

            PublishState(s => s with
            {
                IsCheckingUpdates = true,
                UpdateCheckStatusKey = "plugins.status.checking_updates",
                UpdateCheckFailureCode = null,
                UpdateCheckDiagnostic = null,
            });

            PluginEntryView[] sourceEntries;
            lock (_stateLock)
            {
                sourceEntries = _allEntries as PluginEntryView[] ?? [.. _allEntries];
            }

            var outcome = await _service.CheckForUpdatesAsync(sourceEntries, currentCts.Token).ConfigureAwait(false);
            if (requestSequence != Volatile.Read(ref _updateSequence))
            {
                return;
            }
            if (!outcome.IsOk)
            {
                var failure = outcome.Error!;
                PublishState(s => s with
                {
                    IsCheckingUpdates = false,
                    UpdateCheckStatusKey = null,
                    UpdateCheckFailureCode = failure.Code.ToString(),
                    UpdateCheckDiagnostic = string.IsNullOrWhiteSpace(failure.DiagnosticKey)
                        ? null
                        : SecretRedactor.RedactAndTruncate(failure.DiagnosticKey!, 240),
                });
                return;
            }

            var result = outcome.Value!;
            lock (_stateLock)
            {
                _allEntries = result.Entries;
                // 每轮检查都以本轮结果为准重建缓存，避免上一轮的最新版在查询失败后残留为“可更新”。
                _latestVersionsByPackage.Clear();
                foreach (var entry in result.Entries)
                {
                    if (!string.IsNullOrWhiteSpace(entry.LatestVersion))
                    {
                        _latestVersionsByPackage[entry.PackageName] = entry.LatestVersion;
                    }
                }
            }
            PublishState(s => s with
            {
                IsCheckingUpdates = false,
                LastUpdateCheckAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                UpdateCheckStatusKey = result.FailedCount > 0
                    ? "plugins.status.updates_partial"
                    : "plugins.status.updates_checked",
                UpdateCheckFailureCode = null,
                UpdateCheckDiagnostic = result.DiagnosticWarning,
            });
            RebuildFilteredState(
                isLoading: State.IsLoading,
                isMutating: State.IsMutating,
                isRestarting: State.IsRestarting,
                isHostReady: State.IsHostReady,
                canList: State.CanListPlugins,
                failureCode: State.FailureCode,
                diagnosticSummary: State.DiagnosticSummary,
                statusKey: State.UpdateCheckStatusKey);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && currentCts.IsCancellationRequested)
        {
            // 被更新一轮检查主动取消：保留最新一轮的状态，不覆盖结果。
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var diagnostic = SecretRedactor.RedactAndTruncate($"plugins.update_check_failed:{ex.GetType().Name}", 160);
            PublishState(s => s with
            {
                IsCheckingUpdates = false,
                UpdateCheckStatusKey = null,
                UpdateCheckFailureCode = DshFailureCode.TransportLost.ToString(),
                UpdateCheckDiagnostic = diagnostic,
            });
        }
        finally
        {
            _updateGate.Release();
            ReleaseUpdateCts(currentCts);
        }
    }

    /// <summary>
    /// 将指定插件升级到精确版本：前置校验包名、精确 SemVer 与内置/核心保护限制，与安装/卸载共用
    /// <see cref="_opGate"/> 串行执行；成功后刷新列表并保留“需要重启 DSH 生效”提示。
    /// </summary>
    public async Task<Outcome<PluginMutationResult>> UpgradePluginAsync(
        string packageName,
        string targetVersion,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        var trimmedPackageName = packageName?.Trim(' ');
        var trimmedTargetVersion = targetVersion?.Trim(' ');
        if (!PluginPackageValidator.IsValidPackageName(trimmedPackageName) ||
            string.IsNullOrEmpty(trimmedTargetVersion) ||
            !PluginPackageValidator.IsValidInstallSpec($"{trimmedPackageName}@{trimmedTargetVersion}"))
        {
            PublishState(s => s with
            {
                FailureCode = DshFailureCode.InvalidState.ToString(),
                DiagnosticSummary = "plugin.invalid_upgrade_spec",
                StatusKey = null,
            });
            return Outcome<PluginMutationResult>.Fail(DshFailureCode.InvalidState, "plugin.invalid_upgrade_spec");
        }

        PluginEntryView? knownEntry;
        lock (_stateLock)
        {
            knownEntry = _allEntries.FirstOrDefault(entry =>
                string.Equals(entry.PackageName, trimmedPackageName, StringComparison.Ordinal));
        }
        if (knownEntry is not null &&
            (knownEntry.IsProtected ||
             string.Equals(knownEntry.Availability, "installation", StringComparison.OrdinalIgnoreCase)))
        {
            PublishState(s => s with
            {
                FailureCode = DshFailureCode.InvalidState.ToString(),
                DiagnosticSummary = "plugin.upgrade_requires_dsh_layer",
                StatusKey = null,
            });
            return Outcome<PluginMutationResult>.Fail(DshFailureCode.InvalidState, "plugin.upgrade_requires_dsh_layer");
        }

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PublishState(s => s with
            {
                IsMutating = true,
                FailureCode = null,
                DiagnosticSummary = null,
                StatusKey = null,
            });

            var result = await _service
                .UpgradePluginAsync(trimmedPackageName, trimmedTargetVersion, ct)
                .ConfigureAwait(false);
            if (!result.IsOk)
            {
                var failure = result.Error!;
                PublishState(s => s with
                {
                    IsMutating = false,
                    RequiresDshRestart = _service.RequiresDshRestart,
                    FailureCode = failure.Code.ToString(),
                    DiagnosticSummary = string.IsNullOrWhiteSpace(failure.DiagnosticKey)
                        ? null
                        : SecretRedactor.RedactAndTruncate(failure.DiagnosticKey!, 240),
                    StatusKey = null,
                });
                return result;
            }

            await RefreshCoreAsync(
                includeCliPackages: true,
                statusKeyOnSuccess: "plugins.status.upgraded",
                ct).ConfigureAwait(false);
            return result;
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
            // 最新版本缓存统一在 RebuildFilteredState → ApplyLatestVersion 中合并，避免两处口径不一致。
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

    private void RebuildFilteredStateFromCurrentFlags()
    {
        var current = State;
        RebuildFilteredState(
            isLoading: current.IsLoading,
            isMutating: current.IsMutating,
            isRestarting: current.IsRestarting,
            isHostReady: current.IsHostReady,
            canList: current.CanListPlugins,
            failureCode: current.FailureCode,
            diagnosticSummary: current.DiagnosticSummary,
            statusKey: current.StatusKey);
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
            var mergedEntries = _allEntries.Select(ApplyLatestVersion).ToArray();
            var filtered = FilterAndRankEntries(mergedEntries, _state.FilterMode, _state.SearchQuery);
            var alignedCatalog = _rawCatalogEntries.Count == 0
                ? _state.CatalogEntries
                : AlignCatalogEntriesLocked(_rawCatalogEntries);

            snapshot = _state with
            {
                IsLoading = isLoading,
                IsMutating = isMutating,
                IsRestarting = isRestarting,
                IsHostReady = isHostReady,
                CanListPlugins = canList,
                RequiresDshRestart = _service.RequiresDshRestart,
                Entries = filtered,
                TotalInstalledCount = mergedEntries.Length,
                UpdatableCount = mergedEntries.Count(entry => entry.HasUpdate || entry.CanUpgrade),
                CatalogEntries = alignedCatalog,
                FailureCode = failureCode,
                DiagnosticSummary = diagnosticSummary,
                StatusKey = statusKey,
                IsFullWebUiCompatEnabled = _service.IsFullWebUiCompatEnabled,
            };
            _state = snapshot;
        }
        StateChanged?.Invoke(snapshot);
    }

    /// <summary>
    /// 把远端最新版本缓存合并进单条插件视图，并计算 <c>HasUpdate</c>/<c>CanUpgrade</c>：
    /// 仅用户第三方插件（已装于 profile、非核心保护、可用性不是 installation）且规格可精确安装时才允许升级。
    /// </summary>
    private PluginEntryView ApplyLatestVersion(PluginEntryView entry)
    {
        if (!_latestVersionsByPackage.TryGetValue(entry.PackageName, out var latestVersion) ||
            string.IsNullOrWhiteSpace(latestVersion))
        {
            return entry;
        }

        var hasUpdate = PluginVersionComparer.IsUpdateAvailable(entry.Version, latestVersion);
        var canUpgrade = hasUpdate
            && entry.IsInstalledInProfile
            && !entry.IsProtected
            && !string.Equals(entry.Availability, "installation", StringComparison.OrdinalIgnoreCase)
            && PluginPackageValidator.IsValidInstallSpec($"{entry.PackageName}@{latestVersion}");
        return entry with
        {
            LatestVersion = latestVersion,
            HasUpdate = hasUpdate,
            CanUpgrade = canUpgrade,
        };
    }

    private static IReadOnlyList<PluginEntryView> FilterAndRankEntries(
        PluginEntryView[] sourceEntries,
        PluginFilterMode filterMode,
        string rawQuery)
    {
        var queryTokens = string.IsNullOrWhiteSpace(rawQuery)
            ? []
            : rawQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (queryTokens.Length == 0)
        {
            if (filterMode == PluginFilterMode.All)
            {
                return sourceEntries;
            }

            var filtered = new List<PluginEntryView>(sourceEntries.Length);
            foreach (var entry in sourceEntries)
            {
                if (MatchesFilterMode(entry, filterMode))
                {
                    filtered.Add(entry);
                }
            }
            return filtered;
        }

        var scored = new List<(PluginEntryView Entry, int Score, int OriginalIndex)>(sourceEntries.Length);
        for (var index = 0; index < sourceEntries.Length; index++)
        {
            var entry = sourceEntries[index];
            if (!MatchesFilterMode(entry, filterMode))
            {
                continue;
            }

            var score = ComputeMultiTokenScore(entry, queryTokens);
            if (score > 0)
            {
                scored.Add((entry, score, index));
            }
        }

        scored.Sort(static (left, right) =>
        {
            var scoreComparison = right.Score.CompareTo(left.Score);
            return scoreComparison != 0 ? scoreComparison : left.OriginalIndex.CompareTo(right.OriginalIndex);
        });

        var ranked = new PluginEntryView[scored.Count];
        for (var i = 0; i < scored.Count; i++)
        {
            ranked[i] = scored[i].Entry;
        }
        return ranked;
    }

    private static bool MatchesFilterMode(PluginEntryView entry, PluginFilterMode filterMode) =>
        filterMode switch
        {
            PluginFilterMode.Manageable => !entry.IsProtected && (entry.CanToggle || entry.CanUninstall),
            PluginFilterMode.Enabled => entry.Enabled,
            PluginFilterMode.Protected => entry.IsProtected,
            PluginFilterMode.Issues => PluginPackageValidator.HasIssue(entry),
            PluginFilterMode.Updatable => entry.HasUpdate || entry.CanUpgrade,
            _ => true,
        };

    private static int ComputeMultiTokenScore(PluginEntryView entry, string[] tokens)
    {
        var formatted = PluginDisplayFormatter.Format(entry);
        var totalScore = 0;

        foreach (var token in tokens)
        {
            var tokenScore = ComputeSingleTokenScore(entry, formatted.ShortTitle, token);
            if (tokenScore <= 0)
            {
                return 0;
            }
            totalScore += tokenScore;
        }

        return totalScore;
    }

    private static int ComputeSingleTokenScore(PluginEntryView entry, string shortTitle, string token)
    {
        // 相关性权重层级：短标题精确/前缀命中 > 模块/包名/入口 ID 命中 > 展示名/描述命中 > 版本/诊断/运行阶段命中。
        if (string.Equals(shortTitle, token, StringComparison.OrdinalIgnoreCase))
        {
            return 120;
        }
        if (shortTitle.StartsWith(token, StringComparison.OrdinalIgnoreCase))
        {
            return 90;
        }
        if (shortTitle.Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            return 70;
        }

        if (StartsWithOrEqualsIgnoreCase(entry.ModuleName, token) ||
            StartsWithOrEqualsIgnoreCase(entry.PackageName, token) ||
            StartsWithOrEqualsIgnoreCase(entry.EntryId, token))
        {
            return 55;
        }
        if (ContainsIgnoreCase(entry.ModuleName, token) ||
            ContainsIgnoreCase(entry.PackageName, token) ||
            ContainsIgnoreCase(entry.EntryId, token))
        {
            return 40;
        }

        if (ContainsIgnoreCase(entry.DisplayName, token) ||
            ContainsIgnoreCase(entry.Description, token))
        {
            return 25;
        }

        if (ContainsIgnoreCase(entry.Version, token) ||
            ContainsIgnoreCase(entry.DiagnosticSummary, token) ||
            ContainsIgnoreCase(entry.FiberPhase, token))
        {
            return 10;
        }

        return 0;
    }

    private static bool StartsWithOrEqualsIgnoreCase(string? field, string token) =>
        !string.IsNullOrEmpty(field) && field.StartsWith(token, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsIgnoreCase(string? field, string token) =>
        !string.IsNullOrEmpty(field) && field.Contains(token, StringComparison.OrdinalIgnoreCase);

    private PluginCatalogEntryView[] AlignCatalogEntriesLocked(
        IReadOnlyList<PluginCatalogEntryView> rawCatalogEntries)
    {
        if (rawCatalogEntries.Count == 0)
        {
            return [];
        }

        var installedMap = new Dictionary<string, (bool FromProfile, string? Version)>(StringComparer.Ordinal);
        foreach (var localEntry in _allEntries)
        {
            var packageName = localEntry.PackageName;
            if (string.IsNullOrWhiteSpace(packageName))
            {
                continue;
            }

            if (!installedMap.TryGetValue(packageName, out var existing))
            {
                installedMap[packageName] = (localEntry.IsInstalledInProfile, localEntry.Version);
            }
            else if ((!existing.FromProfile && localEntry.IsInstalledInProfile) ||
                     (string.IsNullOrWhiteSpace(existing.Version) && !string.IsNullOrWhiteSpace(localEntry.Version)))
            {
                installedMap[packageName] = (
                    existing.FromProfile || localEntry.IsInstalledInProfile,
                    !string.IsNullOrWhiteSpace(localEntry.Version) ? localEntry.Version : existing.Version);
            }
        }

        var aligned = new PluginCatalogEntryView[rawCatalogEntries.Count];
        for (var i = 0; i < rawCatalogEntries.Count; i++)
        {
            var item = rawCatalogEntries[i];
            if (installedMap.TryGetValue(item.PackageName, out var installedInfo))
            {
                // 目录条目的 Version 是 registry 最新版；若缓存已有更新结果则优先使用缓存，避免两处口径漂移。
                var latestVersion = _latestVersionsByPackage.TryGetValue(item.PackageName, out var cachedLatest) &&
                                    !string.IsNullOrWhiteSpace(cachedLatest)
                    ? cachedLatest
                    : item.Version;
                var hasUpdate = installedInfo.FromProfile &&
                                PluginVersionComparer.IsUpdateAvailable(installedInfo.Version, latestVersion);
                aligned[i] = item with
                {
                    IsInstalled = true,
                    InstalledVersion = installedInfo.Version,
                    HasUpdate = hasUpdate,
                };
            }
            else
            {
                aligned[i] = item with
                {
                    IsInstalled = false,
                    InstalledVersion = null,
                    HasUpdate = false,
                };
            }
        }
        return aligned;
    }

    private void PublishCatalogResultIfLatest(long requestSequence, Func<PluginsUiState, PluginsUiState> transform)
    {
        PluginsUiState? snapshot = null;
        lock (_stateLock)
        {
            if (requestSequence != _catalogSearchSequence)
            {
                return;
            }
            snapshot = transform(_state);
            _state = snapshot;
        }
        StateChanged?.Invoke(snapshot);
    }

    private void ReleaseCatalogSearchCts(CancellationTokenSource target)
    {
        var shouldDispose = false;
        lock (_stateLock)
        {
            if (ReferenceEquals(_catalogSearchCts, target))
            {
                _catalogSearchCts = null;
                shouldDispose = true;
            }
        }
        if (shouldDispose)
        {
            target.Dispose();
        }
    }

    private void ReleaseUpdateCts(CancellationTokenSource target)
    {
        var shouldDispose = false;
        lock (_stateLock)
        {
            if (ReferenceEquals(_updateCts, target))
            {
                _updateCts = null;
                shouldDispose = true;
            }
        }
        if (shouldDispose)
        {
            target.Dispose();
        }
    }

    private static void CancelAndDisposeCts(CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return;
        }
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cts.Dispose();
        }
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

        CancellationTokenSource? pendingSearchCts;
        CancellationTokenSource? pendingUpdateCts;
        lock (_stateLock)
        {
            _catalogSearchSequence++;
            _updateSequence++;
            pendingSearchCts = _catalogSearchCts;
            _catalogSearchCts = null;
            pendingUpdateCts = _updateCts;
            _updateCts = null;
        }
        CancelAndDisposeCts(pendingSearchCts);
        CancelAndDisposeCts(pendingUpdateCts);
        _opGate.Dispose();
        _updateGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
