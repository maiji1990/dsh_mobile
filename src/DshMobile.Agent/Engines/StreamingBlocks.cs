using System.Text;

namespace DshMobile.Agent.Engines;

/// <summary>Immutable slice produced by <see cref="StreamingBlocks"/> (N-03-02).</summary>
public sealed record StreamingMarkdownBlock(
    int BlockIndex,
    int ChunkIndex,
    string Markdown,
    bool IsSettled,
    int ListDepth,
    bool ContinuedFromPrevious,
    bool ContinuesToNext);

/// <summary>
/// Incremental single-pass Markdown block splitter with a hard 16-line tail cap (N-03-02).
/// Closed blocks (headings, list items, blank-line paragraphs, closed code fences) and any
/// open block exceeding 16 lines are sealed into immutable settled chunks so live tail re-parsing
/// and Markdig work remain O(1) per token even on 6,000-word / 400-line code streams.
/// </summary>
public sealed class StreamingBlocks
{
    public const int MaxTailLines = 16;

    private readonly List<StreamingMarkdownBlock> _settled = [];
    private readonly List<string> _tailLines = [];
    private readonly StringBuilder _lineCarry = new();

    private int _currentBlockIndex;
    private int _currentChunkIndex;
    private bool _inFence;
    private char _fenceChar;
    private int _fenceLength;
    private string _fenceOpenerLine = string.Empty;
    private bool _tailContinuedFromPrevious;
    private int _tailListDepth;

    public IReadOnlyList<StreamingMarkdownBlock> SettledBlocks => _settled;

    public long TotalTailCharsReparsed { get; private set; }

    public long TailReevalCount { get; private set; }

    public int CurrentTailLineCount => _tailLines.Count + (_lineCarry.Length > 0 ? 1 : 0);

    public StreamingMarkdownBlock? TailBlock
    {
        get
        {
            var text = BuildCurrentTailMarkdown(includePartialCarry: true);
            if (text is null)
            {
                return null;
            }
            return new StreamingMarkdownBlock(
                _currentBlockIndex,
                _currentChunkIndex,
                text,
                IsSettled: false,
                ListDepth: _tailListDepth,
                ContinuedFromPrevious: _tailContinuedFromPrevious,
                ContinuesToNext: false);
        }
    }

    /// <summary>Replaces full text and rebuilds blocks deterministically.</summary>
    public void Reset(string text)
    {
        _settled.Clear();
        _tailLines.Clear();
        _lineCarry.Clear();
        _currentBlockIndex = 0;
        _currentChunkIndex = 0;
        _inFence = false;
        _fenceChar = '\0';
        _fenceLength = 0;
        _fenceOpenerLine = string.Empty;
        _tailContinuedFromPrevious = false;
        _tailListDepth = 0;
        Append(text);
    }

    /// <summary>Consumes an incremental text delta in O(delta length) time.</summary>
    public void Append(string? delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        for (var i = 0; i < delta.Length; i++)
        {
            var ch = delta[i];
            if (ch == '\r')
            {
                continue;
            }
            if (ch == '\n')
            {
                var line = _lineCarry.ToString();
                _lineCarry.Clear();
                ProcessCompletedLine(line);
            }
            else
            {
                _lineCarry.Append(ch);
            }
        }

        if (_lineCarry.Length > 0 || _tailLines.Count > 0)
        {
            RecordTailReparseCost();
        }
    }

    /// <summary>Seals any remaining tail text at stream completion.</summary>
    public void FlushFinal()
    {
        if (_lineCarry.Length > 0)
        {
            var lastLine = _lineCarry.ToString();
            _lineCarry.Clear();
            ProcessCompletedLine(lastLine);
        }

        if (_tailLines.Count > 0)
        {
            SealCurrentTail(continuesToNext: false, startNewBlock: true);
        }
    }

    public IReadOnlyList<StreamingMarkdownBlock> SnapshotAllBlocks()
    {
        if (TailBlock is { } tail)
        {
            var list = new List<StreamingMarkdownBlock>(_settled.Count + 1);
            list.AddRange(_settled);
            list.Add(tail);
            return list;
        }
        return _settled;
    }

    private void ProcessCompletedLine(string line)
    {
        if (_inFence)
        {
            _tailLines.Add(line);
            if (IsClosingFence(line, _fenceChar, _fenceLength))
            {
                _inFence = false;
                _fenceChar = '\0';
                _fenceLength = 0;
                _fenceOpenerLine = string.Empty;
                SealCurrentTail(continuesToNext: false, startNewBlock: true);
                return;
            }

            if (_tailLines.Count >= MaxTailLines)
            {
                // Hard 16-line split inside open fence: close synthetic chunk so Markdig stays bounded and valid.
                SealCurrentTail(continuesToNext: true, startNewBlock: false);
            }
            return;
        }

        if (TryParseOpeningFence(line, out var fChar, out var fLen))
        {
            if (_tailLines.Count > 0)
            {
                SealCurrentTail(continuesToNext: false, startNewBlock: true);
            }
            _inFence = true;
            _fenceChar = fChar;
            _fenceLength = fLen;
            _fenceOpenerLine = line;
            _tailListDepth = 0;
            _tailLines.Add(line);
            return;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            if (_tailLines.Count > 0)
            {
                SealCurrentTail(continuesToNext: false, startNewBlock: true);
            }
            return;
        }

        if (IsAtxHeading(line))
        {
            if (_tailLines.Count > 0)
            {
                SealCurrentTail(continuesToNext: false, startNewBlock: true);
            }
            _tailListDepth = 0;
            _tailLines.Add(line);
            SealCurrentTail(continuesToNext: false, startNewBlock: true);
            return;
        }

        if (TryGetListItemDepth(line, out var listDepth))
        {
            if (_tailLines.Count > 0)
            {
                SealCurrentTail(continuesToNext: false, startNewBlock: true);
            }
            _tailListDepth = listDepth;
            _tailLines.Add(line);
            if (_tailLines.Count >= MaxTailLines)
            {
                SealCurrentTail(continuesToNext: true, startNewBlock: false);
            }
            return;
        }

        _tailLines.Add(line);
        if (_tailLines.Count >= MaxTailLines)
        {
            SealCurrentTail(continuesToNext: true, startNewBlock: false);
        }
    }

    private void SealCurrentTail(bool continuesToNext, bool startNewBlock)
    {
        var text = BuildCurrentTailMarkdown(includePartialCarry: false);
        if (text is null)
        {
            _tailLines.Clear();
            return;
        }

        // If continuing an active fence chunk, ensure the sealed slice has a closing fence for standalone rendering.
        if (_inFence && continuesToNext && !IsClosingFence(_tailLines[^1], _fenceChar, _fenceLength))
        {
            text = text + "\n" + new string(_fenceChar, _fenceLength);
        }

        _settled.Add(new StreamingMarkdownBlock(
            _currentBlockIndex,
            _currentChunkIndex,
            text,
            IsSettled: true,
            ListDepth: _tailListDepth,
            ContinuedFromPrevious: _tailContinuedFromPrevious,
            ContinuesToNext: continuesToNext));

        _tailLines.Clear();
        if (startNewBlock)
        {
            _currentBlockIndex++;
            _currentChunkIndex = 0;
            _tailContinuedFromPrevious = false;
            _tailListDepth = 0;
        }
        else
        {
            _currentChunkIndex++;
            _tailContinuedFromPrevious = true;
            if (_inFence && !string.IsNullOrEmpty(_fenceOpenerLine))
            {
                _tailLines.Add(_fenceOpenerLine);
            }
        }
    }

    private string? BuildCurrentTailMarkdown(bool includePartialCarry)
    {
        var count = _tailLines.Count;
        var hasCarry = includePartialCarry && _lineCarry.Length > 0;
        if (count == 0 && !hasCarry)
        {
            return null;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }
            sb.Append(_tailLines[i]);
        }
        if (hasCarry)
        {
            if (count > 0)
            {
                sb.Append('\n');
            }
            sb.Append(_lineCarry);
        }
        return sb.ToString();
    }

    private void RecordTailReparseCost()
    {
        var len = 0;
        for (var i = 0; i < _tailLines.Count; i++)
        {
            len += _tailLines[i].Length + 1;
        }
        len += _lineCarry.Length;
        TailReevalCount++;
        TotalTailCharsReparsed += len;
    }

    private static bool IsAtxHeading(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('#'))
        {
            return false;
        }
        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#' && hashes <= 6)
        {
            hashes++;
        }
        return hashes is >= 1 and <= 6 && hashes < trimmed.Length && trimmed[hashes] == ' ';
    }

    private static bool TryParseOpeningFence(string line, out char fenceChar, out int fenceLength)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length >= 3 && (trimmed[0] == '`' || trimmed[0] == '~'))
        {
            var ch = trimmed[0];
            var count = 0;
            while (count < trimmed.Length && trimmed[count] == ch)
            {
                count++;
            }
            if (count >= 3 && (ch != '`' || !trimmed.AsSpan(count).Contains('`')))
            {
                fenceChar = ch;
                fenceLength = count;
                return true;
            }
        }
        fenceChar = '\0';
        fenceLength = 0;
        return false;
    }

    private static bool IsClosingFence(string line, char fenceChar, int minLength)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < minLength || trimmed[0] != fenceChar)
        {
            return false;
        }
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] != fenceChar)
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryGetListItemDepth(string line, out int depth)
    {
        var spaces = 0;
        while (spaces < line.Length && (line[spaces] == ' ' || line[spaces] == '\t'))
        {
            spaces += line[spaces] == '\t' ? 2 : 1;
        }
        var rest = line.AsSpan(Math.Min(spaces, line.Length));
        if (rest.Length >= 2 && (rest[0] is '-' or '*' or '+') && rest[1] == ' ')
        {
            depth = 1 + (spaces / 2);
            return true;
        }
        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
        {
            digits++;
        }
        if (digits > 0 && digits + 1 < rest.Length && (rest[digits] is '.' or ')') && rest[digits + 1] == ' ')
        {
            depth = 1 + (spaces / 2);
            return true;
        }
        depth = 0;
        return false;
    }
}
