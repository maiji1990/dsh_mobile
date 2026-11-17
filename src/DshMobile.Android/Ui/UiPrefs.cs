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
}
