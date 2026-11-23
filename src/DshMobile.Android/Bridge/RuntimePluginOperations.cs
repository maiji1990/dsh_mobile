using DshMobile.Agent.Domain;
using DshMobile.Application.Runtime;
using DshMobile.Runtime;

namespace DshMobile.App.Bridge;

/// <summary>
/// 应用组合根插件操作适配器（PR-07, N-07-01）：
/// 将 <see cref="DshMobile.Agent"/> 的纯托管 <see cref="IPluginOperations"/> 契约桥接到
/// <see cref="RuntimeCoordinator"/> 的受控 PRoot/CLI 执行与宿主重启流程。
/// </summary>
internal sealed class RuntimePluginOperations(
    RuntimeCoordinator runtime,
    Func<CancellationToken, Task<RuntimeStartResult>> restartHostAsync) : IPluginOperations
{
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
            .Select(p => new PluginInstalledPackageView(p.Name, p.Version, p.HasCustomWebUi, p.IsProtected))
            .ToArray();
        return Outcome<IReadOnlyList<PluginInstalledPackageView>>.Ok(mapped);
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
        _ => DshFailureCode.ProcessExited,
    };
}
