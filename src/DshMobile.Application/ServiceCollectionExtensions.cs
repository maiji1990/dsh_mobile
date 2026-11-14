using DshMobile.Application.Runtime;
using DshMobile.Application.Shadow;
using DshMobile.Core.Bridge;
using DshMobile.Runtime;
using DshMobile.Runtime.Journal;
using DshMobile.Runtime.Legacy;
using Microsoft.Extensions.DependencyInjection;

namespace DshMobile.Application;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Composition root for the C# layers (ADR-0007). The platform supplies <see cref="IForegroundServiceController"/>
    /// and <see cref="IDiagnosticsLog"/>; the runtime is the legacy bridge implementation until a C# slice replaces it.
    /// </summary>
    public static IServiceCollection AddDshMobileApplication(this IServiceCollection services, RuntimeLayout layout, BridgeClient bridge, MigrationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(bridge);
        services.AddSingleton(layout);
        services.AddSingleton(bridge);
        services.AddSingleton(options ?? new MigrationOptions());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IRuntimeJournal>(_ => new FileRuntimeJournal(layout.JournalFile));
        services.AddSingleton<IShadowReportSink>(_ => new FileShadowReportSink(Path.Combine(layout.HostStateDir, "shadow-reports.jsonl")));
        services.AddSingleton<IRuntimeManager>(sp => new LegacyBridgeRuntimeManager(sp.GetRequiredService<BridgeClient>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new RuntimeStateMachine(
            sp.GetRequiredService<IRuntimeJournal>(), sp.GetRequiredService<IRuntimeManager>().Implementation, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new RuntimeCoordinator(
            sp.GetRequiredService<IRuntimeManager>(),
            sp.GetRequiredService<RuntimeStateMachine>(),
            sp.GetRequiredService<IForegroundServiceController>(),
            sp.GetRequiredService<IDiagnosticsLog>(),
            sp.GetRequiredService<IShadowReportSink>(),
            sp.GetRequiredService<MigrationOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<RuntimeLayout>(),
            // Optional: the platform registers its APK assets (bundled DSH layer); without it the C# preparer refuses.
            bundledAssets: sp.GetService<DshMobile.Runtime.Layers.IBundledAssets>()));
        services.AddSingleton(sp => new RemoteHostsShadow(
            sp.GetRequiredService<IShadowReportSink>(),
            sp.GetRequiredService<IDiagnosticsLog>(),
            sp.GetRequiredService<MigrationOptions>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
