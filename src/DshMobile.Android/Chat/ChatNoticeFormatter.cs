using Android.Content;
using DshMobile.App.Ui;

namespace DshMobile.App.Chat;

/// <summary>
/// 聊天通知文案映射（UI-S1）：把内部错误码翻译为用户可读的简体中文说明；
/// 原始 <c>Code</c> / <c>DiagnosticKey</c> 不被丢弃，而是折叠进可展开的技术详情，便于排查且不伪造信息。
/// </summary>
internal static class ChatNoticeFormatter
{
    /// <summary>生成通知主文案。</summary>
    public static string Describe(Context context, string code, string diagnosticKey, bool isError)
    {
        if (!isError)
        {
            // 非错误通知本身就是脱敏后的状态行，按类型给出中文说明；未识别类型回退到通用提示。
            if (string.Equals(code, "subagent.activity", StringComparison.OrdinalIgnoreCase))
            {
                return Rows.Fmt(context, Resource.String.chat_notice_subagent_fmt, diagnosticKey);
            }
            if (string.Equals(code, "tool.late_result", StringComparison.OrdinalIgnoreCase))
            {
                return context.GetString(Resource.String.chat_notice_late_tool_result);
            }
            if (string.Equals(code, "event.unknown", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(diagnosticKey))
            {
                return context.GetString(Resource.String.chat_notice_unknown_event);
            }
            return diagnosticKey;
        }

        var combined = $"{code} {diagnosticKey}".ToUpperInvariant();
        if (ContainsAny(combined, "TRANSPORT", "CONNECTION", "NETWORK", "SOCKET", "ECONNREFUSED"))
        {
            return context.GetString(Resource.String.chat_notice_transport);
        }
        if (ContainsAny(combined, "CREDENTIAL", "API_KEY", "UNAUTHORIZED", "401"))
        {
            return context.GetString(Resource.String.chat_notice_credentials);
        }
        if (ContainsAny(combined, "TIMEOUT", "TIMED OUT"))
        {
            return context.GetString(Resource.String.chat_notice_timeout);
        }
        if (ContainsAny(combined, "BUSY", "WRITER"))
        {
            return context.GetString(Resource.String.chat_notice_busy);
        }
        if (ContainsAny(combined, "PROTOCOL", "INVALID_JSON", "MULTIPART"))
        {
            return context.GetString(Resource.String.chat_notice_protocol);
        }
        if (ContainsAny(combined, "MEMORY_LIMITED", "OUT_OF_MEMORY"))
        {
            return context.GetString(Resource.String.chat_notice_memory);
        }
        if (ContainsAny(combined, "STORAGE", "NOT_INSTALLED", "LAYER_MISSING"))
        {
            return context.GetString(Resource.String.chat_notice_storage);
        }
        if (ContainsAny(combined, "EXITED", "PROCESS"))
        {
            return context.GetString(Resource.String.chat_notice_process);
        }
        if (ContainsAny(combined, "INTERRUPT", "ABORT", "CANCEL"))
        {
            return context.GetString(Resource.String.chat_notice_interrupted);
        }
        if (ContainsAny(combined, "MAX-TOKENS", "MAX_TOKENS"))
        {
            return context.GetString(Resource.String.chat_notice_max_tokens);
        }
        if (combined.Contains("TURN.FAILED", StringComparison.Ordinal))
        {
            return context.GetString(Resource.String.chat_notice_turn_failed);
        }
        return context.GetString(Resource.String.chat_notice_generic);
    }

    /// <summary>原始错误码技术详情：保留 Code 与 DiagnosticKey，供用户按需展开查看。</summary>
    public static string BuildTechnicalDetail(string code, string diagnosticKey)
    {
        var safeCode = string.IsNullOrWhiteSpace(code) ? "unknown" : code.Trim();
        var safeKey = string.IsNullOrWhiteSpace(diagnosticKey) ? "-" : diagnosticKey.Trim();
        return $"Code: {safeCode}\nDiagnostic: {safeKey}";
    }

    private static bool ContainsAny(string value, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (value.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
