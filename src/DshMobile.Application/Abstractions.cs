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
/// its implementation. ADR-0007 made a C# default wait for real-device acceptance; ADR-0016 (owner's decision) made
/// every shadowed slice C# by default ahead of it, with the legacy path kept as an explicit per-call fallback and
/// the real-device acceptance still owed per slice.
/// </summary>
public enum SliceMode
{
    /// <summary>Legacy DSHBox bridge only.</summary>
    Legacy,
    /// <summary>Legacy decides; the C# port runs on the same input read-only and divergences are reported.</summary>
    LegacyWithShadow,

    /// <summary>
    /// The C# implementation decides and the legacy one does not run (the default of every shadowed slice since
    /// ADR-0016). Shadows that compare a legacy run still fire when the user falls back to the legacy path.
    /// </summary>
    CSharp,
}

/// <summary>
/// Per-slice implementation choice. Since ADR-0016 every slice that had a C# shadow defaults to
/// <see cref="SliceMode.CSharp"/>; the legacy bridge stays reachable as an explicit per-call fallback (long-press /
/// choice dialog), and a legacy run on that fallback is still shadowed by C# where a shadow exists.
/// </summary>
public sealed class MigrationOptions
{
    /// <summary>
    /// Runtime bundle import (PR-02: RuntimeManifest verification + LayerInstaller). <see cref="SliceMode.CSharp"/>
    /// (default): the C# <c>LayerInstaller</c> verifies, extracts and swaps, then the installed tree is checked against a
    /// fresh dry-run (<c>runtime.layer-install</c>). A per-call <see cref="SliceMode.LegacyWithShadow"/> is the legacy
    /// fallback, with the C# inspector / extractor shadowing it.
    /// </summary>
    public SliceMode BundleImport { get; set; } = SliceMode.CSharp;

    /// <summary>
    /// LayerInstaller extraction shadow on a legacy import: the C# extractor dry-runs each layer and is compared with the
    /// installed tree. Only used when the import itself runs legacy (fallback).
    /// </summary>
    public SliceMode LayerExtract { get; set; } = SliceMode.LegacyWithShadow;

    /// <summary>
    /// Guest command execution (PR-03). <see cref="SliceMode.CSharp"/> (default): the fixed diagnostics commands run
    /// through the C# PRoot launcher only; <see cref="SliceMode.LegacyWithShadow"/>: legacy <c>runtime.diagnostics</c>
    /// with the C# run compared. The supervisor / launcher-argv shadow of legacy-started processes runs unless
    /// <see cref="SliceMode.Legacy"/>.
    /// </summary>
    public SliceMode GuestExec { get; set; } = SliceMode.CSharp;

    /// <summary>
    /// Plugin list (PR-05). <see cref="SliceMode.CSharp"/> (default): the plugin page lists through the C# PRoot launcher
    /// (<c>dsh plugin list --json --depth 0</c>); otherwise legacy <c>plugin.list</c> with the C# list shadowing it.
    /// </summary>
    public SliceMode PluginList { get; set; } = SliceMode.CSharp;

    /// <summary>
    /// Mobile automation (PR-07, segments 1–9 and the guest switch of ADR-0010): <see cref="RelayHost"/> =
    /// <see cref="SliceMode.CSharp"/> (default) makes "start relay" stop the legacy Relay and let the C# Relay host serve
    /// the guest mailbox, publish the guest files and register the ui_* layer. The per-segment options below only gate
    /// the shadows that compare a legacy Relay run (on the legacy fallback); <see cref="SliceMode.Legacy"/> turns one off.
    /// </summary>
    public SliceMode UiTools { get; set; } = SliceMode.CSharp;

    /// <summary>PR-07 segment 2 (mailbox envelope decoding); shadow of a legacy Relay only (see <see cref="UiTools"/>).</summary>
    public SliceMode RelayEnvelopeCodec { get; set; } = SliceMode.CSharp;

    /// <summary>PR-07 segment 3 (admission + response encoding); shadow of a legacy Relay only.</summary>
    public SliceMode RelayAdmission { get; set; } = SliceMode.CSharp;

    /// <summary>PR-07 segment 4 (execution surface + quota); shadow of a legacy Relay only.</summary>
    public SliceMode RelaySurface { get; set; } = SliceMode.CSharp;

    /// <summary>PR-07 segment 5 (approval gate decision); shadow of a legacy Relay only.</summary>
    public SliceMode RelayGate { get; set; } = SliceMode.CSharp;

    /// <summary>PR-07 segment 7 (accessibility read path); shadow of a legacy Relay only.</summary>
    public SliceMode RelayA11yRead { get; set; } = SliceMode.CSharp;

    /// <summary>PR-07 segment 8 (<c>ui.waitFor</c>); shadow of a legacy Relay only.</summary>
    public SliceMode RelayA11yWaitFor { get; set; } = SliceMode.CSharp;

    /// <summary>
    /// Who serves the guest mailbox when the user starts the relay (ADR-0010 / ADR-0016). <see cref="SliceMode.CSharp"/>
    /// (default): the C# Relay host (approval on the C# page / floating / notification card); anything else: the legacy
    /// Relay. Long-press "start relay" offers both. Also gates the dual-relay developer shadow (unless Legacy).
    /// </summary>
    public SliceMode RelayHost { get; set; } = SliceMode.CSharp;

    /// <summary>
    /// PR-09 first segment (host profiles, host-key pin, SSH identity). <see cref="SliceMode.CSharp"/> (default): the
    /// remote page reads and writes the profile list (same SharedPreferences file and format as the legacy
    /// <c>RemoteHosts</c>), pins only the key the C# connection last presented, and creates the non-exportable Keystore
    /// key (same alias and parameters as the legacy <c>SshKeys</c>). Otherwise legacy <c>remote.*</c> + C# shadow.
    /// </summary>
    public SliceMode RemoteHosts { get; set; } = SliceMode.CSharp;

    /// <summary>
    /// PR-09 second segment (SSH connection, ADR-0012). <see cref="SliceMode.CSharp"/> (default): "connect" is the C#
    /// SSH.NET connection; long-press is the legacy JSch fallback, which the C# connection still shadows (unless Legacy).
    /// </summary>
    public SliceMode RemoteConnect { get; set; } = SliceMode.CSharp;

    /// <summary>
    /// PR-09 third segment (remote dsh web start / stop, ADR-0013): on a C# connection start / stop run in C#; after a
    /// legacy start / stop (fallback connection) the C# check still shadows it (unless Legacy).
    /// </summary>
    public SliceMode RemoteDsh { get; set; } = SliceMode.CSharp;

    /// <summary>
    /// Who starts and owns the sandbox + DSH processes (ADR-0015). <see cref="SliceMode.CSharp"/> (default): the C#
    /// <c>DshHost</c>; any other value: the legacy DSHBox sandbox-manager through the bridge (explicit fallback, needed
    /// for browser automation). A per-call mode passed to <c>RuntimeCoordinator.StartAsync</c> overrides this.
    /// </summary>
    public SliceMode RuntimeHost { get; set; } = SliceMode.CSharp;

    /// <summary>
    /// Bundled DSH layer provisioning (<c>runtime.prepare</c>, MIGRATION row 17). <see cref="SliceMode.CSharp"/>
    /// (default, ADR-0016): the C# <c>RuntimePreparer</c> decides (no DSHBox code); <see cref="SliceMode.LegacyWithShadow"/>:
    /// the legacy bridge decides and C# predicts the steps and dry-runs the same asset. A per-call mode passed to
    /// <c>RuntimeCoordinator.PrepareAsync</c> overrides this.
    /// </summary>
    public SliceMode DshProvision { get; set; } = SliceMode.CSharp;

    /// <summary>
    /// Terminal (PR-06, MIGRATION rows 30–31): <see cref="SliceMode.CSharp"/> (default, ADR-0016) opens the C#
    /// multi-session terminal on <c>libdshmobile_native</c> PTYs; any other value opens the legacy TerminalView (whose
    /// sessions the <c>terminal.launcher-argv</c> shadow still compares). Long-press "terminal" is the explicit fallback.
    /// </summary>
    public SliceMode Terminal { get; set; } = SliceMode.CSharp;

    /// <summary>Host-path probe for the extractor; null = the real filesystem (the right choice on a device).</summary>
    public Func<string, bool>? HostPathExists { get; set; }
}
