using System.Text;

namespace DshMobile.Agent.Engines;

/// <summary>由 <see cref="StreamingBlocks"/> 产出的不可变切片（N-03-02）。</summary>
public sealed record StreamingMarkdownBlock(
    int BlockIndex,
    int ChunkIndex,
    string Markdown,
    bool IsSettled,
    int ListDepth,
    bool ContinuedFromPrevious,
    bool ContinuesToNext);

/// <summary>
/// 带 16 行尾块硬上限的单遍增量 Markdown 分块器（N-03-02）。
/// 已闭合块（标题、列表项、空行段落、已闭合代码围栏）以及任意超过 16 行的
/// 未闭合块都会被封口为不可变的已定块，使实时尾块重解析与 Markdig 计算在
/// 6,000 词 / 400 行代码流下仍保持每 token O(1)。
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

    /// <summary>已封口块数量，用于增量登记时间线节点，避免每次遍历全部块。</summary>
    public int SettledCount => _settled.Count;

    /// <summary>当前未封口尾块的稳定键；没有尾块时返回 <c>null</c>。</summary>
    public (int BlockIndex, int ChunkIndex)? TailKey =>
        CurrentTailLineCount > 0 ? (_currentBlockIndex, _currentChunkIndex) : null;

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

    /// <summary>替换全文并确定性地重建所有块。</summary>
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

    /// <summary>以 O(增量长度) 的代价消费一段增量文本。</summary>
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

    /// <summary>在流结束时封口所有剩余尾块文本。</summary>
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
                // 未闭合围栏内触发 16 行硬切分：补一个合成的闭合围栏，保证 Markdig 输入有界且合法。
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

        // 若正在延续未闭合围栏块，为可独立渲染的已定块补上闭合围栏。
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
