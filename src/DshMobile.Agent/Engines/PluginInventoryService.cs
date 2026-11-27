using DshMobile.Agent.Domain;

namespace DshMobile.Agent.Engines;

/// <summary>
/// 原生插件目录与受控 L0/L1 管理服务（PR-07, N-07-01 / ADR-0033）：
/// <list type="bullet">
/// <item>L0：聚合 <see cref="IPluginGateway.GetPluginInventoryAsync"/>（<c>pluginInventory/list</c>）与可选 <c>dsh plugin list --json</c> 信息，所有诊断文本经 <see cref="SecretRedactor"/> 脱敏。</item>
/// <item>L1：执行严格包名白名单校验（非法输入零进程启动），不重写 npm 语义、不做 L2 动态表单，任意变更成功后置位 <see cref="RequiresDshRestart"/>。</item>
/// </list>
/// </summary>
public sealed class PluginInventoryService
{
    private readonly IPluginGateway _pluginGateway;
    private readonly IPluginOperations _operations;
    private readonly IPluginCatalogSearch _catalogSearch;
    private readonly Lock _restartLock = new();
    private bool _requiresDshRestart;

    public PluginInventoryService(
        IPluginGateway pluginGateway,
        IPluginOperations? operations = null,
        IPluginCatalogSearch? catalogSearch = null)
    {
        _pluginGateway = pluginGateway ?? throw new ArgumentNullException(nameof(pluginGateway));
        _operations = operations ?? NoopPluginOperations.Instance;
        _catalogSearch = catalogSearch ?? NoopPluginCatalogSearch.Instance;
    }

    /// <summary>当前宿主是否已启用「完整 WebUI 与页面插件兼容模式」（ADR-0034）。</summary>
    public bool IsFullWebUiCompatEnabled => _operations.IsFullWebUiCompatEnabled;

    /// <summary>当前是否存在尚未通过重启 DSH 生效的 L1 插件变更。</summary>
    public bool RequiresDshRestart
    {
        get
        {
            lock (_restartLock)
            {
                return _requiresDshRestart;
            }
        }
    }

    /// <summary>宿主完成新一轮启动/握手后清除「需要重启 DSH 生效」标记。</summary>
    public void NotifyHostRestarted()
    {
        lock (_restartLock)
        {
            _requiresDshRestart = false;
        }
    }

    /// <summary>L0：读取插件运行时清单并可选通过受控 CLI 补充包版本与安装态。</summary>
    public async Task<Outcome<PluginInventorySnapshotResult>> LoadInventoryAsync(
        bool includeCliPackages = true,
        CancellationToken ct = default)
    {
        var invOutcome = await _pluginGateway.GetPluginInventoryAsync(ct).ConfigureAwait(false);
        if (!invOutcome.IsOk)
        {
            var err = RedactFailure(invOutcome.Error!);
            return Outcome<PluginInventorySnapshotResult>.Fail(err);
        }

        var inventory = invOutcome.Value!;
        // 非阻塞读取 bundle 元数据：失败或宿主不支持时静默降级，仅用“是否在 profile 中声明”推断可用性。
        var bundleInfoByName = await TryLoadBundleInfoAsync(ct).ConfigureAwait(false);
        IReadOnlyList<PluginInstalledPackageView> installed = [];
        string? warning = null;

        if (includeCliPackages)
        {
            var pkgOutcome = await _operations.ListInstalledPackagesAsync(ct).ConfigureAwait(false);
            if (pkgOutcome.IsOk)
            {
                installed = pkgOutcome.Value!;
            }
            else if (pkgOutcome.Error is { Code: not DshFailureCode.Unsupported } cliErr)
            {
                warning = SecretRedactor.RedactAndTruncate(cliErr.DiagnosticKey ?? cliErr.Code.ToString(), 200);
            }
        }

        var installedByName = new Dictionary<string, PluginInstalledPackageView>(StringComparer.Ordinal);
        foreach (var pkg in installed)
        {
            installedByName[pkg.Name] = pkg;
        }

        // 尝试拉取官方 settings/describe 标注可实时配置的插件命名空间（失败或不支持时静默忽略，不阻断清单加载）。
        var configurableNamespaces = await TryLoadConfigurableNamespacesAsync(ct).ConfigureAwait(false);
        var fullWebUiCompat = _operations.IsFullWebUiCompatEnabled;
        var nativeTrimmedEntryIds = fullWebUiCompat
            ? null
            : _operations.GetNativeTrimmedEntryIds();

        var matchedPackages = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<PluginEntryView>(inventory.Entries.Count + installed.Count);

        foreach (var entry in inventory.Entries)
        {
            var pkgName = PluginPackageValidator.ExtractPackageName(entry.ModuleName);
            installedByName.TryGetValue(pkgName, out var pkgInfo);
            bundleInfoByName.TryGetValue(pkgName, out var bundleInfo);
            if (pkgInfo is not null)
            {
                matchedPackages.Add(pkgInfo.Name);
            }

            var availability = ResolveAvailability(bundleInfo, installedInProfile: pkgInfo is not null);
            var isProtected = PluginPackageValidator.IsProtectedEntry(entry.EntryId, entry.ModuleName)
                || (pkgInfo?.IsProtected ?? false);
            var isTrimmedByNativeMode = !fullWebUiCompat && IsTrimmedEntry(entry.EntryId, nativeTrimmedEntryIds);
            var hasCustomUi = PluginPackageValidator.IsCustomWebUiPlugin(
                entry.ModuleName,
                entry.EntryId,
                pkgInfo?.HasCustomWebUi ?? false);
            var displayName = !string.IsNullOrWhiteSpace(entry.DisplayName)
                ? entry.DisplayName!
                : entry.ModuleName;
            var rawDescription = !string.IsNullOrWhiteSpace(entry.Description)
                ? entry.Description
                : pkgInfo?.Description;
            var redactedDescription = string.IsNullOrWhiteSpace(rawDescription)
                ? null
                : SecretRedactor.RedactAndTruncate(rawDescription.Trim(), 240);
            var redactedDiagnostic = string.IsNullOrWhiteSpace(entry.Diagnostic)
                ? null
                : SecretRedactor.RedactAndTruncate(entry.Diagnostic!, 240);
            var settingsNamespace = ResolveSettingsNamespace(entry.EntryId, configurableNamespaces);

            merged.Add(new PluginEntryView(
                EntryId: entry.EntryId,
                ModuleName: entry.ModuleName,
                PackageName: pkgName,
                DisplayName: displayName,
                Version: pkgInfo?.Version,
                Enabled: entry.Enabled,
                FiberPhase: entry.FiberPhase,
                DiagnosticSummary: redactedDiagnostic,
                HasCustomWebUi: hasCustomUi,
                IsInstalledInProfile: pkgInfo is not null,
                IsProtected: isProtected,
                CanToggle: !isProtected && !isTrimmedByNativeMode && PluginPackageValidator.IsValidEntryId(entry.EntryId),
                CanUninstall: pkgInfo is not null && !isProtected && PluginPackageValidator.IsValidPackageName(pkgName),
                Description: redactedDescription,
                HasBundleManifest: pkgInfo?.HasBundleManifest ?? true,
                IsTrimmedByNativeMode: isTrimmedByNativeMode,
                HasConfigurableSettings: settingsNamespace is not null,
                SettingsNamespace: settingsNamespace,
                Author: pkgInfo?.Author,
                License: pkgInfo?.License,
                HomepageUrl: pkgInfo?.HomepageUrl,
                RepositoryUrl: pkgInfo?.RepositoryUrl,
                Availability: availability));
        }

        // 将已写入 profile 但尚未随当前 DSH 进程加载的新装/未挂载包也合并进清单，便于用户在重启前看到并可卸载。
        foreach (var pkg in installed)
        {
            if (matchedPackages.Contains(pkg.Name))
            {
                continue;
            }
            var isProtected = pkg.IsProtected || PluginPackageValidator.IsProtectedPackage(pkg.Name);
            var hasCustomUi = PluginPackageValidator.IsCustomWebUiPlugin(pkg.Name, pkg.Name, pkg.HasCustomWebUi);
            bundleInfoByName.TryGetValue(pkg.Name, out var bundleInfo);
            var availability = ResolveAvailability(bundleInfo, installedInProfile: true);
            var redactedDescription = string.IsNullOrWhiteSpace(pkg.Description)
                ? null
                : SecretRedactor.RedactAndTruncate(pkg.Description.Trim(), 240);
            var phase = pkg.HasBundleManifest ? "pending-restart" : "not-bundle";
            merged.Add(new PluginEntryView(
                EntryId: pkg.Name,
                ModuleName: pkg.Name,
                PackageName: pkg.Name,
                DisplayName: pkg.Name,
                Version: pkg.Version,
                Enabled: false,
                FiberPhase: phase,
                DiagnosticSummary: null,
                HasCustomWebUi: hasCustomUi,
                IsInstalledInProfile: true,
                IsProtected: isProtected,
                CanToggle: false,
                CanUninstall: !isProtected && PluginPackageValidator.IsValidPackageName(pkg.Name),
                Description: redactedDescription,
                HasBundleManifest: pkg.HasBundleManifest,
                IsTrimmedByNativeMode: false,
                HasConfigurableSettings: false,
                SettingsNamespace: null,
                Author: pkg.Author,
                License: pkg.License,
                HomepageUrl: pkg.HomepageUrl,
                RepositoryUrl: pkg.RepositoryUrl,
                Availability: availability));
        }

        return Outcome<PluginInventorySnapshotResult>.Ok(new PluginInventorySnapshotResult(
            ManagementAvailable: inventory.ManagementAvailable,
            Entries: merged,
            InstalledPackages: installed,
            DiagnosticWarning: warning));
    }

    /// <summary>L1：受控安装 npm 插件包（前置白名单校验，非法输入零进程调用）。</summary>
    public async Task<Outcome<PluginMutationResult>> InstallPluginAsync(string? packageSpec, CancellationToken ct = default)
    {
        var trimmed = packageSpec?.Trim(' ');
        if (!PluginPackageValidator.IsValidInstallSpec(trimmed))
        {
            return Outcome<PluginMutationResult>.Fail(DshFailureCode.InvalidState, "plugin.invalid_package_spec");
        }

        var outcome = await _operations.InstallPackageAsync(trimmed!, ct).ConfigureAwait(false);
        if (!outcome.IsOk)
        {
            return Outcome<PluginMutationResult>.Fail(RedactFailure(outcome.Error!));
        }

        MarkRestartRequired();
        return Outcome<PluginMutationResult>.Ok(new PluginMutationResult(
            Succeeded: true,
            RequiresDshRestart: true,
            Target: trimmed!));
    }

    /// <summary>L1：受控卸载 npm 插件包（前置白名单与核心包保护校验，非法输入零进程调用）。</summary>
    public async Task<Outcome<PluginMutationResult>> UninstallPluginAsync(string? packageName, CancellationToken ct = default)
    {
        var trimmed = packageName?.Trim(' ');
        if (!PluginPackageValidator.IsValidPackageName(trimmed))
        {
            return Outcome<PluginMutationResult>.Fail(DshFailureCode.InvalidState, "plugin.invalid_package_name");
        }
        if (PluginPackageValidator.IsProtectedPackage(trimmed!))
        {
            return Outcome<PluginMutationResult>.Fail(DshFailureCode.InvalidState, "plugin.protected_package");
        }

        var outcome = await _operations.UninstallPackageAsync(trimmed!, ct).ConfigureAwait(false);
        if (!outcome.IsOk)
        {
            return Outcome<PluginMutationResult>.Fail(RedactFailure(outcome.Error!));
        }

        MarkRestartRequired();
        return Outcome<PluginMutationResult>.Ok(new PluginMutationResult(
            Succeeded: true,
            RequiresDshRestart: true,
            Target: trimmed!));
    }

    /// <summary>L1：受控启停插件入口（前置校验 + 核心入口保护，成功后要求重启 DSH 生效）。</summary>
    public async Task<Outcome<PluginMutationResult>> SetPluginEnabledAsync(
        string? entryId,
        string? moduleName,
        bool enabled,
        CancellationToken ct = default)
    {
        var trimmedId = entryId?.Trim();
        var trimmedModule = moduleName?.Trim() ?? string.Empty;
        if (!PluginPackageValidator.IsValidEntryId(trimmedId))
        {
            return Outcome<PluginMutationResult>.Fail(DshFailureCode.InvalidState, "plugin.invalid_entry_id");
        }
        if (PluginPackageValidator.IsProtectedEntry(trimmedId!, trimmedModule))
        {
            return Outcome<PluginMutationResult>.Fail(DshFailureCode.InvalidState, "plugin.protected_entry");
        }

        var outcome = await _operations.SetPluginEnabledAsync(trimmedId!, trimmedModule, enabled, ct).ConfigureAwait(false);
        if (!outcome.IsOk)
        {
            return Outcome<PluginMutationResult>.Fail(RedactFailure(outcome.Error!));
        }

        MarkRestartRequired();
        return Outcome<PluginMutationResult>.Ok(new PluginMutationResult(
            Succeeded: true,
            RequiresDshRestart: true,
            Target: trimmedId!));
    }

    /// <summary>重启 DSH 宿主使已保存的插件变更生效，成功后清除重启横幅状态。</summary>
    public async Task<Outcome<Unit>> RestartDshHostAsync(CancellationToken ct = default)
    {
        var outcome = await _operations.RestartDshHostAsync(ct).ConfigureAwait(false);
        if (!outcome.IsOk)
        {
            return Outcome<Unit>.Fail(RedactFailure(outcome.Error!));
        }

        NotifyHostRestarted();
        return Outcome<Unit>.Ok(Unit.Value);
    }

    /// <summary>
    /// 检查用户第三方插件是否有新版本：筛选“已装于 profile、非核心保护、可用性不是 installation 且版本合法”的条目，
    /// 去重后批量查询 npm 最新版本，并用 <see cref="PluginVersionComparer.IsUpdateAvailable"/> 计算
    /// <c>LatestVersion</c>/<c>HasUpdate</c>/<c>CanUpgrade</c>。内置插件（<c>availability = installation</c>）不参与检查，
    /// 必须随 DSH 层更新。单个包查询失败只累计失败数并给出脱敏摘要，不伪造更新。
    /// </summary>
    public async Task<Outcome<PluginUpdateCheckResult>> CheckForUpdatesAsync(
        IReadOnlyList<PluginEntryView> currentEntries,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentEntries);
        ct.ThrowIfCancellationRequested();

        var candidatePackageNames = new List<string>();
        var seenPackageNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in currentEntries)
        {
            if (!IsUserUpdatableEntry(entry))
            {
                continue;
            }
            if (seenPackageNames.Add(entry.PackageName))
            {
                candidatePackageNames.Add(entry.PackageName);
            }
        }

        if (candidatePackageNames.Count == 0)
        {
            return Outcome<PluginUpdateCheckResult>.Ok(new PluginUpdateCheckResult(currentEntries, 0, 0));
        }

        var latestOutcome = await _catalogSearch.GetLatestVersionsAsync(candidatePackageNames, ct).ConfigureAwait(false);
        if (!latestOutcome.IsOk)
        {
            return Outcome<PluginUpdateCheckResult>.Fail(RedactFailure(latestOutcome.Error!));
        }

        var latestByPackageName = new Dictionary<string, PluginLatestVersionView>(StringComparer.Ordinal);
        foreach (var latest in latestOutcome.Value ?? [])
        {
            if (!string.IsNullOrWhiteSpace(latest.PackageName))
            {
                latestByPackageName[latest.PackageName] = latest;
            }
        }

        var failedCount = candidatePackageNames.Count(packageName =>
            !latestByPackageName.TryGetValue(packageName, out var latest) ||
            string.IsNullOrWhiteSpace(latest.LatestVersion));

        var updatedEntries = new List<PluginEntryView>(currentEntries.Count);
        foreach (var entry in currentEntries)
        {
            if (!IsUserUpdatableEntry(entry) ||
                !latestByPackageName.TryGetValue(entry.PackageName, out var latest) ||
                string.IsNullOrWhiteSpace(latest.LatestVersion))
            {
                // 不参与检查、查询失败或没有 latest 的条目：清空旧的最新版信息，避免显示过期结论。
                updatedEntries.Add(entry with
                {
                    LatestVersion = null,
                    HasUpdate = false,
                    CanUpgrade = false,
                });
                continue;
            }

            var hasUpdate = PluginVersionComparer.IsUpdateAvailable(entry.Version, latest.LatestVersion);
            var targetSpec = $"{entry.PackageName}@{latest.LatestVersion}";
            var canUpgrade = hasUpdate
                && !entry.IsProtected
                && PluginPackageValidator.IsValidInstallSpec(targetSpec);
            updatedEntries.Add(entry with
            {
                LatestVersion = latest.LatestVersion,
                HasUpdate = hasUpdate,
                CanUpgrade = canUpgrade,
            });
        }

        var warning = failedCount > 0
            ? SecretRedactor.RedactAndTruncate(
                $"plugins.update_check_partial:{failedCount}/{candidatePackageNames.Count}", 200)
            : null;
        return Outcome<PluginUpdateCheckResult>.Ok(new PluginUpdateCheckResult(
            Entries: updatedEntries,
            CheckedCount: candidatePackageNames.Count,
            FailedCount: failedCount,
            DiagnosticWarning: warning));
    }

    /// <summary>
    /// 受控升级第三方插件到精确版本：校验包名、非核心保护包与精确 SemVer，调用运行时桥接执行
    /// <c>dsh plugin --profile web add &lt;包名&gt;@&lt;精确版本&gt;</c>；成功后置位“需要重启 DSH 生效”，
    /// 并返回带 <see cref="PluginMutationKind.Upgrade"/> 语义的结果。内置包的拦截在 Runtime 边界按 DSH 层依赖再次校验。
    /// </summary>
    public async Task<Outcome<PluginMutationResult>> UpgradePluginAsync(
        string? packageName,
        string? targetVersion,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var trimmedPackageName = packageName?.Trim(' ');
        var trimmedTargetVersion = targetVersion?.Trim(' ');
        if (!PluginPackageValidator.IsValidPackageName(trimmedPackageName))
        {
            return Outcome<PluginMutationResult>.Fail(DshFailureCode.InvalidState, "plugin.invalid_package_name");
        }
        if (PluginPackageValidator.IsProtectedPackage(trimmedPackageName!))
        {
            return Outcome<PluginMutationResult>.Fail(DshFailureCode.InvalidState, "plugin.protected_package");
        }
        if (string.IsNullOrEmpty(trimmedTargetVersion) ||
            !PluginPackageValidator.IsValidInstallSpec($"{trimmedPackageName}@{trimmedTargetVersion}"))
        {
            return Outcome<PluginMutationResult>.Fail(DshFailureCode.InvalidState, "plugin.invalid_upgrade_version");
        }

        var outcome = await _operations
            .UpgradePackageAsync(trimmedPackageName!, trimmedTargetVersion!, ct)
            .ConfigureAwait(false);
        if (!outcome.IsOk)
        {
            return Outcome<PluginMutationResult>.Fail(RedactFailure(outcome.Error!));
        }

        MarkRestartRequired();
        return Outcome<PluginMutationResult>.Ok(new PluginMutationResult(
            Succeeded: true,
            RequiresDshRestart: true,
            Target: trimmedPackageName!,
            Kind: PluginMutationKind.Upgrade,
            ToVersion: trimmedTargetVersion));
    }

    /// <summary>非阻塞读取 bundle 元数据；失败、不可用或空结果都返回空字典，绝不阻断插件清单加载。</summary>
    private async Task<IReadOnlyDictionary<string, PluginBundleInfoView>> TryLoadBundleInfoAsync(CancellationToken ct)
    {
        var bundleInfoByName = new Dictionary<string, PluginBundleInfoView>(StringComparer.Ordinal);
        Outcome<IReadOnlyList<PluginBundleInfoView>> bundleOutcome;
        try
        {
            bundleOutcome = await _pluginGateway.ListPluginBundlesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // bundle 元数据只是增强信息：读取失败时静默降级，绝不影响插件清单本身。
            return bundleInfoByName;
        }
        if (!bundleOutcome.IsOk || bundleOutcome.Value is null)
        {
            return bundleInfoByName;
        }
        foreach (var bundleInfo in bundleOutcome.Value)
        {
            if (string.IsNullOrWhiteSpace(bundleInfo.Name))
            {
                continue;
            }
            bundleInfoByName[bundleInfo.Name] = bundleInfo;
        }
        return bundleInfoByName;
    }

    /// <summary>优先采用 bundle 元数据给出的可用性；缺失时按“是否在 profile 中声明”回退为 profile/installation。</summary>
    private static string ResolveAvailability(PluginBundleInfoView? bundleInfo, bool installedInProfile)
    {
        if (bundleInfo is not null && !string.IsNullOrWhiteSpace(bundleInfo.Availability))
        {
            return bundleInfo.Availability;
        }
        return installedInProfile ? "profile" : "installation";
    }

    /// <summary>判断条目是否为可独立检查更新的用户第三方插件（内置 installation 插件必须随 DSH 层更新）。</summary>
    private static bool IsUserUpdatableEntry(PluginEntryView entry) =>
        entry.IsInstalledInProfile
        && !entry.IsProtected
        && !string.Equals(entry.Availability, "installation", StringComparison.OrdinalIgnoreCase)
        && PluginPackageValidator.IsValidPackageName(entry.PackageName)
        && !string.IsNullOrWhiteSpace(entry.Version);

    private async Task<HashSet<string>> TryLoadConfigurableNamespacesAsync(CancellationToken ct)
    {
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        var describeOutcome = await _pluginGateway.DescribeSettingsAsync(ct).ConfigureAwait(false);
        if (!describeOutcome.IsOk || describeOutcome.Value is null)
        {
            return namespaces;
        }

        foreach (var nsView in describeOutcome.Value.Namespaces)
        {
            if (string.IsNullOrWhiteSpace(nsView.Namespace))
            {
                continue;
            }
            if (PluginSettingsSchemaParser.HasConfigurableFields(nsView))
            {
                namespaces.Add(nsView.Namespace);
            }
        }
        return namespaces;
    }

    private static bool IsTrimmedEntry(string entryId, IReadOnlySet<string>? trimmedEntryIds)
    {
        if (trimmedEntryIds is null || trimmedEntryIds.Count == 0 || string.IsNullOrEmpty(entryId))
        {
            return false;
        }
        if (trimmedEntryIds.Contains(entryId))
        {
            return true;
        }
        if (entryId.StartsWith("include:", StringComparison.Ordinal))
        {
            return trimmedEntryIds.Contains(entryId["include:".Length..]);
        }
        return false;
    }

    private static string? ResolveSettingsNamespace(string entryId, HashSet<string> configurableNamespaces)
    {
        if (configurableNamespaces.Count == 0 || string.IsNullOrEmpty(entryId))
        {
            return null;
        }
        if (configurableNamespaces.Contains(entryId))
        {
            return entryId;
        }
        if (entryId.StartsWith("include:", StringComparison.Ordinal))
        {
            var stripped = entryId["include:".Length..];
            if (configurableNamespaces.Contains(stripped))
            {
                return stripped;
            }
        }
        var lastColon = entryId.LastIndexOf(':');
        if (lastColon >= 0 && lastColon + 1 < entryId.Length)
        {
            var leafId = entryId[(lastColon + 1)..];
            if (configurableNamespaces.Contains(leafId))
            {
                return leafId;
            }
        }
        return null;
    }

    private void MarkRestartRequired()
    {
        lock (_restartLock)
        {
            _requiresDshRestart = true;
        }
    }

    private static DshFailure RedactFailure(DshFailure failure)
    {
        var redactedKey = string.IsNullOrWhiteSpace(failure.DiagnosticKey)
            ? null
            : SecretRedactor.RedactAndTruncate(failure.DiagnosticKey!, 240);
        return failure with { DiagnosticKey = redactedKey };
    }
}
