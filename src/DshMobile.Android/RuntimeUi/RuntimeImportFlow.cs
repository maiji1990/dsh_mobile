using Android.Content;
using DshMobile.App.Files;

namespace DshMobile.App.RuntimeUi;

/// <summary>
/// Runtime imports from the system document picker (no storage permission, no adb): copy the picked file into the
/// app cache (an allowed import root), import it with the C# installers through <c>BridgeHost</c>, delete the copy.
/// Two flows, both used by Home and the Runtime screen: the runtime bundle zip (base/node/android-side) and, since
/// ADR-0027, the DSH layer archive (<c>&lt;version&gt;.tar.zst</c>).
/// </summary>
public static class RuntimeImportFlow
{
    public const int RequestCode = 61;
    public const int DshRequestCode = 62;

    public static Intent PickIntent() => DocumentPicker.OpenIntent(["application/zip", ".zip"], multiple: false);

    /// <summary>
    /// Any file: providers rarely agree on a MIME type for <c>.tar.zst</c>, so filtering by MIME would hide the
    /// archive. The name is validated after the pick, then the coordinator validates the package.
    /// </summary>
    public static Intent PickDshIntent() => DocumentPicker.OpenIntent(["*/*"], multiple: false);

    public static Task ImportAsync(Android.Net.Uri uri) => DshMobileApplication.Current.Bridge.RunPickedImportAsync(uri, dsh: false);

    public static Task ImportDshAsync(Android.Net.Uri uri) => DshMobileApplication.Current.Bridge.RunPickedImportAsync(uri, dsh: true);
}
