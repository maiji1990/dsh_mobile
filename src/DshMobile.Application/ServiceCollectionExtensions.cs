using DshMobile.Application.Runtime;
using DshMobile.Application.Shadow;
using DshMobile.Runtime;
using DshMobile.Runtime.Journal;
using Microsoft.Extensions.DependencyInjection;

namespace DshMobile.Application;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Composition root for the C# layers (ADR-0007, ADR-0018). The platform supplies
    /// <see cref="IForegroundServiceController"/>, <see cref="IDiagnosticsLog"/> and, optionally, its APK assets
    /// (<see cref="DshMobile.Runtime.Layers.IBundledAssets"/>; without them the bundled DSH layer is refused).
    /// </summary>
    public static IServiceCollection AddDshMobileApplication(this IServiceCollection services, RuntimeLayout layout, RuntimeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(layout);
        services.AddSingleton(layout);
        services.AddSingleton(options ?? new RuntimeOptions());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IRuntimeJournal>(_ => new FileRuntimeJournal(layout.JournalFile));
        services.AddSingleton<IShadowReportSink>(_ => new FileShadowReportSink(Path.Combine(layout.HostStateDir, "shadow-reports.jsonl")));
        services.AddSingleton(sp => new RuntimeStateMachine(
            sp.GetRequiredService<IRuntimeJournal>(), RuntimeCoordinator.Implementation, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new RuntimeCoordinator(
            sp.GetRequiredService<RuntimeLayout>(),
            sp.GetRequiredService<RuntimeStateMachine>(),
            sp.GetRequiredService<IForegroundServiceController>(),
            sp.GetRequiredService<IDiagnosticsLog>(),
            sp.GetRequiredService<IShadowReportSink>(),
            sp.GetRequiredService<RuntimeOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            bundledAssets: sp.GetService<DshMobile.Runtime.Layers.IBundledAssets>()));
        return services;
    }

    /// <summary>
    /// Read-only release discovery (vNext PR-02, ADR-0022): <see cref="Updates.UpdateChecker"/> over the built-in trust
    /// (<see cref="DshMobile.Runtime.Updates.ReleaseTrust.BuiltIn"/>) and the fixed channel URLs of
    /// <see cref="DshMobile.Runtime.Updates.ReleaseEndpoints"/>, fetched anonymously with <paramref name="http"/>.
    /// Call after <see cref="AddDshMobileApplication"/>.
    /// </summary>
    public static IServiceCollection AddDshMobileUpdateCheck(this IServiceCollection services, Updates.UpdateCheckOptions options, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(http);
        services.AddSingleton(sp => new Updates.UpdateChecker(
            sp.GetRequiredService<RuntimeLayout>(),
            DshMobile.Runtime.Updates.ReleaseTrust.BuiltIn,
            channel => new DshMobile.Runtime.Updates.HttpReleaseSource(http, DshMobile.Runtime.Updates.ReleaseEndpoints.ManifestUrl(channel)),
            options,
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>
    /// Online DSH layer update (vNext PR-05): <see cref="DshMobile.Runtime.Updates.ArtifactDownloader"/> into the private
    /// update store and <see cref="Updates.UpdateCoordinator"/>. <paramref name="downloadHttp"/> should have no overall
    /// timeout (large files; every download is cancellable). Call after <see cref="AddDshMobileApplication"/>.
    /// </summary>
    public static IServiceCollection AddDshMobileUpdateInstall(this IServiceCollection services, HttpClient downloadHttp)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(downloadHttp);
        services.AddSingleton(sp => new DshMobile.Runtime.Updates.ArtifactDownloader(downloadHttp,
            new DshMobile.Runtime.Updates.ArtifactStore(sp.GetRequiredService<RuntimeLayout>())));
        services.AddSingleton(sp => new Updates.UpdateCoordinator(
            sp.GetRequiredService<RuntimeCoordinator>(),
            sp.GetRequiredService<DshMobile.Runtime.Updates.ArtifactDownloader>(),
            new DshMobile.Runtime.Updates.UpdateStateStore(sp.GetRequiredService<RuntimeLayout>()),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
