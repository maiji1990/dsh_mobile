using Android.Content;
using Android.Content.PM;
using Android.OS;
using DshMobile.Application.LocalNetwork;
using Google.Android.Material.Dialog;

namespace DshMobile.App.LocalNetwork;

/// <summary>
/// Android 17 Local Network Protection (ADR-0032): the platform facts (build level, targetSdk, manifest, grant state)
/// behind <see cref="LocalNetworkPolicy"/>, plus the one runtime request. The permission is granted per app UID, so a
/// single grant also covers the PRoot guest's curl/ssh - there is nothing to do inside the sandbox.
/// </summary>
public static class LocalNetworkAccess
{
    /// <summary>
    /// The platform permission. There is no <c>Manifest.Permission</c> constant on every compile SDK we build with, so
    /// the string is pinned here; the manifest declares exactly this name.
    /// </summary>
    public const string PermissionName = "android.permission.ACCESS_LOCAL_NETWORK";

    /// <summary>Request code; the activity answers it in <see cref="OnRequestResult"/>.</summary>
    public const int RequestCode = 3701;

    private static TaskCompletionSource<bool>? _pending;

    /// <summary>The current LNP decision (what the UI shows and whether a request is needed).</summary>
    public static LocalNetworkDecision Current(Context context) => LocalNetworkPolicy.Decide(
        (int)Build.VERSION.SdkInt,
        (int)(context.ApplicationInfo?.TargetSdkVersion ?? 0),
        IsDeclared(context),
        IsGranted(context));

    /// <summary>Whether the merged manifest asks for the permission (a build fact, not a runtime grant).</summary>
    public static bool IsDeclared(Context context)
    {
        var info = context.PackageManager?.GetPackageInfo(context.PackageName!, PackageInfoFlags.Permissions);
        return info?.RequestedPermissions?.Contains(PermissionName, StringComparer.Ordinal) == true;
    }

    private static bool IsGranted(Context context) =>
        context.CheckSelfPermission(PermissionName) == Permission.Granted;

    /// <summary>
    /// Explain, then ask. Only asks when the decision is <see cref="LocalNetworkDecision.NeedsPermission"/>; the caller
    /// decides what to do with a denial (starting DSH must continue either way, ADR-0032).
    /// </summary>
    public static async Task<LocalNetworkDecision> EnsureAsync(Activity activity, string? host = null)
    {
        var before = Current(activity);
        if (before != LocalNetworkDecision.NeedsPermission)
        {
            return before;
        }
        if (activity.IsFinishing || activity.IsDestroyed)
        {
            return before;
        }
        if (!await ConfirmAsync(activity, host).ConfigureAwait(true))
        {
            return before;
        }
        await RequestAsync(activity).ConfigureAwait(true);
        return Current(activity);
    }

    /// <summary>Asks the system permission dialog; completes when the user answers.</summary>
    public static Task<bool> RequestAsync(Activity activity)
    {
        if (Current(activity) is not LocalNetworkDecision.NeedsPermission)
        {
            return Task.FromResult(Current(activity) == LocalNetworkDecision.Granted);
        }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = completion;
        activity.RequestPermissions([PermissionName], RequestCode);
        return completion.Task;
    }

    /// <summary>Called from the activity's <c>OnRequestPermissionsResult</c>; every other request code is ignored.</summary>
    public static void OnRequestResult(int requestCode, Permission[]? grantResults)
    {
        if (requestCode != RequestCode)
        {
            return;
        }
        var granted = grantResults is { Length: > 0 } && grantResults[0] == Permission.Granted;
        var pending = _pending;
        _pending = null;
        Android.Util.Log.Info("DshMobile", $"lan permission result: granted={granted}");
        pending?.TrySetResult(granted);
    }

    /// <summary>The rationale dialog; true only when the user chose to ask the system.</summary>
    private static Task<bool> ConfirmAsync(Activity activity, string? host)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new MaterialAlertDialogBuilder(activity)
            .SetTitle(Resource.String.lan_dialog_title)
            .SetMessage(host is null
                ? activity.GetString(Resource.String.lan_dialog_message)
                : activity.GetString(Resource.String.lan_dialog_message_host, host))
            .SetPositiveButton(Resource.String.lan_dialog_allow, (_, _) => completion.TrySetResult(true))
            .SetNegativeButton(Resource.String.lan_dialog_later, (_, _) => completion.TrySetResult(false));
        var dialog = builder.Create();
        dialog.SetOnDismissListener(new DismissListener(() => completion.TrySetResult(false)));
        dialog.Show();
        return completion.Task;
    }

    private sealed class DismissListener(Action onDismiss) : Java.Lang.Object, IDialogInterfaceOnDismissListener
    {
        public void OnDismiss(IDialogInterface? dialog) => onDismiss();
    }
}
