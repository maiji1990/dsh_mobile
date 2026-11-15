using Android.Content;
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
        "runtime.prepare" => Resource.String.op_prepare,
        "runtime.rollback" => Resource.String.op_rollback,
        "diagnostics" => Resource.String.op_diagnostics,
        "info" => Resource.String.op_info,
        "dsh.probe" => Resource.String.op_probe,
        "terminal.pty-probe" => Resource.String.op_pty,
        _ => Resource.String.op_generic,
    })!;

    /// <summary>"code: message", or just the code.</summary>
    public static string Error(string? code, string? message) =>
        string.IsNullOrWhiteSpace(message) ? code ?? "?" : $"{code}: {message}";
}
