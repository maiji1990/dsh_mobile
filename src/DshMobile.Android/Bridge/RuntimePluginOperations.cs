using DshMobile.Agent.Domain;
using DshMobile.Application.Runtime;
using DshMobile.Runtime;
using DshMobile.Runtime.Plugins;

namespace DshMobile.App.Bridge;

/// <summary>
/// 应用组合根插件操作适配器（PR-07, N-07-01；ADR-0034 增强）：
/// 将 <see cref="DshMobile.Agent"/> 的纯托管 <see cref="IPluginOperations"/> 契约桥接到
/// <see cref="RuntimeCoordinator"/> 的受控 PRoot/CLI 执行、本地包安全只读详情与宿主重启流程。
/// </summary>
internal sealed class RuntimePluginOperations(
    RuntimeCoordinator runtime,
    Func<CancellationToken, Task<RuntimeStartResult>> restartHostAsync) : IPluginOperations
{
    public bool IsFullWebUiCompatEnabled => runtime.IsFullWebUiActive;

    public IReadOnlySet<string> GetNativeTrimmedEntryIds() => WebTrimPatchInventory.TrimmedEntryIds;

    public async Task<Outcome<IReadOnlyList<PluginInstalledPackageView>>> ListInstalledPackagesAsync(CancellationToken ct = default)
    {
        var result = await runtime.ListProfilePluginsAsync(ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return Outcome<IReadOnlyList<PluginInstalledPackageView>>.Fail(
                MapCode(result.Code),
                result.Diagnostic ?? result.Code);
        }

        var mapped = result.Packages
            .Select(p => new PluginInstalledPackageView(
                Name: p.Name,
                Version: p.Version,
                HasCustomWebUi: p.HasCustomWebUi,
                IsProtected: p.IsProtected,
                HasBundleManifest: p.HasBundleManifest,
                Description: p.Description,
                Author: p.Author,
                License: p.License,
                HomepageUrl: p.HomepageUrl,
                RepositoryUrl: p.RepositoryUrl))
            .ToArray();
        return Outcome<IReadOnlyList<PluginInstalledPackageView>>.Ok(mapped);
    }

    public async Task<Outcome<PluginLocalPackageDetailView>> ReadLocalPackageDetailAsync(
        string packageName,
        string language,
        CancellationToken ct = default)
    {
        var detail = await runtime.ReadLocalPluginDetailAsync(packageName, language, ct).ConfigureAwait(false);
        if (!detail.Ok)
        {
            return Outcome<PluginLocalPackageDetailView>.Fail(
                MapCode(detail.Code),
                detail.Diagnostic ?? detail.Code);
        }

        return Outcome<PluginLocalPackageDetailView>.Ok(new PluginLocalPackageDetailView(
            PackageName: detail.PackageName,
            Version: detail.Version,
            Title: null,
            Description: detail.Description,
            Markdown: detail.Markdown,
            DocumentOrigin: detail.DocumentOrigin,
            DocumentPath: detail.DocumentRelativePath,
            Author: detail.Author,
            License: detail.License,
            HomepageUrl: detail.HomepageUrl,
            RepositoryUrl: detail.RepositoryUrl,
            HasBundleManifest: detail.HasBundleManifest,
            HasCustomWebUi: detail.HasCustomWebUi));
    }

    public async Task<Outcome<Unit>> InstallPackageAsync(string packageSpec, CancellationToken ct = default)
    {
        var result = await runtime.InstallProfilePluginAsync(packageSpec, ct).ConfigureAwait(false);
        return result.Ok
            ? Outcome<Unit>.Ok(Unit.Value)
            : Outcome<Unit>.Fail(MapCode(result.Code), result.Diagnostic ?? result.Code);
    }

    public async Task<Outcome<Unit>> UninstallPackageAsync(string packageName, CancellationToken ct = default)
    {
        var result = await runtime.UninstallProfilePluginAsync(packageName, ct).ConfigureAwait(false);
        return result.Ok
            ? Outcome<Unit>.Ok(Unit.Value)
            : Outcome<Unit>.Fail(MapCode(result.Code), result.Diagnostic ?? result.Code);
    }

    public async Task<Outcome<Unit>> SetPluginEnabledAsync(string entryId, string moduleName, bool enabled, CancellationToken ct = default)
    {
        var result = await runtime.SetProfilePluginEnabledAsync(entryId, moduleName, enabled, ct).ConfigureAwait(false);
        return result.Ok
            ? Outcome<Unit>.Ok(Unit.Value)
            : Outcome<Unit>.Fail(MapCode(result.Code), result.Diagnostic ?? result.Code);
    }

    public async Task<Outcome<Unit>> UpgradePackageAsync(string packageName, string targetVersion, CancellationToken ct = default)
    {
        var result = await runtime.UpgradeProfilePluginAsync(packageName, targetVersion, ct).ConfigureAwait(false);
        return result.Ok
            ? Outcome<Unit>.Ok(Unit.Value)
            : Outcome<Unit>.Fail(MapCode(result.Code), result.Diagnostic ?? result.Code);
    }

    public async Task<Outcome<Unit>> RestartDshHostAsync(CancellationToken ct = default)
    {
        var result = await restartHostAsync(ct).ConfigureAwait(false);
        return result.Ok
            ? Outcome<Unit>.Ok(Unit.Value)
            : Outcome<Unit>.Fail(MapCode(result.Code), result.Message ?? result.Code);
    }

    private static DshFailureCode MapCode(string code) => code switch
    {
        RuntimeErrorCodes.BadRequest => DshFailureCode.InvalidState,
        RuntimeErrorCodes.InvalidTransition => DshFailureCode.SessionBusy,
        RuntimeErrorCodes.NotInstalled => DshFailureCode.Unavailable,
        RuntimeErrorCodes.NotImplemented => DshFailureCode.Unsupported,
        GuestPluginService.PackageNotFoundCode => DshFailureCode.Unavailable,
        GuestPluginService.ShippedPackageCode => DshFailureCode.InvalidState,
        _ => DshFailureCode.ProcessExited,
    };
}
