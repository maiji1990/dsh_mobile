using System.Text.Json;
using DshMobile.Agent.Domain;
using DshMobile.Agent.Engines;

namespace DshMobile.Agent.Presentation;

/// <summary>原生插件详情与官方 <c>settings/*</c> 实时配置不可变状态快照（ADR-0034）。</summary>
public sealed record PluginDetailUiState(
    bool IsLoading,
    bool IsSavingConfig,
    bool IsMutating,
    bool IsRestarting,
    bool IsInstalled,
    bool IsFullWebUiCompatEnabled,
    bool RequiresDshRestart,
    string Title,
    string PackageName,
    string? EntryId = null,
    string? ModuleName = null,
    string? Version = null,
    string? LatestCatalogVersion = null,
    string? Description = null,
    string? MarkdownDocument = null,
    string? DocumentOrigin = null,
    string? DocumentPath = null,
    bool Enabled = false,
    string? FiberPhase = null,
    string? DiagnosticSummary = null,
    bool HasCustomWebUi = false,
    bool HasBundleManifest = true,
    bool IsTrimmedByNativeMode = false,
    bool IsProtected = false,
    bool CanToggle = false,
    bool CanUninstall = false,
    bool CanInstall = false,
    string? RecommendedInstallSpec = null,
    string? Author = null,
    string? License = null,
    string? HomepageUrl = null,
    string? RepositoryUrl = null,
    string? NpmUrl = null,
    bool HasSettings = false,
    bool SettingsWritable = false,
    string? SettingsNamespace = null,
    long? SettingsRevision = null,
    string? FailureCode = null,
    string? StatusKey = null,
    string? FeedbackDiagnostic = null)
{
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public IReadOnlyList<string> AvailableVersions { get; init; } = [];
    public IReadOnlyList<PluginConfigFieldView> ConfigFields { get; init; } = [];
    public IReadOnlyList<SettingsSecretSlotView> SecretSlots { get; init; } = [];

    public static readonly PluginDetailUiState Empty = new(
        IsLoading: false,
        IsSavingConfig: false,
        IsMutating: false,
        IsRestarting: false,
        IsInstalled: false,
        IsFullWebUiCompatEnabled: false,
        RequiresDshRestart: false,
        Title: string.Empty,
        PackageName: string.Empty);
}

/// <summary>
/// 纯托管插件详情与官方 <c>settings/*</c> 实时配置 ViewModel（ADR-0034，零 Android 依赖）：
/// <list type="bullet">
/// <item>统一驱动「已安装插件详情」与「远端发现插件详情」两种入口；</item>
/// <item>文档回退链：<c>pluginManager/readBundleDetail</c> → 本地安全读取 → 远端 npm README；</item>
/// <item>配置实时写入携带 <c>expectedRevision</c> 乐观锁，冲突自动刷新；全量重置遇密钥槽位绝不误删用户密钥。</item>
/// </list>
/// </summary>
public sealed class PluginDetailViewModel : IAsyncDisposable
{
    private enum DetailSourceMode
    {
        None,
        Installed,
        Catalog,
    }

    private readonly IDshGateway _gateway;
    private readonly PluginInventoryService _inventoryService;
    private readonly IPluginOperations _operations;
    private readonly IPluginCatalogSearch _catalogSearch;
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _opGate = new(1, 1);

    private PluginDetailUiState _state;
    private DetailSourceMode _sourceMode = DetailSourceMode.None;
    private string _loadedTarget = string.Empty;
    private string _loadedLanguage = "zh-CN";
    private int _disposed;

    public PluginDetailViewModel(
        IDshGateway gateway,
        PluginInventoryService inventoryService,
        IPluginOperations? operations = null,
        IPluginCatalogSearch? catalogSearch = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _inventoryService = inventoryService ?? throw new ArgumentNullException(nameof(inventoryService));
        _operations = operations ?? NoopPluginOperations.Instance;
        _catalogSearch = catalogSearch ?? NoopPluginCatalogSearch.Instance;
        _state = PluginDetailUiState.Empty with
        {
            IsFullWebUiCompatEnabled = _inventoryService.IsFullWebUiCompatEnabled,
            RequiresDshRestart = _inventoryService.RequiresDshRestart,
        };
    }

    public PluginDetailUiState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public event Action<PluginDetailUiState>? StateChanged;

    public void ClearFeedback()
    {
        PublishState(s => s with
        {
            FailureCode = null,
            StatusKey = null,
            FeedbackDiagnostic = null,
        });
    }

    /// <summary>加载已安装插件的完整详情文档、运行态与官方实时配置区块。</summary>
    public async Task LoadInstalledAsync(
        string entryIdOrPackageName,
        string language = "zh-CN",
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        var target = entryIdOrPackageName?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(target))
        {
            PublishState(s => s with
            {
                IsLoading = false,
                FailureCode = DshFailureCode.InvalidState.ToString(),
                FeedbackDiagnostic = "plugin_detail.missing_target",
            });
            return;
        }

        var normalizedLanguage = string.IsNullOrWhiteSpace(language) ? "zh-CN" : language.Trim();
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _sourceMode = DetailSourceMode.Installed;
            _loadedTarget = target;
            _loadedLanguage = normalizedLanguage;

            PublishState(s => s with
            {
                IsLoading = true,
                FailureCode = null,
                StatusKey = null,
                FeedbackDiagnostic = null,
            });

            await LoadInstalledCoreAsync(target, normalizedLanguage, statusKeyOnSuccess: null, ct).ConfigureAwait(false);
        }
        finally
        {
            PublishState(s => s.IsLoading ? s with { IsLoading = false } : s);
            _opGate.Release();
        }
    }

    /// <summary>加载远端发现插件详情、README 与版本列表；若本机已装则自动融合启停与实时配置区块。</summary>
    public async Task LoadCatalogAsync(
        string packageName,
        string language = "zh-CN",
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        var trimmedPackage = packageName?.Trim() ?? string.Empty;
        if (!PluginPackageValidator.IsValidPackageName(trimmedPackage))
        {
            PublishState(s => s with
            {
                IsLoading = false,
                FailureCode = DshFailureCode.InvalidState.ToString(),
                FeedbackDiagnostic = "plugins.invalid_package_name",
            });
            return;
        }

        var normalizedLanguage = string.IsNullOrWhiteSpace(language) ? "zh-CN" : language.Trim();
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _sourceMode = DetailSourceMode.Catalog;
            _loadedTarget = trimmedPackage;
            _loadedLanguage = normalizedLanguage;

            PublishState(s => s with
            {
                IsLoading = true,
                FailureCode = null,
                StatusKey = null,
                FeedbackDiagnostic = null,
            });

            await LoadCatalogCoreAsync(trimmedPackage, normalizedLanguage, statusKeyOnSuccess: null, ct).ConfigureAwait(false);
        }
        finally
        {
            PublishState(s => s.IsLoading ? s with { IsLoading = false } : s);
            _opGate.Release();
        }
    }

    /// <summary>按原始表单输入保存单个配置字段（前置强类型校验，实时热生效）。</summary>
    public async Task<Outcome<Unit>> SaveFieldInputAsync(
        PluginConfigFieldView field,
        string? rawInput,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (!PluginSettingsSchemaParser.TryEncodeFieldValue(field, rawInput, out var encoded, out var errorKey))
        {
            PublishState(s => s with
            {
                FailureCode = DshFailureCode.InvalidState.ToString(),
                StatusKey = null,
                FeedbackDiagnostic = errorKey ?? "settings.invalid_value",
            });
            return Outcome<Unit>.Fail(DshFailureCode.InvalidState, errorKey ?? "settings.invalid_value");
        }

        return await SaveFieldAsync(field.Path, encoded, ct).ConfigureAwait(false);
    }

    /// <summary>保存单个配置字段并实时热生效（携带乐观锁 <c>expectedRevision</c>，冲突自动刷新）。</summary>
    public async Task<Outcome<Unit>> SaveFieldAsync(
        IReadOnlyList<string> path,
        JsonElement newValue,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(path);
        ct.ThrowIfCancellationRequested();

        if (path.Count == 0)
        {
            return Outcome<Unit>.Fail(DshFailureCode.InvalidState, "settings.empty_path");
        }

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = State;
            if (!current.HasSettings || !current.SettingsWritable || string.IsNullOrWhiteSpace(current.SettingsNamespace))
            {
                PublishState(s => s with
                {
                    FailureCode = DshFailureCode.Unsupported.ToString(),
                    StatusKey = null,
                    FeedbackDiagnostic = "settings.not_writable",
                });
                return Outcome<Unit>.Fail(DshFailureCode.Unsupported, "settings.not_writable");
            }

            PublishState(s => s with
            {
                IsSavingConfig = true,
                FailureCode = null,
                StatusKey = null,
                FeedbackDiagnostic = null,
            });

            var mutateOutcome = await _gateway.MutateSettingsAsync(
                current.SettingsNamespace!,
                [SettingsPathOp.Set(path, newValue)],
                expectedRevision: current.SettingsRevision,
                ct).ConfigureAwait(false);

            return await HandleSettingsWriteOutcomeAsync(
                mutateOutcome,
                current.SettingsNamespace!,
                statusKeyOnSuccess: "plugin_detail.status.config_saved_live",
                ct).ConfigureAwait(false);
        }
        finally
        {
            PublishState(s => s.IsSavingConfig ? s with { IsSavingConfig = false } : s);
            _opGate.Release();
        }
    }

    /// <summary>重置单个配置字段为默认值（发 <c>settings/mutate</c> <c>unset</c>）。</summary>
    public async Task<Outcome<Unit>> ResetFieldAsync(
        IReadOnlyList<string> path,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(path);
        ct.ThrowIfCancellationRequested();

        if (path.Count == 0)
        {
            return Outcome<Unit>.Fail(DshFailureCode.InvalidState, "settings.empty_path");
        }

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = State;
            if (!current.HasSettings || !current.SettingsWritable || string.IsNullOrWhiteSpace(current.SettingsNamespace))
            {
                PublishState(s => s with
                {
                    FailureCode = DshFailureCode.Unsupported.ToString(),
                    StatusKey = null,
                    FeedbackDiagnostic = "settings.not_writable",
                });
                return Outcome<Unit>.Fail(DshFailureCode.Unsupported, "settings.not_writable");
            }

            PublishState(s => s with
            {
                IsSavingConfig = true,
                FailureCode = null,
                StatusKey = null,
                FeedbackDiagnostic = null,
            });

            var mutateOutcome = await _gateway.MutateSettingsAsync(
                current.SettingsNamespace!,
                [SettingsPathOp.Unset(path)],
                expectedRevision: current.SettingsRevision,
                ct).ConfigureAwait(false);

            return await HandleSettingsWriteOutcomeAsync(
                mutateOutcome,
                current.SettingsNamespace!,
                statusKeyOnSuccess: "plugin_detail.status.config_field_reset",
                ct).ConfigureAwait(false);
        }
        finally
        {
            PublishState(s => s.IsSavingConfig ? s with { IsSavingConfig = false } : s);
            _opGate.Release();
        }
    }

    /// <summary>
    /// 全量重置当前插件配置为默认值：
    /// 无密钥槽位时调用 <c>settings/replace(ns, {})</c>；存在任何密钥槽位时仅对非密钥覆盖项发批量 <c>unset</c>，绝不误删用户密钥。
    /// </summary>
    public async Task<Outcome<Unit>> ResetAllFieldsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = State;
            if (!current.HasSettings || !current.SettingsWritable || string.IsNullOrWhiteSpace(current.SettingsNamespace))
            {
                PublishState(s => s with
                {
                    FailureCode = DshFailureCode.Unsupported.ToString(),
                    StatusKey = null,
                    FeedbackDiagnostic = "settings.not_writable",
                });
                return Outcome<Unit>.Fail(DshFailureCode.Unsupported, "settings.not_writable");
            }

            var hasSecretSlots = current.SecretSlots.Count > 0 ||
                                 current.ConfigFields.Any(static f => f.Kind == PluginConfigFieldKind.Secret);

            if (hasSecretSlots)
            {
                var nonSecretOps = PluginSettingsSchemaParser.BuildResetNonSecretOps(current.ConfigFields);
                if (nonSecretOps.Count == 0)
                {
                    PublishState(s => s with
                    {
                        IsSavingConfig = false,
                        FailureCode = null,
                        StatusKey = "plugin_detail.status.config_no_non_secret_overrides",
                        FeedbackDiagnostic = null,
                    });
                    return Outcome<Unit>.Ok(Unit.Value);
                }

                PublishState(s => s with
                {
                    IsSavingConfig = true,
                    FailureCode = null,
                    StatusKey = null,
                    FeedbackDiagnostic = null,
                });

                var mutateOutcome = await _gateway.MutateSettingsAsync(
                    current.SettingsNamespace!,
                    nonSecretOps,
                    expectedRevision: current.SettingsRevision,
                    ct).ConfigureAwait(false);

                return await HandleSettingsWriteOutcomeAsync(
                    mutateOutcome,
                    current.SettingsNamespace!,
                    statusKeyOnSuccess: "plugin_detail.status.config_reset_preserving_secrets",
                    ct).ConfigureAwait(false);
            }

            PublishState(s => s with
            {
                IsSavingConfig = true,
                FailureCode = null,
                StatusKey = null,
                FeedbackDiagnostic = null,
            });

            var replaceOutcome = await _gateway.ReplaceSettingsAsync(
                current.SettingsNamespace!,
                PluginSettingsSchemaParser.CreateEmptyObjectElement(),
                expectedRevision: current.SettingsRevision,
                ct).ConfigureAwait(false);

            return await HandleSettingsWriteOutcomeAsync(
                replaceOutcome,
                current.SettingsNamespace!,
                statusKeyOnSuccess: "plugin_detail.status.config_reset_all",
                ct).ConfigureAwait(false);
        }
        finally
        {
            PublishState(s => s.IsSavingConfig ? s with { IsSavingConfig = false } : s);
            _opGate.Release();
        }
    }

    /// <summary>启停当前已安装插件并刷新详情状态。</summary>
    public async Task<Outcome<PluginMutationResult>> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = State;
            if (!current.CanToggle || string.IsNullOrWhiteSpace(current.EntryId))
            {
                PublishState(s => s with
                {
                    FailureCode = DshFailureCode.InvalidState.ToString(),
                    FeedbackDiagnostic = current.IsTrimmedByNativeMode
                        ? "plugin.trimmed_by_native_mode"
                        : "plugin.toggle_unavailable",
                });
                return Outcome<PluginMutationResult>.Fail(DshFailureCode.InvalidState, "plugin.toggle_unavailable");
            }

            PublishState(s => s with { IsMutating = true, FailureCode = null, StatusKey = null, FeedbackDiagnostic = null });
            var res = await _inventoryService
                .SetPluginEnabledAsync(current.EntryId, current.ModuleName ?? current.PackageName, enabled, ct)
                .ConfigureAwait(false);

            if (!res.IsOk)
            {
                PublishMutationFailure(res.Error!);
                return res;
            }

            PublishState(s => s with
            {
                IsMutating = false,
                Enabled = enabled,
                RequiresDshRestart = _inventoryService.RequiresDshRestart,
                StatusKey = enabled ? "plugins.status.enabled" : "plugins.status.disabled",
            });
            return res;
        }
        finally
        {
            PublishState(s => s.IsMutating ? s with { IsMutating = false } : s);
            _opGate.Release();
        }
    }

    /// <summary>安装指定规格（缺省采用推荐规格）并刷新详情状态。</summary>
    public async Task<Outcome<PluginMutationResult>> InstallAsync(string? spec = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = State;
            var effectiveSpec = !string.IsNullOrWhiteSpace(spec)
                ? spec.Trim()
                : current.RecommendedInstallSpec ?? current.PackageName;

            PublishState(s => s with { IsMutating = true, FailureCode = null, StatusKey = null, FeedbackDiagnostic = null });
            var res = await _inventoryService.InstallPluginAsync(effectiveSpec, ct).ConfigureAwait(false);
            if (!res.IsOk)
            {
                PublishMutationFailure(res.Error!);
                return res;
            }

            await ReloadCurrentCoreAsync("plugins.status.installed", ct).ConfigureAwait(false);
            return res;
        }
        finally
        {
            PublishState(s => s.IsMutating ? s with { IsMutating = false } : s);
            _opGate.Release();
        }
    }

    /// <summary>卸载当前插件并刷新详情状态。</summary>
    public async Task<Outcome<PluginMutationResult>> UninstallAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = State;
            if (!current.CanUninstall || string.IsNullOrWhiteSpace(current.PackageName))
            {
                PublishState(s => s with
                {
                    FailureCode = DshFailureCode.InvalidState.ToString(),
                    FeedbackDiagnostic = "plugin.uninstall_unavailable",
                });
                return Outcome<PluginMutationResult>.Fail(DshFailureCode.InvalidState, "plugin.uninstall_unavailable");
            }

            PublishState(s => s with { IsMutating = true, FailureCode = null, StatusKey = null, FeedbackDiagnostic = null });
            var res = await _inventoryService.UninstallPluginAsync(current.PackageName, ct).ConfigureAwait(false);
            if (!res.IsOk)
            {
                PublishMutationFailure(res.Error!);
                return res;
            }

            await ReloadCurrentCoreAsync("plugins.status.uninstalled", ct).ConfigureAwait(false);
            return res;
        }
        finally
        {
            PublishState(s => s.IsMutating ? s with { IsMutating = false } : s);
            _opGate.Release();
        }
    }

    /// <summary>重启 DSH 宿主使已保存的装卸/启停变更生效并刷新详情状态。</summary>
    public async Task<Outcome<Unit>> RestartDshAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PublishState(s => s with { IsRestarting = true, FailureCode = null, StatusKey = null, FeedbackDiagnostic = null });
            var res = await _inventoryService.RestartDshHostAsync(ct).ConfigureAwait(false);
            if (!res.IsOk)
            {
                var failure = res.Error!;
                PublishState(s => s with
                {
                    IsRestarting = false,
                    RequiresDshRestart = _inventoryService.RequiresDshRestart,
                    FailureCode = failure.Code.ToString(),
                    FeedbackDiagnostic = failure.DiagnosticKey,
                });
                return res;
            }

            await ReloadCurrentCoreAsync("plugins.status.restarted", ct).ConfigureAwait(false);
            return Outcome<Unit>.Ok(Unit.Value);
        }
        finally
        {
            PublishState(s => s.IsRestarting ? s with { IsRestarting = false } : s);
            _opGate.Release();
        }
    }

    private async Task ReloadCurrentCoreAsync(string? statusKeyOnSuccess, CancellationToken ct)
    {
        if (_sourceMode == DetailSourceMode.Installed && !string.IsNullOrEmpty(_loadedTarget))
        {
            await LoadInstalledCoreAsync(_loadedTarget, _loadedLanguage, statusKeyOnSuccess, ct).ConfigureAwait(false);
        }
        else if (_sourceMode == DetailSourceMode.Catalog && !string.IsNullOrEmpty(_loadedTarget))
        {
            await LoadCatalogCoreAsync(_loadedTarget, _loadedLanguage, statusKeyOnSuccess, ct).ConfigureAwait(false);
        }
        else
        {
            PublishState(s => s with
            {
                IsMutating = false,
                IsRestarting = false,
                RequiresDshRestart = _inventoryService.RequiresDshRestart,
                IsFullWebUiCompatEnabled = _inventoryService.IsFullWebUiCompatEnabled,
                StatusKey = statusKeyOnSuccess,
            });
        }
    }

    private async Task LoadInstalledCoreAsync(
        string target,
        string language,
        string? statusKeyOnSuccess,
        CancellationToken ct)
    {
        PluginEntryView? matchedEntry = null;
        var invOutcome = await _inventoryService.LoadInventoryAsync(includeCliPackages: true, ct).ConfigureAwait(false);
        if (invOutcome.IsOk && invOutcome.Value is not null)
        {
            matchedEntry = FindMatchingEntry(invOutcome.Value.Entries, target);
        }

        if (matchedEntry is null)
        {
            // 若在当前运行清单未找到（例如刚被卸载），尝试按远端包详情降级展示
            if (PluginPackageValidator.IsValidPackageName(target) && _catalogSearch is not NoopPluginCatalogSearch)
            {
                await LoadCatalogCoreAsync(target, language, statusKeyOnSuccess, ct).ConfigureAwait(false);
                return;
            }

            PublishState(s => s with
            {
                IsLoading = false,
                IsMutating = false,
                IsRestarting = false,
                IsInstalled = false,
                RequiresDshRestart = _inventoryService.RequiresDshRestart,
                IsFullWebUiCompatEnabled = _inventoryService.IsFullWebUiCompatEnabled,
                FailureCode = invOutcome.IsOk
                    ? DshFailureCode.InvalidState.ToString()
                    : invOutcome.Error!.Code.ToString(),
                FeedbackDiagnostic = invOutcome.IsOk
                    ? "plugin_detail.not_found"
                    : invOutcome.Error!.DiagnosticKey,
                StatusKey = statusKeyOnSuccess,
            });
            return;
        }

        var packageName = !string.IsNullOrWhiteSpace(matchedEntry.PackageName)
            ? matchedEntry.PackageName
            : PluginPackageValidator.ExtractPackageName(matchedEntry.ModuleName);
        var formatted = PluginDisplayFormatter.Format(matchedEntry);
        var title = !string.IsNullOrWhiteSpace(matchedEntry.DisplayName) &&
                    !string.Equals(matchedEntry.DisplayName, matchedEntry.ModuleName, StringComparison.Ordinal)
            ? matchedEntry.DisplayName
            : formatted.ShortTitle;
        var description = matchedEntry.Description ?? formatted.Description;
        var author = matchedEntry.Author;
        var license = matchedEntry.License;
        var homepageUrl = matchedEntry.HomepageUrl;
        var repositoryUrl = matchedEntry.RepositoryUrl;
        var hasBundleManifest = matchedEntry.HasBundleManifest;
        var hasCustomWebUi = matchedEntry.HasCustomWebUi;
        var version = matchedEntry.Version;
        string? latestCatalogVersion = null;
        string? npmUrl = PluginPackageValidator.IsValidPackageName(packageName)
            ? $"https://www.npmjs.com/package/{packageName}"
            : null;
        IReadOnlyList<string> keywords = [];
        IReadOnlyList<string> availableVersions = [];

        string? markdown = null;
        string? docOrigin = null;
        string? docPath = null;

        // 1. 优先从宿主 pluginManager/readBundleDetail 读取 Bundle 文档
        var bundleName = await ResolveMatchingBundleNameAsync(matchedEntry, packageName, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(bundleName))
        {
            var detailOutcome = await _gateway.ReadBundleDetailAsync(bundleName!, language, ct).ConfigureAwait(false);
            if (detailOutcome.IsOk && detailOutcome.Value is { Found: true } bundleDetail &&
                !string.IsNullOrWhiteSpace(bundleDetail.Markdown))
            {
                markdown = bundleDetail.Markdown;
                docOrigin = string.Equals(bundleDetail.Origin, "locale", StringComparison.OrdinalIgnoreCase)
                    ? "bundle-locale"
                    : "bundle-readme";
                docPath = bundleDetail.Path;
            }
        }

        // 2. 回退：通过受控 IPluginOperations 只读本地包详情（package.json / locale / README）
        if (string.IsNullOrWhiteSpace(markdown) && PluginPackageValidator.IsValidPackageName(packageName))
        {
            var localOutcome = await _operations.ReadLocalPackageDetailAsync(packageName, language, ct).ConfigureAwait(false);
            if (localOutcome.IsOk && localOutcome.Value is { } localDetail)
            {
                if (!string.IsNullOrWhiteSpace(localDetail.Markdown))
                {
                    markdown = localDetail.Markdown;
                    docOrigin = string.IsNullOrWhiteSpace(localDetail.DocumentOrigin) ? "local-readme" : $"local-{localDetail.DocumentOrigin}";
                    docPath = localDetail.DocumentPath;
                }
                if (string.IsNullOrWhiteSpace(description) && !string.IsNullOrWhiteSpace(localDetail.Description))
                {
                    description = SecretRedactor.RedactAndTruncate(localDetail.Description!, 240);
                }
                version ??= localDetail.Version;
                author ??= localDetail.Author;
                license ??= localDetail.License;
                homepageUrl ??= localDetail.HomepageUrl;
                repositoryUrl ??= localDetail.RepositoryUrl;
                hasBundleManifest = hasBundleManifest || localDetail.HasBundleManifest;
                hasCustomWebUi = hasCustomWebUi || localDetail.HasCustomWebUi;
            }
        }

        // 3. 回退：若仍无文档且可查远端，查询 npm Registry 补充 README 与版本元数据
        if (string.IsNullOrWhiteSpace(markdown) &&
            _catalogSearch is not NoopPluginCatalogSearch &&
            PluginPackageValidator.IsValidPackageName(packageName))
        {
            var catalogOutcome = await _catalogSearch.GetPackageDetailAsync(packageName, ct).ConfigureAwait(false);
            if (catalogOutcome.IsOk && catalogOutcome.Value is { } catalogDetail)
            {
                if (!string.IsNullOrWhiteSpace(catalogDetail.ReadmeMarkdown))
                {
                    markdown = catalogDetail.ReadmeMarkdown;
                    docOrigin = "npm-readme";
                    docPath = "README.md";
                }
                if (string.IsNullOrWhiteSpace(description) && !string.IsNullOrWhiteSpace(catalogDetail.Description))
                {
                    description = catalogDetail.Description;
                }
                latestCatalogVersion = catalogDetail.LatestVersion;
                author ??= catalogDetail.Author;
                license ??= catalogDetail.License;
                homepageUrl ??= catalogDetail.HomepageUrl;
                repositoryUrl ??= catalogDetail.RepositoryUrl;
                npmUrl ??= catalogDetail.NpmUrl;
                keywords = catalogDetail.Keywords;
                availableVersions = catalogDetail.AvailableVersions;
            }
        }

        // 4. 加载官方 settings/describe 实时配置视图
        var settingsInfo = await TryLoadSettingsForEntryAsync(matchedEntry, language, ct).ConfigureAwait(false);

        PublishState(_ => new PluginDetailUiState(
            IsLoading: false,
            IsSavingConfig: false,
            IsMutating: false,
            IsRestarting: false,
            IsInstalled: true,
            IsFullWebUiCompatEnabled: _inventoryService.IsFullWebUiCompatEnabled,
            RequiresDshRestart: _inventoryService.RequiresDshRestart,
            Title: title,
            PackageName: packageName,
            EntryId: matchedEntry.EntryId,
            ModuleName: matchedEntry.ModuleName,
            Version: version,
            LatestCatalogVersion: latestCatalogVersion,
            Description: description,
            MarkdownDocument: markdown,
            DocumentOrigin: docOrigin,
            DocumentPath: docPath,
            Enabled: matchedEntry.Enabled,
            FiberPhase: matchedEntry.FiberPhase,
            DiagnosticSummary: matchedEntry.DiagnosticSummary,
            HasCustomWebUi: hasCustomWebUi,
            HasBundleManifest: hasBundleManifest,
            IsTrimmedByNativeMode: matchedEntry.IsTrimmedByNativeMode,
            IsProtected: matchedEntry.IsProtected,
            CanToggle: matchedEntry.CanToggle,
            CanUninstall: matchedEntry.CanUninstall,
            CanInstall: false,
            RecommendedInstallSpec: PluginPackageValidator.BuildRecommendedInstallSpec(packageName, latestCatalogVersion ?? version),
            Author: author,
            License: license,
            HomepageUrl: homepageUrl,
            RepositoryUrl: repositoryUrl,
            NpmUrl: npmUrl,
            HasSettings: settingsInfo.HasSettings,
            SettingsWritable: settingsInfo.Writable,
            SettingsNamespace: settingsInfo.Namespace,
            SettingsRevision: settingsInfo.Revision,
            FailureCode: null,
            StatusKey: statusKeyOnSuccess,
            FeedbackDiagnostic: null)
        {
            Keywords = keywords,
            AvailableVersions = availableVersions,
            ConfigFields = settingsInfo.Fields,
            SecretSlots = settingsInfo.Secrets,
        });
    }

    private async Task LoadCatalogCoreAsync(
        string packageName,
        string language,
        string? statusKeyOnSuccess,
        CancellationToken ct)
    {
        var detailOutcome = await _catalogSearch.GetPackageDetailAsync(packageName, ct).ConfigureAwait(false);
        if (!detailOutcome.IsOk)
        {
            var failure = detailOutcome.Error!;
            var redactedDiag = string.IsNullOrWhiteSpace(failure.DiagnosticKey)
                ? null
                : SecretRedactor.RedactAndTruncate(failure.DiagnosticKey!, 240);
            PublishState(s => s with
            {
                IsLoading = false,
                IsMutating = false,
                IsRestarting = false,
                RequiresDshRestart = _inventoryService.RequiresDshRestart,
                IsFullWebUiCompatEnabled = _inventoryService.IsFullWebUiCompatEnabled,
                FailureCode = failure.Code.ToString(),
                FeedbackDiagnostic = redactedDiag,
                StatusKey = null,
            });
            return;
        }

        var detail = detailOutcome.Value!;

        // 融合本机已安装状态与官方配置区块（清单不可用时安静跳过，不阻断远端详情展示）
        PluginEntryView? localEntry = null;
        var invOutcome = await _inventoryService.LoadInventoryAsync(includeCliPackages: true, ct).ConfigureAwait(false);
        if (invOutcome.IsOk && invOutcome.Value is not null)
        {
            localEntry = FindMatchingEntry(invOutcome.Value.Entries, detail.PackageName);
        }

        var settingsInfo = localEntry is not null
            ? await TryLoadSettingsForEntryAsync(localEntry, language, ct).ConfigureAwait(false)
            : LoadedSettingsInfo.Empty;

        var shortTitle = PluginDisplayFormatter.FormatCatalog(new PluginCatalogEntryView(
            PackageName: detail.PackageName,
            Version: detail.LatestVersion,
            Description: detail.Description,
            Keywords: detail.Keywords,
            Publisher: detail.Author,
            PublishedAtUnixMs: null,
            NpmUrl: detail.NpmUrl,
            IsOfficialScope: detail.IsOfficialScope,
            IsInstalled: localEntry is not null,
            InstalledVersion: localEntry?.Version,
            RecommendedInstallSpec: detail.RecommendedInstallSpec)).ShortTitle;

        var isInstalled = localEntry is not null;
        var canInstall = !isInstalled && PluginPackageValidator.IsValidInstallSpec(detail.RecommendedInstallSpec);

        PublishState(_ => new PluginDetailUiState(
            IsLoading: false,
            IsSavingConfig: false,
            IsMutating: false,
            IsRestarting: false,
            IsInstalled: isInstalled,
            IsFullWebUiCompatEnabled: _inventoryService.IsFullWebUiCompatEnabled,
            RequiresDshRestart: _inventoryService.RequiresDshRestart,
            Title: shortTitle,
            PackageName: detail.PackageName,
            EntryId: localEntry?.EntryId,
            ModuleName: localEntry?.ModuleName ?? detail.PackageName,
            Version: localEntry?.Version ?? detail.LatestVersion,
            LatestCatalogVersion: detail.LatestVersion,
            Description: !string.IsNullOrWhiteSpace(detail.Description) ? detail.Description : localEntry?.Description,
            MarkdownDocument: detail.ReadmeMarkdown,
            DocumentOrigin: string.IsNullOrWhiteSpace(detail.ReadmeMarkdown) ? null : "npm-readme",
            DocumentPath: string.IsNullOrWhiteSpace(detail.ReadmeMarkdown) ? null : "README.md",
            Enabled: localEntry?.Enabled ?? false,
            FiberPhase: localEntry?.FiberPhase,
            DiagnosticSummary: localEntry?.DiagnosticSummary,
            HasCustomWebUi: detail.HasCustomWebUi || (localEntry?.HasCustomWebUi ?? false),
            HasBundleManifest: detail.HasBundleManifest || (localEntry?.HasBundleManifest ?? false),
            IsTrimmedByNativeMode: localEntry?.IsTrimmedByNativeMode ?? false,
            IsProtected: localEntry?.IsProtected ?? PluginPackageValidator.IsProtectedPackage(detail.PackageName),
            CanToggle: localEntry?.CanToggle ?? false,
            CanUninstall: localEntry?.CanUninstall ?? false,
            CanInstall: canInstall,
            RecommendedInstallSpec: detail.RecommendedInstallSpec,
            Author: detail.Author ?? localEntry?.Author,
            License: detail.License ?? localEntry?.License,
            HomepageUrl: detail.HomepageUrl ?? localEntry?.HomepageUrl,
            RepositoryUrl: detail.RepositoryUrl ?? localEntry?.RepositoryUrl,
            NpmUrl: detail.NpmUrl,
            HasSettings: settingsInfo.HasSettings,
            SettingsWritable: settingsInfo.Writable,
            SettingsNamespace: settingsInfo.Namespace,
            SettingsRevision: settingsInfo.Revision,
            FailureCode: null,
            StatusKey: statusKeyOnSuccess,
            FeedbackDiagnostic: null)
        {
            Keywords = detail.Keywords,
            AvailableVersions = detail.AvailableVersions,
            ConfigFields = settingsInfo.Fields,
            SecretSlots = settingsInfo.Secrets,
        });
    }

    private async Task<Outcome<Unit>> HandleSettingsWriteOutcomeAsync(
        Outcome<SettingsNamespaceView> writeOutcome,
        string ns,
        string statusKeyOnSuccess,
        CancellationToken ct)
    {
        if (writeOutcome.IsOk && writeOutcome.Value is { } updatedView)
        {
            var currentWritable = State.SettingsWritable;
            var locale = ResolveLocalePrefix(_loadedLanguage);
            var parsedFields = PluginSettingsSchemaParser.ParseFields(updatedView, currentWritable, locale);
            PublishState(s => s with
            {
                IsSavingConfig = false,
                HasSettings = parsedFields.Count > 0 || updatedView.Secrets.Count > 0,
                SettingsNamespace = updatedView.Namespace,
                SettingsRevision = updatedView.Revision,
                ConfigFields = parsedFields,
                SecretSlots = updatedView.Secrets,
                FailureCode = null,
                StatusKey = statusKeyOnSuccess,
                FeedbackDiagnostic = null,
            });
            return Outcome<Unit>.Ok(Unit.Value);
        }

        var failure = writeOutcome.Error!;
        if (failure.Code == DshFailureCode.SettingsConflict)
        {
            // 乐观锁冲突：自动重新拉取最新 describe，把最新 revision 与字段视图同步给用户后提示重试。
            var refreshed = await TryLoadSettingsByNamespaceAsync(ns, _loadedLanguage, ct).ConfigureAwait(false);
            PublishState(s => s with
            {
                IsSavingConfig = false,
                HasSettings = refreshed.HasSettings || s.HasSettings,
                SettingsWritable = refreshed.HasSettings ? refreshed.Writable : s.SettingsWritable,
                SettingsNamespace = refreshed.Namespace ?? s.SettingsNamespace,
                SettingsRevision = refreshed.Revision ?? s.SettingsRevision,
                ConfigFields = refreshed.HasSettings ? refreshed.Fields : s.ConfigFields,
                SecretSlots = refreshed.HasSettings ? refreshed.Secrets : s.SecretSlots,
                FailureCode = DshFailureCode.SettingsConflict.ToString(),
                StatusKey = "plugin_detail.status.config_conflict_refreshed",
                FeedbackDiagnostic = failure.DiagnosticKey,
            });
            return Outcome<Unit>.Fail(failure);
        }

        var redacted = string.IsNullOrWhiteSpace(failure.DiagnosticKey)
            ? null
            : SecretRedactor.RedactAndTruncate(failure.DiagnosticKey!, 240);
        PublishState(s => s with
        {
            IsSavingConfig = false,
            FailureCode = failure.Code.ToString(),
            StatusKey = null,
            FeedbackDiagnostic = redacted,
        });
        return Outcome<Unit>.Fail(failure);
    }

    private async Task<string?> ResolveMatchingBundleNameAsync(
        PluginEntryView entry,
        string packageName,
        CancellationToken ct)
    {
        var bundlesOutcome = await _gateway.ListPluginBundlesAsync(ct).ConfigureAwait(false);
        if (!bundlesOutcome.IsOk || bundlesOutcome.Value is null)
        {
            return null;
        }

        foreach (var bundle in bundlesOutcome.Value)
        {
            if (string.Equals(bundle.Name, packageName, StringComparison.Ordinal))
            {
                return bundle.Name;
            }
            foreach (var row in bundle.Rows)
            {
                if (!string.IsNullOrWhiteSpace(row.EntryId) &&
                    string.Equals(row.EntryId, entry.EntryId, StringComparison.Ordinal))
                {
                    return bundle.Name;
                }
                if (string.Equals(row.ModuleName, entry.ModuleName, StringComparison.Ordinal))
                {
                    return bundle.Name;
                }
            }
        }
        return null;
    }

    private async Task<LoadedSettingsInfo> TryLoadSettingsForEntryAsync(
        PluginEntryView entry,
        string language,
        CancellationToken ct)
    {
        var describeOutcome = await _gateway.DescribeSettingsAsync(ct).ConfigureAwait(false);
        if (!describeOutcome.IsOk || describeOutcome.Value is null)
        {
            return LoadedSettingsInfo.Empty;
        }

        var snapshot = describeOutcome.Value;
        var candidateIds = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(entry.SettingsNamespace))
        {
            candidateIds.Add(entry.SettingsNamespace!);
        }
        if (!string.IsNullOrWhiteSpace(entry.EntryId))
        {
            candidateIds.Add(entry.EntryId);
            if (entry.EntryId.StartsWith("include:", StringComparison.Ordinal))
            {
                candidateIds.Add(entry.EntryId["include:".Length..]);
            }
            var lastColon = entry.EntryId.LastIndexOf(':');
            if (lastColon >= 0 && lastColon + 1 < entry.EntryId.Length)
            {
                candidateIds.Add(entry.EntryId[(lastColon + 1)..]);
            }
        }

        SettingsNamespaceView? matched = null;
        foreach (var candidate in candidateIds)
        {
            matched = snapshot.Namespaces.FirstOrDefault(n =>
                string.Equals(n.Namespace, candidate, StringComparison.Ordinal));
            if (matched is not null)
            {
                break;
            }
        }

        if (matched is null)
        {
            return LoadedSettingsInfo.Empty;
        }

        var locale = ResolveLocalePrefix(language);
        var fields = PluginSettingsSchemaParser.ParseFields(matched, snapshot.Writable, locale);
        if (fields.Count == 0 && matched.Secrets.Count == 0)
        {
            return LoadedSettingsInfo.Empty;
        }

        return new LoadedSettingsInfo(
            HasSettings: true,
            Writable: snapshot.Writable,
            Namespace: matched.Namespace,
            Revision: matched.Revision,
            Fields: fields,
            Secrets: matched.Secrets);
    }

    private async Task<LoadedSettingsInfo> TryLoadSettingsByNamespaceAsync(
        string ns,
        string language,
        CancellationToken ct)
    {
        var describeOutcome = await _gateway.DescribeSettingsAsync(ct).ConfigureAwait(false);
        if (!describeOutcome.IsOk || describeOutcome.Value is null)
        {
            return LoadedSettingsInfo.Empty;
        }

        var snapshot = describeOutcome.Value;
        var matched = snapshot.Namespaces.FirstOrDefault(n =>
            string.Equals(n.Namespace, ns, StringComparison.Ordinal));
        if (matched is null)
        {
            return LoadedSettingsInfo.Empty;
        }

        var locale = ResolveLocalePrefix(language);
        var fields = PluginSettingsSchemaParser.ParseFields(matched, snapshot.Writable, locale);
        return new LoadedSettingsInfo(
            HasSettings: fields.Count > 0 || matched.Secrets.Count > 0,
            Writable: snapshot.Writable,
            Namespace: matched.Namespace,
            Revision: matched.Revision,
            Fields: fields,
            Secrets: matched.Secrets);
    }

    private static PluginEntryView? FindMatchingEntry(IReadOnlyList<PluginEntryView> entries, string target)
    {
        foreach (var entry in entries)
        {
            if (string.Equals(entry.EntryId, target, StringComparison.Ordinal))
            {
                return entry;
            }
        }
        foreach (var entry in entries)
        {
            if (string.Equals(entry.PackageName, target, StringComparison.Ordinal) ||
                string.Equals(entry.ModuleName, target, StringComparison.Ordinal))
            {
                return entry;
            }
        }
        return null;
    }

    private static string ResolveLocalePrefix(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "zh";
        }
        var dash = language.IndexOf('-');
        return (dash > 0 ? language[..dash] : language).ToLowerInvariant();
    }

    private void PublishMutationFailure(DshFailure failure)
    {
        PublishState(s => s with
        {
            IsMutating = false,
            RequiresDshRestart = _inventoryService.RequiresDshRestart,
            FailureCode = failure.Code.ToString(),
            StatusKey = null,
            FeedbackDiagnostic = failure.DiagnosticKey,
        });
    }

    private void PublishState(Func<PluginDetailUiState, PluginDetailUiState> transform)
    {
        PluginDetailUiState snapshot;
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

    private sealed record LoadedSettingsInfo(
        bool HasSettings,
        bool Writable,
        string? Namespace,
        long? Revision,
        IReadOnlyList<PluginConfigFieldView> Fields,
        IReadOnlyList<SettingsSecretSlotView> Secrets)
    {
        public static readonly LoadedSettingsInfo Empty = new(false, false, null, null, [], []);
    }
}
