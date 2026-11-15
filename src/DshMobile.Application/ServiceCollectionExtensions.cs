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
}
