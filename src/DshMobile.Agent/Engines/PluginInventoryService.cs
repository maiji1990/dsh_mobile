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
    private readonly Lock _restartLock = new();
    private bool _requiresDshRestart;

    public PluginInventoryService(IPluginGateway pluginGateway, IPluginOperations? operations = null)
    {
        _pluginGateway = pluginGateway ?? throw new ArgumentNullException(nameof(pluginGateway));
        _operations = operations ?? NoopPluginOperations.Instance;
    }

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

        var matchedPackages = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<PluginEntryView>(inventory.Entries.Count + installed.Count);

        foreach (var entry in inventory.Entries)
        {
            var pkgName = PluginPackageValidator.ExtractPackageName(entry.ModuleName);
            installedByName.TryGetValue(pkgName, out var pkgInfo);
            if (pkgInfo is not null)
            {
                matchedPackages.Add(pkgInfo.Name);
            }

            var isProtected = PluginPackageValidator.IsProtectedEntry(entry.EntryId, entry.ModuleName)
                || (pkgInfo?.IsProtected ?? false);
            var hasCustomUi = PluginPackageValidator.IsCustomWebUiPlugin(
                entry.ModuleName,
                entry.EntryId,
                pkgInfo?.HasCustomWebUi ?? false);
            var displayName = !string.IsNullOrWhiteSpace(entry.DisplayName)
                ? entry.DisplayName!
                : entry.ModuleName;
            var redactedDiagnostic = string.IsNullOrWhiteSpace(entry.Diagnostic)
                ? null
                : SecretRedactor.RedactAndTruncate(entry.Diagnostic!, 240);

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
                CanToggle: !isProtected && PluginPackageValidator.IsValidEntryId(entry.EntryId),
                CanUninstall: pkgInfo is not null && !isProtected && PluginPackageValidator.IsValidPackageName(pkgName)));
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
            merged.Add(new PluginEntryView(
                EntryId: pkg.Name,
                ModuleName: pkg.Name,
                PackageName: pkg.Name,
                DisplayName: pkg.Name,
                Version: pkg.Version,
                Enabled: false,
                FiberPhase: "pending-restart",
                DiagnosticSummary: null,
                HasCustomWebUi: hasCustomUi,
                IsInstalledInProfile: true,
                IsProtected: isProtected,
                CanToggle: false,
                CanUninstall: !isProtected && PluginPackageValidator.IsValidPackageName(pkg.Name)));
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
