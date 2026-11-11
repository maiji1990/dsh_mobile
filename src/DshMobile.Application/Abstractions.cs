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
/// Which implementation runs a migrated slice. Only modes that exist are listed: a C# mode is added together with
/// its implementation, and becomes the default only after real-device acceptance (ADR-0007).
/// </summary>
public enum SliceMode
{
    /// <summary>Legacy DSHBox bridge only.</summary>
    Legacy,
    /// <summary>Legacy decides; the C# port runs on the same input read-only and divergences are reported.</summary>
    LegacyWithShadow,
}

public sealed class MigrationOptions
{
    /// <summary>Runtime bundle import (PR-02 slice: RuntimeManifest / LayerInstaller verification).</summary>
    public SliceMode BundleImport { get; set; } = SliceMode.LegacyWithShadow;

    /// <summary>
    /// LayerInstaller extraction (PR-02 second half): after a legacy import, the C# extractor dry-runs each layer
    /// and is compared with the installed tree. Needs <see cref="BundleImport"/> shadowing (it reuses the inspection).
    /// </summary>
    public SliceMode LayerExtract { get; set; } = SliceMode.LegacyWithShadow;

    /// <summary>Host-path probe for the extractor; null = the real filesystem (the right choice on a device).</summary>
    public Func<string, bool>? HostPathExists { get; set; }
}
