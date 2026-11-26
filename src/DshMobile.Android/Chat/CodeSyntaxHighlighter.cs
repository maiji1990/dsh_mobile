using System;
using System.Collections.Generic;

namespace DshMobile.App.Chat;

/// <summary>轻量代码着色 token 类别；只描述语义，不携带任何 Android 类型。</summary>
internal enum CodeSyntaxTokenKind
{
    /// <summary>行注释或块注释。</summary>
    Comment,

    /// <summary>单引号、双引号或反引号字符串。</summary>
    String,

    /// <summary>常见语言关键字。</summary>
    Keyword,

    /// <summary>十进制、十六进制或浮点数字。</summary>
    Number,

    /// <summary>Shell 行首提示符 <c>$ </c>。</summary>
    Prompt,

    /// <summary>绝对 / 相对 / 家目录 / Windows 盘符路径。</summary>
    Path,
}

/// <summary>代码着色结果中的半开区间 <c>[Start, Start + Length)</c>。</summary>
internal readonly record struct CodeSyntaxToken(int Start, int Length, CodeSyntaxTokenKind Kind);

/// <summary>
/// 纯 C# 轻量语法高亮器：单次正向字符扫描，不使用正则、不做回溯，
/// 只识别注释、字符串、关键字、数字、Shell 提示符与路径，并由 Android 层映射为文本 Span。
/// 超长代码或异常语言直接返回空结果，避免在 RecyclerView 绑定期间产生不可控开销。
/// </summary>
internal static class CodeSyntaxHighlighter
{
    /// <summary>超过该字符数的代码不进行着色，优先保证长输出的滚动流畅度。</summary>
    private const int MaxHighlightLength = 20_000;

    /// <summary>单段代码最多生成的 token 数，防止极端输出撑大 Span 数量。</summary>
    private const int MaxTokens = 4_096;

    /// <summary>常见关键字合集；按语言族补充，但不做大小写归一，避免误伤变量名。</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "else", "elif", "then", "fi", "for", "foreach", "while", "until", "do", "done",
        "switch", "case", "esac", "break", "continue", "return", "yield", "goto",
        "class", "struct", "interface", "record", "enum", "namespace", "using", "import", "from",
        "public", "private", "protected", "internal", "static", "readonly", "sealed", "abstract",
        "virtual", "override", "async", "await", "new", "delete", "this", "base", "self",
        "def", "lambda", "pass", "with", "as", "in", "is", "and", "or", "not",
        "try", "catch", "finally", "throw", "throws", "raise", "except",
        "function", "fn", "func", "let", "const", "var", "val", "mut", "ref", "out", "params",
        "void", "int", "long", "short", "byte", "char", "bool", "boolean", "float", "double",
        "string", "object", "dynamic", "decimal", "null", "nil", "none", "true", "false",
        "None", "True", "False",
        "select", "insert", "update", "values", "create", "table", "alter", "drop", "join",
        "where", "group", "order", "limit", "offset", "into", "set", "on", "having",
        "echo", "export", "local", "declare", "unset", "alias", "source", "exit",
    };

    /// <summary>对一段代码生成不重叠的着色 token；语言无法识别时按通用文本处理。</summary>
    public static IReadOnlyList<CodeSyntaxToken> Highlight(string code, string? language)
    {
        if (string.IsNullOrEmpty(code) || code.Length > MaxHighlightLength)
        {
            return Array.Empty<CodeSyntaxToken>();
        }

        var family = ResolveFamily(language);
        var tokens = new List<CodeSyntaxToken>();
        var index = 0;
        var atLineStart = true;

        while (index < code.Length && tokens.Count < MaxTokens)
        {
            var ch = code[index];

            if (ch == '\n')
            {
                index++;
                atLineStart = true;
                continue;
            }
            if (ch is ' ' or '\t' or '\r')
            {
                index++;
                continue;
            }

            // Shell 行首提示符 `$ ` 单独着色，帮助阅读命令与输出混排。
            if (atLineStart && ch == '$' && index + 1 < code.Length && code[index + 1] == ' ')
            {
                Add(tokens, index, 2, CodeSyntaxTokenKind.Prompt);
                index += 2;
                atLineStart = false;
                continue;
            }

            atLineStart = false;

            // 注释优先于路径与字符串，避免 `#`、`//` 被误识别成代码内容。
            if (TryComment(code, index, family, out var commentEnd))
            {
                Add(tokens, index, commentEnd - index, CodeSyntaxTokenKind.Comment);
                index = commentEnd;
                continue;
            }

            if (ch is '"' or '\'' or '`')
            {
                var stringEnd = ScanString(code, index, ch, family is LanguageFamily.Python);
                Add(tokens, index, stringEnd - index, CodeSyntaxTokenKind.String);
                index = stringEnd;
                continue;
            }

            if (TryPath(code, index, out var pathEnd))
            {
                Add(tokens, index, pathEnd - index, CodeSyntaxTokenKind.Path);
                index = pathEnd;
                continue;
            }

            if (char.IsDigit(ch))
            {
                var numberEnd = ScanNumber(code, index);
                Add(tokens, index, numberEnd - index, CodeSyntaxTokenKind.Number);
                index = numberEnd;
                continue;
            }

            if (IsIdentifierStart(ch))
            {
                var wordEnd = index + 1;
                while (wordEnd < code.Length && IsIdentifierPart(code[wordEnd]))
                {
                    wordEnd++;
                }
                var word = code.AsSpan(index, wordEnd - index);
                if (Keywords.Contains(word.ToString()))
                {
                    Add(tokens, index, wordEnd - index, CodeSyntaxTokenKind.Keyword);
                }
                index = wordEnd;
                continue;
            }

            index++;
        }

        return tokens;
    }

    /// <summary>
    /// 面向终端卡片 / 全量输出的保守着色：只对以 <c>$ </c> 开头的命令行按 Shell 语义着色，
    /// 其余 stdout / stderr 纯文本一律不着色，避免把 <c>is</c> / <c>on</c> / <c>this</c> / <c>or</c>
    /// 等普通英文错误消息词误染成关键字亮绿。
    /// </summary>
    public static IReadOnlyList<CodeSyntaxToken> HighlightTerminal(string terminalText)
    {
        if (string.IsNullOrEmpty(terminalText) || terminalText.Length > MaxHighlightLength)
        {
            return Array.Empty<CodeSyntaxToken>();
        }

        var tokens = new List<CodeSyntaxToken>();
        var lineStart = 0;
        while (lineStart < terminalText.Length && tokens.Count < MaxTokens)
        {
            var newline = terminalText.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? terminalText.Length : newline;
            var lineLength = lineEnd - lineStart;

            // 仅行首 `$ ` 视为真实命令行；带缩进或普通输出不参与着色。
            if (lineLength >= 2 && terminalText[lineStart] == '$' && terminalText[lineStart + 1] == ' ')
            {
                var lineText = terminalText.Substring(lineStart, lineLength);
                foreach (var token in Highlight(lineText, "bash"))
                {
                    if (tokens.Count >= MaxTokens)
                    {
                        break;
                    }
                    tokens.Add(new CodeSyntaxToken(token.Start + lineStart, token.Length, token.Kind));
                }
            }

            if (newline < 0)
            {
                break;
            }
            lineStart = newline + 1;
        }

        return tokens;
    }

    private static void Add(List<CodeSyntaxToken> tokens, int start, int length, CodeSyntaxTokenKind kind)
    {
        if (length <= 0 || tokens.Count >= MaxTokens)
        {
            return;
        }
        tokens.Add(new CodeSyntaxToken(start, length, kind));
    }

    private static bool TryComment(string code, int index, LanguageFamily family, out int end)
    {
        end = index;
        var ch = code[index];
        var next = index + 1 < code.Length ? code[index + 1] : '\0';

        if (family is LanguageFamily.Shell or LanguageFamily.Python or LanguageFamily.Generic)
        {
            // 行首或空白后的 `#` 视为注释；`$#` 等参数展开不会被误判。
            if (ch == '#' && (index == 0 || char.IsWhiteSpace(code[index - 1]) || code[index - 1] == '\n'))
            {
                end = ScanToLineEnd(code, index);
                return end > index;
            }
        }

        if (family is LanguageFamily.CLike or LanguageFamily.Generic)
        {
            if (ch == '/' && next == '/')
            {
                end = ScanToLineEnd(code, index);
                return end > index;
            }
            if (ch == '/' && next == '*')
            {
                var close = code.IndexOf("*/", index + 2, StringComparison.Ordinal);
                end = close >= 0 ? close + 2 : code.Length;
                return end > index;
            }
        }

        if (family == LanguageFamily.Sql && ch == '-' && next == '-')
        {
            end = ScanToLineEnd(code, index);
            return end > index;
        }

        return false;
    }

    private static int ScanToLineEnd(string code, int start)
    {
        var end = code.IndexOf('\n', start);
        return end >= 0 ? end : code.Length;
    }

    private static int ScanString(string code, int start, char quote, bool allowTriple)
    {
        var index = start + 1;
        var triple = allowTriple
            && index + 1 < code.Length
            && code[index] == quote
            && code[index + 1] == quote;

        if (triple)
        {
            index += 2;
        }

        while (index < code.Length)
        {
            var ch = code[index];
            if (ch == '\\')
            {
                // 跳过转义字符，字符串结束判断不会停在 `\"` 上。
                index += 2;
                continue;
            }

            if (triple)
            {
                if (ch == quote
                    && index + 2 < code.Length
                    && code[index + 1] == quote
                    && code[index + 2] == quote)
                {
                    return index + 3;
                }
                index++;
                continue;
            }

            if (ch == quote)
            {
                return index + 1;
            }
            if (ch == '\n')
            {
                // 普通字符串不跨行；遇到换行立即收束，避免把后续代码整体吞掉。
                return index;
            }
            index++;
        }

        return code.Length;
    }

    private static bool TryPath(string code, int index, out int end)
    {
        end = index;
        var ch = code[index];
        var previousIsIdentifier = index > 0 && IsIdentifierPart(code[index - 1]);
        if (previousIsIdentifier)
        {
            return false;
        }

        var startsLikePath = ch switch
        {
            '/' => index + 1 < code.Length && code[index + 1] is not ('/' or '*') && !char.IsWhiteSpace(code[index + 1]),
            '~' => index + 1 < code.Length && code[index + 1] == '/',
            '.' => index + 1 < code.Length && (code[index + 1] == '/' || code[index + 1] == '.'),
            _ => IsWindowsDrivePath(code, index),
        };
        if (!startsLikePath)
        {
            return false;
        }

        end = index + 1;
        while (end < code.Length && IsPathPart(code[end]))
        {
            end++;
        }
        return end - index >= 2;
    }

    private static bool IsWindowsDrivePath(string code, int index) =>
        index + 2 < code.Length
        && char.IsLetter(code[index])
        && code[index + 1] == ':'
        && code[index + 2] is '/' or '\\';

    private static bool IsPathPart(char ch) =>
        char.IsLetterOrDigit(ch)
        || ch is '/' or '\\' or '.' or '_' or '-' or '~' or '@' or '+';

    private static int ScanNumber(string code, int start)
    {
        var index = start;
        if (code[index] == '0' && index + 1 < code.Length && code[index + 1] is 'x' or 'X')
        {
            index += 2;
            while (index < code.Length && Uri.IsHexDigit(code[index]))
            {
                index++;
            }
            return index;
        }

        while (index < code.Length && (char.IsDigit(code[index]) || code[index] == '_'))
        {
            index++;
        }
        if (index < code.Length && code[index] == '.'
            && index + 1 < code.Length && char.IsDigit(code[index + 1]))
        {
            index++;
            while (index < code.Length && (char.IsDigit(code[index]) || code[index] == '_'))
            {
                index++;
            }
        }
        if (index < code.Length && code[index] is 'e' or 'E')
        {
            var exponent = index + 1;
            if (exponent < code.Length && code[exponent] is '+' or '-')
            {
                exponent++;
            }
            if (exponent < code.Length && char.IsDigit(code[exponent]))
            {
                index = exponent + 1;
                while (index < code.Length && char.IsDigit(code[index]))
                {
                    index++;
                }
            }
        }
        return index;
    }

    private static bool IsIdentifierStart(char ch) => char.IsLetter(ch) || ch == '_';

    private static bool IsIdentifierPart(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

    private static LanguageFamily ResolveFamily(string? language)
    {
        var normalized = language?.Trim().TrimStart('.').ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "sh" or "shell" or "bash" or "zsh" or "fish" or "console" or "shellscript" => LanguageFamily.Shell,
            "py" or "python" or "python3" => LanguageFamily.Python,
            "sql" or "sqlite" => LanguageFamily.Sql,
            "cs" or "csharp" or "c#" or "java" or "kotlin" or "kt" or "js" or "javascript"
                or "ts" or "typescript" or "c" or "cpp" or "c++" or "go" or "rust" or "swift"
                or "json" or "xml" or "html" or "css" => LanguageFamily.CLike,
            _ => LanguageFamily.Generic,
        };
    }

    private enum LanguageFamily
    {
        /// <summary>通用 / 纯文本输出。</summary>
        Generic,

        /// <summary>C#、Java、JS、Rust 等 C 系语言。</summary>
        CLike,

        /// <summary>Shell / Bash 系。</summary>
        Shell,

        /// <summary>Python 系。</summary>
        Python,

        /// <summary>SQL 系。</summary>
        Sql,
    }
}
