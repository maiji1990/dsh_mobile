using Android.Content;

namespace DshMobile.App.Ui;

/// <summary>
/// UI-only preferences (ADR-0021). Developer options (diagnostics, probes, PTY self-test, log, Relay surface preference)
/// are hidden until the user taps the version row seven times, like Android's own developer options. Nothing here
/// changes a permission or a security gate.
/// </summary>
public static class UiPrefs
{
    private const string File = "dshmobile-ui";
    private const string DeveloperKey = "developer_options";

    /// <summary>Taps on the version row that turn developer options on.</summary>
    public const int DeveloperTaps = 7;

    public static bool DeveloperEnabled(Context context) =>
        context.GetSharedPreferences(File, FileCreationMode.Private)!.GetBoolean(DeveloperKey, false);

    public static void SetDeveloperEnabled(Context context, bool on)
    {
        var editor = context.GetSharedPreferences(File, FileCreationMode.Private)!.Edit()!;
        editor.PutBoolean(DeveloperKey, on);
        editor.Apply();
    }

    private const string ChannelKey = "update_channel";

    /// <summary>The release channel the user follows (vNext PR-06): <c>stable</c> (default) or <c>preview</c>.</summary>
    public static string UpdateChannel(Context context) =>
        context.GetSharedPreferences(File, FileCreationMode.Private)!.GetString(ChannelKey, null) is "preview" ? "preview" : "stable";

    public static void SetUpdateChannel(Context context, string channel)
    {
        var editor = context.GetSharedPreferences(File, FileCreationMode.Private)!.Edit()!;
        editor.PutString(ChannelKey, channel == "preview" ? "preview" : "stable");
        editor.Apply();
    }

    private const string TerminalFontKey = "terminal_font_size";
    private const string TerminalBarKey = "terminal_key_bar";

    /// <summary>Terminal font size in CSS px (vNext PR-08); the page clamps it to 8…32 as well.</summary>
    public const int TerminalFontDefault = 14;
    public const int TerminalFontMin = 8;
    public const int TerminalFontMax = 32;

    public static int TerminalFontSize(Context context) =>
        Math.Clamp(context.GetSharedPreferences(File, FileCreationMode.Private)!.GetInt(TerminalFontKey, TerminalFontDefault), TerminalFontMin, TerminalFontMax);

    public static void SetTerminalFontSize(Context context, int size)
    {
        var editor = context.GetSharedPreferences(File, FileCreationMode.Private)!.Edit()!;
        editor.PutInt(TerminalFontKey, Math.Clamp(size, TerminalFontMin, TerminalFontMax));
        editor.Apply();
    }

    /// <summary>The extra-keys bar layout (vNext PR-08), as <c>TerminalKeys.FormatBar</c> wrote it; null = default bar.</summary>
    public static string? TerminalKeyBar(Context context) =>
        context.GetSharedPreferences(File, FileCreationMode.Private)!.GetString(TerminalBarKey, null);

    public static void SetTerminalKeyBar(Context context, string? layout)
    {
        var editor = context.GetSharedPreferences(File, FileCreationMode.Private)!.Edit()!;
        if (layout is null)
        {
            editor.Remove(TerminalBarKey);
        }
        else
        {
            editor.PutString(TerminalBarKey, layout);
        }
        editor.Apply();
    }
}
