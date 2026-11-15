using Android.Content;
using DshMobile.App.Files;

namespace DshMobile.App.RuntimeUi;

/// <summary>
/// Runtime bundle import from the system document picker (no storage permission, no adb): copy the picked zip into the
/// app cache (an allowed import root), import it with the C# LayerInstaller through <c>BridgeHost</c>, delete the copy.
/// Used by Home and the Runtime screen.
/// </summary>
public static class RuntimeImportFlow
{
    public const int RequestCode = 61;

    public static Intent PickIntent() => DocumentPicker.OpenIntent(["application/zip", ".zip"], multiple: false);

    public static async Task ImportAsync(Context context, Android.Net.Uri uri)
    {
        var bridge = DshMobileApplication.Current.Bridge;
        var resolver = context.ContentResolver!;
        var name = DshMobile.Core.Files.WorkspaceFileSystem.SanitizeFileName(DocumentPicker.DisplayName(resolver, uri));
        var dest = Path.Combine(context.CacheDir!.AbsolutePath, "runtime-import", name);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bridge.Note($"import: copying {name} from picker ...");
            await Task.Run(async () =>
            {
                await using var input = resolver.OpenInputStream(uri) ?? throw new IOException("cannot open picked file");
                await using var output = File.Create(dest);
                await input.CopyToAsync(output);
            });
            bridge.Note($"import: copied {new FileInfo(dest).Length / 1024 / 1024} MiB in {sw.Elapsed.TotalSeconds:F1} s");
            await bridge.RunImportAsync(dest, deleteAfter: true);
        }
        catch (Exception e) when (e is IOException or Java.Lang.Exception or UnauthorizedAccessException)
        {
            bridge.Fail(DshMobile.Application.Ui.HomeStatus.ImportOperation, DshMobile.Runtime.RuntimeErrorCodes.BundleUnreadable, $"{e.GetType().Name}: {e.Message}");
            File.Delete(dest);
        }
    }
}
