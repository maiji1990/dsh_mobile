// Ported from this repo's native/native-bridge/src/main/java/dshmobile/nativebridge/MobileAutomation.kt
// (object UiToolsRegistration, transition code). The overlay path is DSHBox common Constants.kt
// DSH_OFFICIAL_PLUGIN_OVERLAY_RELATIVE_PATH (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4).
// Registered in docs/UPSTREAMS.md. Behaviour locked by tests/TestVectors/ui-tools-registration.json (legacy Kotlin
// LegacyUiToolsRegistrationParityTest runs the same cases).

using System.Text;

namespace DshMobile.Runtime.Automation;

/// <summary>
/// Registers the guest <c>ui_*</c> facade (<c>ui-facade.mjs</c>, an MCP stdio server) with DSH while mobile automation
/// is on (TASK-010, ADR-0004). DSH is not modified: the entry is an official <c>dsh-mcp-client</c> plugin row in the
/// host-owned <c>--patch</c> layer that the launcher passes only when present and non-empty. The user's profile
/// <c>cordis.patch.yml</c> is never touched. Patch layers are read when DSH starts.
/// </summary>
public static class UiToolsRegistration
{
    /// <summary>Guest path of the facade (host: guest cache dir <c>dshmobile/</c>, bound at <c>/opt/dshmobile-cache</c>).</summary>
    public const string GuestFacade = "/opt/dshmobile-cache/dshmobile/ui-facade.mjs";

    /// <summary>First line of the layer; only a file carrying it is ever replaced or deleted by us.</summary>
    public const string Marker = "# dshmobile:ui-tools v1";

    /// <summary>Above the facade's own approval wait (90 s) so DSH never cuts a call that is still waiting.</summary>
    public const int ToolCallTimeoutMs = 120_000;

    /// <summary>MCP server name, hence the tool prefix <c>mcp__ui__</c>.</summary>
    public const string ServerName = "ui";

    /// <summary>Host path relative to user-data (DSHBox <c>DSH_OFFICIAL_PLUGIN_OVERLAY_RELATIVE_PATH</c>).</summary>
    public const string OverlayRelativePath = ".dsh/dshmobile/dsh-official-plugin/dsh-official-plugin-overlay.yml";

    public static string OverlayFile(string userDataDir)
    {
        ArgumentNullException.ThrowIfNull(userDataDir);
        return Path.Combine([userDataDir, .. OverlayRelativePath.Split('/')]);
    }

    /// <summary>The whole layer: one top-level YAML sequence (a second top-level node would make DSH drop the layer).</summary>
    public static string Render() => new StringBuilder()
        .Append(Marker).Append('\n')
        .Append("# Written by DSH Mobile while 手机操控 is on; deleted when it is turned off. ui_* tools (every write goes through Relay approval).\n")
        .Append("- insert:\n")
        .Append("    - id: dshmobile-ui\n")
        .Append("      name: '@deepseek-ai/dsh-mcp-client'\n")
        .Append("      config:\n")
        .Append("        serverName: ").Append(ServerName).Append('\n')
        .Append("        transport: stdio\n")
        .Append("        command: !!js process.execPath\n")
        .Append("        args: ['").Append(GuestFacade).Append("']\n")
        .Append("        toolCallTimeoutMs: ").Append(ToolCallTimeoutMs).Append('\n')
        .Append("        failOnStartupError: false\n")
        .ToString();

    /// <summary>A regular file whose first line (split on LF, CRLF or CR, as Kotlin <c>useLines</c>) is the marker.</summary>
    public static bool IsRegistered(string userDataDir)
    {
        var path = OverlayFile(userDataDir);
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            return reader.ReadLine() == Marker;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Something is there (a file or even a directory) that is not ours: never overwritten or deleted.</summary>
    public static bool HasConflict(string userDataDir)
    {
        var path = OverlayFile(userDataDir);
        return (File.Exists(path) || Directory.Exists(path)) && !IsRegistered(userDataDir);
    }

    /// <summary>
    /// Writes the layer atomically (an empty or half-written patch file would stop DSH from starting). False, without
    /// writing, when the path holds someone else's content.
    /// </summary>
    public static bool Register(string userDataDir)
    {
        if (HasConflict(userDataDir))
        {
            return false;
        }
        var path = OverlayFile(userDataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Render(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
        return true;
    }

    /// <summary>Removes the layer if it is ours; false (file left as is) when someone else's content is there.</summary>
    public static bool Unregister(string userDataDir)
    {
        var path = OverlayFile(userDataDir);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return true;
        }
        if (!IsRegistered(userDataDir))
        {
            return false;
        }
        File.Delete(path);
        return true;
    }
}
