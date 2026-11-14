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

    /// <summary>
    /// The C# implementation decides and the legacy one does not run. Opt-in per operation until the slice has passed
    /// real-device acceptance; never a default before that (ADR-0007).
    /// </summary>
    CSharp,
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

    /// <summary>
    /// Guest command execution (PR-03 first slice): after the legacy diagnostics, the same fixed commands run through
    /// the C# PRoot launcher and the outputs are compared.
    /// </summary>
    public SliceMode GuestExec { get; set; } = SliceMode.LegacyWithShadow;

    /// <summary>
    /// Plugin management (PR-05): after the legacy <c>plugin.list</c>, the same list runs through the C# PRoot launcher
    /// and the parsed plugin lists are compared. Add / remove stay legacy until list parity holds on a device.
    /// </summary>
    public SliceMode PluginList { get; set; } = SliceMode.LegacyWithShadow;

    /// <summary>
    /// Mobile automation, ui_* registration and status (PR-07 first segment): after each legacy <c>relay.*</c> call the
    /// C# port reads the same patch layer and accessibility setting and is compared. Writing stays legacy.
    /// </summary>
    public SliceMode UiTools { get; set; } = SliceMode.LegacyWithShadow;

    /// <summary>
    /// Relay mailbox envelope decoding (PR-07 second segment): a developer-run probe writes always-rejected requests into
    /// the live legacy Relay's mailbox and compares its answers with the C# decoder. Decoding stays legacy.
    /// </summary>
    public SliceMode RelayEnvelopeCodec { get; set; } = SliceMode.LegacyWithShadow;

    /// <summary>
    /// Relay admission before the approval gate and response encoding (PR-07 third segment): a developer-run probe
    /// sends well-formed requests that admission must reject and compares Relay's answers byte for byte with C#.
    /// Admission stays legacy.
    /// </summary>
    public SliceMode RelayAdmission { get; set; } = SliceMode.LegacyWithShadow;

    /// <summary>
    /// PR-07 segment 4 (execution surface and execution quota): shadow reads the live Relay's published surface
    /// decision (<c>files/pilot/capabilities.json</c>) and compares it with the C# surface policy. Read-only; the
    /// decision and the quota stay legacy.
    /// </summary>
    public SliceMode RelaySurface { get; set; } = SliceMode.LegacyWithShadow;

    /// <summary>
    /// PR-07 segment 5 (approval gate decision, not the prompt): shadow compares the live Relay's published system state,
    /// tier and ceiling per capability with the C# probe / tier store, and a developer-run probe sends two read-only calls
    /// the gate must refuse for a missing system permission (only when accessibility is off). The gate stays legacy.
    /// </summary>
    public SliceMode RelayGate { get; set; } = SliceMode.LegacyWithShadow;

    /// <summary>
    /// PR-07 segment 7 (accessibility read path: ui.snapshot / ui.node): a developer-run shadow sends read calls through
    /// the normal approval and compares the legacy Relay's data with the C# reader on the same live tree. Reading,
    /// every write and the service stay legacy.
    /// </summary>
    public SliceMode RelayA11yRead { get; set; } = SliceMode.LegacyWithShadow;

    /// <summary>
    /// PR-07 segment 8 (ui.waitFor): the same developer-run shadow sends wait calls that all answer at the first poll and
    /// compares the legacy Relay's answers with the C# port (all bytes but <c>waitedMs</c>). Waiting stays legacy.
    /// </summary>
    public SliceMode RelayA11yWaitFor { get; set; } = SliceMode.LegacyWithShadow;

    /// <summary>
    /// PR-07 steps 1–4 (write path, Direct, Shizuku, host / coordinator / services): an opt-in C# Relay host serves its
    /// own mailbox (files/pilot-cs/run); a developer-run shadow sends the same requests to both mailboxes, each through
    /// its own approval, and compares the normalized answers. The guest keeps talking to the legacy Relay.
    /// </summary>
    public SliceMode RelayHost { get; set; } = SliceMode.LegacyWithShadow;

    /// <summary>
    /// PR-09 first segment (remote host profiles, host-key pin, SSH identity): after each legacy <c>remote.*</c> call of
    /// the remote page, the C# port reads the same SharedPreferences list and Keystore key and is compared. SSH stays
    /// legacy (JSch); C# never writes the list or creates the key.
    /// </summary>
    public SliceMode RemoteHosts { get; set; } = SliceMode.LegacyWithShadow;

    /// <summary>
    /// PR-09 second segment (SSH connection, ADR-0012): after each legacy <c>remote.connect</c>, C# opens its own SSH.NET
    /// connection to the same stored profile (same pin, same Keystore key), runs the same probe, disconnects and is
    /// compared. Read-only: never pins, never starts the remote DSH. Long-press "connect" is the opt-in C# connect.
    /// </summary>
    public SliceMode RemoteConnect { get; set; } = SliceMode.LegacyWithShadow;

    /// <summary>Host-path probe for the extractor; null = the real filesystem (the right choice on a device).</summary>
    public Func<string, bool>? HostPathExists { get; set; }
}
