using System.Text.Json;
using DshMobile.Agent.Protocol;
using DshMobile.Agent.Transport;

namespace DshMobile.Agent.Domain;

/// <summary>
/// Strongly typed domain failure taxonomy shared across Gateways, Engines, and Presentation (ADR-0033, N-02-01).
/// Never carries raw exception strings or host filesystem paths to UI layers.
/// </summary>
public enum DshFailureCode
{
    Unavailable,
    Unsupported,
    Unauthorized,
    CredentialsMissing,
    InvalidState,
    SessionBusy,
    SessionActive,
    SessionNotFound,
    StaleRequest,
    ProcessExited,
    TransportLost,
    Timeout,
    ProtocolMismatch,
    CorruptHistory,
    StorageUnavailable,
    PhantomProcessLimit,
    MemoryLimited,
}

public sealed record DshFailure(
    DshFailureCode Code,
    string? DiagnosticKey = null,
    bool Retryable = false);

/// <summary>Zero-state success marker for void-returning <see cref="Outcome{T}"/> operations.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value;
}

/// <summary>Typed domain result replacing throwing across async Gateway / Engine boundaries.</summary>
public readonly record struct Outcome<T>
{
    public bool IsOk { get; }
    public T? Value { get; }
    public DshFailure? Error { get; }

    private Outcome(bool isOk, T? value, DshFailure? error)
    {
        IsOk = isOk;
        Value = value;
        Error = error;
    }

#pragma warning disable CA1000 // Idiomatic Outcome<T>.Ok / Fail factories match DshMobile domain API
    public static Outcome<T> Ok(T value) => new(true, value, null);

    public static Outcome<T> Fail(DshFailure error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(false, default, error);
    }

    public static Outcome<T> Fail(DshFailureCode code, string? diagnosticKey = null, bool retryable = false) =>
        new(false, default, new DshFailure(code, diagnosticKey, retryable));
#pragma warning restore CA1000

    public Outcome<TNext> Map<TNext>(Func<T, TNext> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return IsOk ? Outcome<TNext>.Ok(selector(Value!)) : Outcome<TNext>.Fail(Error!);
    }

    public Outcome<TNext> Bind<TNext>(Func<T, Outcome<TNext>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        return IsOk ? binder(Value!) : Outcome<TNext>.Fail(Error!);
    }

    public TResult Match<TResult>(Func<T, TResult> onOk, Func<DshFailure, TResult> onFail)
    {
        ArgumentNullException.ThrowIfNull(onOk);
        ArgumentNullException.ThrowIfNull(onFail);
        return IsOk ? onOk(Value!) : onFail(Error!);
    }
}

/// <summary>
/// Pure mapper from Remote wire errors and transport exceptions to <see cref="DshFailure"/> (N-02-01).
/// Never forwards raw exception messages.
/// </summary>
public static class DshFailureMapper
{
    public static DshFailure FromRpcError(RemoteRpcError? error)
    {
        if (error is null || string.IsNullOrWhiteSpace(error.Code))
        {
            return new DshFailure(DshFailureCode.ProtocolMismatch, "rpc.missing_error");
        }

        return error.Code switch
        {
            "session/provider-credentials-unavailable" =>
                new DshFailure(DshFailureCode.CredentialsMissing, error.Code, Retryable: false),

            "session/provider-models-unavailable" or
            "session/model-unavailable" or
            "session/agent-busy" or
            "session/writer-held" =>
                new DshFailure(DshFailureCode.SessionBusy, error.Code, Retryable: true),

            "workspace/session-active" =>
                new DshFailure(DshFailureCode.SessionActive, error.Code, Retryable: false),

            "session/not-found" =>
                new DshFailure(DshFailureCode.SessionNotFound, error.Code, Retryable: false),

            "session/conflict" or
            "session/invalid-time-zone" or
            "session/attachment-invalid" or
            "session/title-invalid" or
            "session/fork-unavailable" or
            "session/steer-unavailable" or
            "session/workspace-attach-failed" or
            "agent-preset/conflict" =>
                new DshFailure(DshFailureCode.InvalidState, error.Code, Retryable: false),

            "session/queue-item-not-found" or
            "session/observation-ended" =>
                new DshFailure(DshFailureCode.StaleRequest, error.Code, Retryable: false),

            "session/migration-required" =>
                new DshFailure(DshFailureCode.CorruptHistory, error.Code, Retryable: false),

            "session/projections-unavailable" =>
                new DshFailure(DshFailureCode.Unavailable, error.Code, Retryable: true),

            "session/wait-timeout" =>
                new DshFailure(DshFailureCode.Timeout, error.Code, Retryable: true),

            _ when error.Code.StartsWith("credentials/", StringComparison.Ordinal) =>
                error.Code.Contains("not-found", StringComparison.Ordinal) ||
                error.Code.Contains("missing", StringComparison.Ordinal)
                    ? new DshFailure(DshFailureCode.CredentialsMissing, error.Code, Retryable: false)
                    : new DshFailure(DshFailureCode.InvalidState, error.Code, Retryable: false),

            _ when error.Code.StartsWith("settings/", StringComparison.Ordinal) =>
                error.Code.Contains("revision", StringComparison.Ordinal) ||
                error.Code.Contains("conflict", StringComparison.Ordinal)
                    ? new DshFailure(DshFailureCode.StaleRequest, error.Code, Retryable: true)
                    : new DshFailure(DshFailureCode.InvalidState, error.Code, Retryable: false),

            _ when error.Code.StartsWith("unauthorized", StringComparison.OrdinalIgnoreCase) =>
                new DshFailure(DshFailureCode.Unauthorized, error.Code, Retryable: true),

            _ => new DshFailure(DshFailureCode.Unsupported, error.Code, Retryable: false),
        };
    }

    public static DshFailure FromException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex switch
        {
            RemoteAuthenticationException => new DshFailure(DshFailureCode.Unauthorized, "transport.unauthorized", Retryable: true),
            RemoteDrainingException => new DshFailure(DshFailureCode.TransportLost, "transport.draining", Retryable: false),
            RemoteProtocolException => new DshFailure(DshFailureCode.ProtocolMismatch, "transport.protocol_mismatch", Retryable: false),
            JsonException => new DshFailure(DshFailureCode.ProtocolMismatch, "transport.invalid_json", Retryable: false),
            InvalidDataException => new DshFailure(DshFailureCode.ProtocolMismatch, "transport.invalid_multipart", Retryable: false),
            RemoteConnectionException => new DshFailure(DshFailureCode.TransportLost, "transport.connection_lost", Retryable: true),
            HttpRequestException => new DshFailure(DshFailureCode.TransportLost, "transport.http_error", Retryable: true),
            TimeoutException => new DshFailure(DshFailureCode.Timeout, "transport.timeout", Retryable: true),
            _ => new DshFailure(DshFailureCode.Unavailable, $"transport.{ex.GetType().Name}", Retryable: true),
        };
    }

    /// <summary>将网关推送的会话错误事件安全分类为领域错误码，避免直接透传原始异常字符串（F3 / N-02-01）。</summary>
    public static DshFailure FromSessionError(SessionErrorGatewayEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        var raw = sessionEvent.Message ?? string.Empty;
        if (raw.Contains("CREDENTIAL", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("API_KEY", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("provider-credentials", StringComparison.OrdinalIgnoreCase))
        {
            return new DshFailure(DshFailureCode.CredentialsMissing, "session_error.credentials_missing", Retryable: false);
        }
        if (raw.Contains("UNAUTHORIZED", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("401", StringComparison.Ordinal))
        {
            return new DshFailure(DshFailureCode.Unauthorized, "session_error.unauthorized", Retryable: true);
        }
        if (raw.Contains("TRANSPORT", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("NETWORK", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("SOCKET", StringComparison.OrdinalIgnoreCase))
        {
            return new DshFailure(DshFailureCode.TransportLost, "session_error.transport", Retryable: true);
        }
        if (raw.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("TIMED OUT", StringComparison.OrdinalIgnoreCase))
        {
            return new DshFailure(DshFailureCode.Timeout, "session_error.timeout", Retryable: true);
        }
        if (raw.Contains("BUSY", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("WRITER", StringComparison.OrdinalIgnoreCase))
        {
            return new DshFailure(DshFailureCode.SessionBusy, "session_error.busy", Retryable: true);
        }
        return new DshFailure(DshFailureCode.Unavailable, "session_error.unknown", Retryable: true);
    }

    /// <summary>将进程退出码映射为领域失败码（N-08-03：捕获 exit 137 归类为幻影进程限制）。</summary>
    public static DshFailure FromProcessExitCode(int exitCode) => exitCode switch
    {
        137 => new DshFailure(DshFailureCode.PhantomProcessLimit, "process.exit_137", Retryable: false),
        _ => new DshFailure(DshFailureCode.ProcessExited, "process.exited", Retryable: true),
    };

    /// <summary>将宿主/运行层错误码安全映射为领域失败码（N-08-03 / N-08-04）。</summary>
    public static DshFailure FromRuntimeErrorCode(string? runtimeCode) => runtimeCode switch
    {
        "PHANTOM_PROCESS_LIMIT" => new DshFailure(DshFailureCode.PhantomProcessLimit, "runtime.phantom_process_limit", Retryable: false),
        "MEMORY_LIMITED" => new DshFailure(DshFailureCode.MemoryLimited, "runtime.memory_limited", Retryable: false),
        "DSH_EXITED" => new DshFailure(DshFailureCode.ProcessExited, "runtime.dsh_exited", Retryable: true),
        "DSH_NOT_READY" => new DshFailure(DshFailureCode.Timeout, "runtime.dsh_not_ready", Retryable: true),
        "RUNTIME_NOT_INSTALLED" or "NODE_LAYER_MISSING" => new DshFailure(DshFailureCode.StorageUnavailable, "runtime.not_installed", Retryable: false),
        _ => new DshFailure(DshFailureCode.Unavailable, "runtime.unavailable", Retryable: true),
    };
}
