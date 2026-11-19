using Android.Content;
using DshMobile.Application.Runtime;
using DshMobile.Application.Ui;

namespace DshMobile.App.Ui;

/// <summary>User-facing names of <c>BridgeHost</c> operations (the log keeps the wire names).</summary>
public static class OperationNames
{
    public static string Label(Context context, string operation) => context.GetString(operation switch
    {
        HomeStatus.StartOperation => Resource.String.op_start,
        HomeStatus.StopOperation => Resource.String.op_stop,
        HomeStatus.ImportOperation => Resource.String.op_import,
        HomeStatus.ImportDshOperation => Resource.String.op_import_dsh,
        "runtime.rollback" => Resource.String.op_rollback,
        "diagnostics" => Resource.String.op_diagnostics,
        "info" => Resource.String.op_info,
        "dsh.probe" => Resource.String.op_probe,
        PtyProbe.Slice => Resource.String.op_pty,
        HomeStatus.UpdateCheckOperation => Resource.String.op_update_check,
        HomeStatus.UpdateInstallOperation => Resource.String.op_update_install,
        _ => Resource.String.op_generic,
    })!;

    /// <summary>"code: message", or just the code.</summary>
    public static string Error(string? code, string? message) =>
        string.IsNullOrWhiteSpace(message) ? code ?? "?" : $"{code}: {message}";
}
