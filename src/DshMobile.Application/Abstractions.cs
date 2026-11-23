using DshMobile.Runtime.Hosting;

namespace DshMobile.Application;

/// <summary>The Android foreground service that keeps PRoot children alive (implemented by DshMobile.Android).</summary>
public interface IForegroundServiceController
{
    void Acquire();

    void Release();
}

/// <summary>Human-readable operation log (the app's diagnostics pane / logcat). Must never receive secrets.</summary>
public interface IDiagnosticsLog
{
    void Note(string line);
}

/// <summary>
/// Runtime options. Since PR-10 (ADR-0018) every slice runs in C# only; the per-slice <c>SliceMode</c> /
/// <c>MigrationOptions</c> of the migration were removed together with the legacy bridge.
/// </summary>
public sealed class RuntimeOptions
{
    /// <summary>Host-path probe for the extractor; null = the real filesystem (the right choice on a device).</summary>
    public Func<string, bool>? HostPathExists { get; set; }

    /// <summary>可选的 C# 宿主启动配置（如协议握手与性能跟踪挂载）。</summary>
    public DshHostOptions? HostOptions { get; set; }
}
