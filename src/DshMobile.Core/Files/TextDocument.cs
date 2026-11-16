using System.Text;
using System.Text.Json;

namespace DshMobile.Core.Files;

public enum TextEncodingKind
{
    Utf8,
    Utf8Bom,
    Utf16Le,
    Utf16Be,
}

/// <summary>What kind of text a file name suggests (label + optional validation in the editor).</summary>
public enum TextKind
{
    Plain,
    Markdown,
    Json,
    Yaml,
    Code,
}

/// <summary>
/// A text file opened in the editor (vNext PR-09): its decoded text, how to write it back byte-compatibly (encoding,
/// BOM, dominant line ending) and the <see cref="FileVersion"/> it was read at, which the save passes back so a file
/// changed meanwhile is a <see cref="WorkspaceErrors.Conflict"/>, never silently overwritten.
/// <para>Rules: at most <see cref="EditLimit"/> bytes; UTF-8 (with or without BOM) or UTF-16 with a BOM is editable;
/// anything else that is not valid UTF-8 opens <b>read-only</b> (shown with U+FFFD, never re-encoded); NUL bytes without
/// a UTF-16 BOM mean binary — no text at all.</para>
/// </summary>
public sealed record TextDocument(
    string Text,
    TextEncodingKind Encoding,
    string LineEnding,
    FileVersion Version,
    bool ReadOnly,
    string? ReadOnlyReason)
{
    public const long EditLimit = 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Decodes bytes read at <paramref name="version"/>; null for binary.</summary>
    public static TextDocument? Decode(byte[] bytes, FileVersion version)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(version);
        if (bytes.Length > EditLimit)
        {
            throw new WorkspaceException(WorkspaceErrors.TooLarge, $"{bytes.Length} bytes > {EditLimit}");
        }
        string text;
        TextEncodingKind kind;
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            kind = TextEncodingKind.Utf8Bom;
            text = TryUtf8(bytes.AsSpan(3), out var t) ? t : null!;
            if (text is null)
            {
                return ReadOnlyLossy(bytes.AsSpan(3), kind, version, "contains bytes that are not UTF-8");
            }
        }
        else if (bytes is [0xFF, 0xFE, ..])
        {
            kind = TextEncodingKind.Utf16Le;
            text = System.Text.Encoding.Unicode.GetString(bytes, 2, (bytes.Length - 2) & ~1);
        }
        else if (bytes is [0xFE, 0xFF, ..])
        {
            kind = TextEncodingKind.Utf16Be;
            text = System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, (bytes.Length - 2) & ~1);
        }
        else if (bytes.AsSpan().Contains((byte)0))
        {
            return null; // binary
        }
        else
        {
            kind = TextEncodingKind.Utf8;
            if (!TryUtf8(bytes, out text))
            {
                return ReadOnlyLossy(bytes, kind, version, "not UTF-8 (e.g. GBK / Latin-1): shown read-only so it is never re-encoded");
            }
        }
        return new TextDocument(text, kind, DominantLineEnding(text), version, false, null);
    }

    private static TextDocument ReadOnlyLossy(ReadOnlySpan<byte> bytes, TextEncodingKind kind, FileVersion version, string reason)
    {
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        return new TextDocument(text, kind, DominantLineEnding(text), version, true, reason);
    }

    private static bool TryUtf8(ReadOnlySpan<byte> bytes, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }

    /// <summary>"\r\n", "\r" or "\n" — whichever the file uses most ("\n" for a file without line breaks).</summary>
    public static string DominantLineEnding(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }
        return crlf > lf && crlf >= cr ? "\r\n" : cr > lf && cr > crlf ? "\r" : "\n";
    }

    /// <summary>
    /// Bytes for the edited text in this document's encoding (BOM kept) with line endings normalised to
    /// <see cref="LineEnding"/> (an Android editor hands back "\n"). Refused for a read-only document.
    /// </summary>
    public byte[] Encode(string edited)
    {
        ArgumentNullException.ThrowIfNull(edited);
        if (ReadOnly)
        {
            throw new WorkspaceException(WorkspaceErrors.NotSupported, "read-only: " + ReadOnlyReason);
        }
        var normalised = edited.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (LineEnding != "\n")
        {
            normalised = normalised.Replace("\n", LineEnding, StringComparison.Ordinal);
        }
        var (preamble, enc) = Encoding switch
        {
            TextEncodingKind.Utf8Bom => (new byte[] { 0xEF, 0xBB, 0xBF }, (Encoding)StrictUtf8),
            TextEncodingKind.Utf16Le => ([0xFF, 0xFE], System.Text.Encoding.Unicode),
            TextEncodingKind.Utf16Be => ([0xFE, 0xFF], System.Text.Encoding.BigEndianUnicode),
            _ => ([], StrictUtf8),
        };
        var body = enc.GetBytes(normalised);
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        if (result.Length > EditLimit)
        {
            throw new WorkspaceException(WorkspaceErrors.TooLarge, $"{result.Length} bytes > {EditLimit}");
        }
        return result;
    }

    public static TextKind KindOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".md" or ".markdown" => TextKind.Markdown,
            ".json" or ".jsonc" => TextKind.Json,
            ".yaml" or ".yml" => TextKind.Yaml,
            ".cs" or ".js" or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".jsx" or ".py" or ".sh" or ".bash" or ".go" or ".rs"
                or ".java" or ".kt" or ".c" or ".h" or ".cpp" or ".hpp" or ".css" or ".html" or ".xml" or ".toml" or ".ini"
                or ".sql" or ".rb" or ".php" or ".swift" or ".lua" or ".jsonl" => TextKind.Code,
            _ => TextKind.Plain,
        };
    }

    /// <summary>A warning to show before saving (never blocks): today only JSON is checked. Null when fine.</summary>
    public static string? Validate(TextKind kind, string text)
    {
        if (kind != TextKind.Json || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        try
        {
            using var _ = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return null;
        }
        catch (JsonException e)
        {
            return $"JSON: {e.Message}";
        }
    }
}
