using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshMobile.Agent.Engines;

public enum KeptRowKind
{
    User,
    Markdown,
    Reasoning,
    ToolCall,
    Command,
    Notice,
}

public enum KeptTranscriptLoadStatus
{
    NotFound,
    Loaded,
    Corrupt,
}

public sealed record KeptTranscriptLoadResult(
    KeptTranscriptLoadStatus Status,
    KeptTranscriptRecord? Record = null);

/// <summary>写入磁盘的脱敏缓存行，已剥离密钥与审批令牌（N-03-05）。</summary>
public sealed record KeptChatRow(
    KeptRowKind Kind,
    string ItemId,
    long TurnSeq,
    string PrimaryText,
    string? SecondaryText = null,
    string? CodeOrToolName = null,
    bool IsError = false,
    long? StartedAtUnixMs = null,
    long? CompletedAtUnixMs = null);

public sealed record KeptTranscriptRecord(
    int SchemaVersion,
    SessionKey Scope,
    long CursorSeq,
    RunPhase Phase,
    long SavedAtUnixMs,
    IReadOnlyList<KeptChatRow> Rows,
    string? SessionTitle = null,
    Dictionary<long, long>? TurnDurationsMs = null);

public sealed record LoadedRecentSession(
    SessionKey Scope,
    string? SessionTitle,
    KeptTranscriptRecord Record);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(KeptTranscriptRecord))]
[JsonSerializable(typeof(KeptChatRow))]
internal sealed partial class KeptTranscriptJsonContext : JsonSerializerContext;

/// <summary>
/// 面向 `KeptTranscript` / `detachedTranscript` 缓存优先启动的原子私有文件快照存储（N-03-05）。
/// 落盘前所有持久化字符串字段都会经过 <see cref="SecretRedactor"/> 脱敏。
/// </summary>
public sealed class KeptTranscriptStore
{
    public const int CurrentSchemaVersion = 1;
    public const int DefaultMaxRetainedRows = 50;

    private readonly string _directory;
    private readonly int _maxRetainedRows;
    private readonly TimeProvider _time;

    public KeptTranscriptStore(
        string directory,
        int maxRetainedRows = DefaultMaxRetainedRows,
        TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetainedRows);
        _directory = directory;
        _maxRetainedRows = maxRetainedRows;
        _time = time ?? TimeProvider.System;
    }

    public async Task SaveAsync(ChatSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Directory.CreateDirectory(_directory);

        var startIdx = Math.Max(0, snapshot.Rows.Count - _maxRetainedRows);
        var keptRows = new List<KeptChatRow>(snapshot.Rows.Count - startIdx);
        for (var i = startIdx; i < snapshot.Rows.Count; i++)
        {
            if (TrySanitizeRow(snapshot.Rows[i]) is { } row)
            {
                keptRows.Add(row);
            }
        }

        var nowUnixMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var sanitizedTitle = string.IsNullOrWhiteSpace(snapshot.SessionTitle)
            ? null
            : SecretRedactor.RedactAndTruncate(snapshot.SessionTitle, 120);
        var turnDurations = snapshot.TurnDurationsMs is { Count: > 0 } durations
            ? new Dictionary<long, long>(durations)
            : null;
        var record = new KeptTranscriptRecord(
            CurrentSchemaVersion,
            snapshot.Session,
            snapshot.CursorSeq,
            snapshot.Phase,
            nowUnixMs,
            keptRows,
            sanitizedTitle,
            turnDurations);

        var json = JsonSerializer.Serialize(record, KeptTranscriptJsonContext.Default.KeptTranscriptRecord);
        await WriteFileAtomicallyAsync(FilePathFor(snapshot.Session), json, ct).ConfigureAwait(false);
    }

    /// <summary>冷启 0.2s 从已有 <c>*.kept.json</c> 快照恢复最近一次会话（N-05-04），损坏或不存在时安静返回 null。</summary>
    public async Task<LoadedRecentSession?> TryLoadMostRecentAsync(CancellationToken ct = default)
    {
        var list = await ListRecentAsync(maxCount: 1, ct).ConfigureAwait(false);
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>列出磁盘上按时间倒序排列的有效离线会话快照（只读回看用途），自动跳过损坏条目。</summary>
    public async Task<IReadOnlyList<LoadedRecentSession>> ListRecentAsync(int maxCount = 20, CancellationToken ct = default)
    {
        if (maxCount <= 0 || !Directory.Exists(_directory))
        {
            return [];
        }

        var loaded = new List<LoadedRecentSession>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.kept.json"))
        {
            ct.ThrowIfCancellationRequested();
            if (await TryReadRecordAsync(path, ct).ConfigureAwait(false) is { } parsed)
            {
                loaded.Add(new LoadedRecentSession(parsed.Scope, parsed.SessionTitle, parsed));
            }
        }

        return loaded
            .OrderByDescending(item => item.Record.SavedAtUnixMs)
            .Take(maxCount)
            .ToArray();
    }

    private static async Task<KeptTranscriptRecord?> TryReadRecordAsync(string path, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize(json, KeptTranscriptJsonContext.Default.KeptTranscriptRecord);
            if (parsed is null || parsed.SchemaVersion != CurrentSchemaVersion || string.IsNullOrWhiteSpace(parsed.Scope.SessionId))
            {
                return null;
            }
            return parsed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return null;
        }
    }

    private static async Task WriteFileAtomicallyAsync(string finalPath, string content, CancellationToken ct)
    {
        var tmpPath = $"{finalPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            await using (var writer = new StreamWriter(fs))
            {
                await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmpPath, finalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                try
                {
                    File.Delete(tmpPath);
                }
#pragma warning disable CA1031 // 尽力清理临时文件
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    _ = ex;
                }
            }
        }
    }

    public async Task<KeptTranscriptLoadResult> LoadDetailedAsync(SessionKey scope, CancellationToken ct = default)
    {
        var path = FilePathFor(scope);
        if (!File.Exists(path))
        {
            return new KeptTranscriptLoadResult(KeptTranscriptLoadStatus.NotFound);
        }
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize(json, KeptTranscriptJsonContext.Default.KeptTranscriptRecord);
            if (parsed is null || parsed.SchemaVersion != CurrentSchemaVersion || parsed.Scope != scope)
            {
                return new KeptTranscriptLoadResult(KeptTranscriptLoadStatus.Corrupt);
            }
            return new KeptTranscriptLoadResult(KeptTranscriptLoadStatus.Loaded, parsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return new KeptTranscriptLoadResult(KeptTranscriptLoadStatus.Corrupt);
        }
    }

    public async Task<KeptTranscriptRecord?> TryLoadAsync(SessionKey scope, CancellationToken ct = default)
    {
        var res = await LoadDetailedAsync(scope, ct).ConfigureAwait(false);
        return res.Status == KeptTranscriptLoadStatus.Loaded ? res.Record : null;
    }

    /// <summary>删除指定作用域会话对应的本地快照缓存文件；不存在时安全返回 false。</summary>
    public Task<bool> DeleteAsync(SessionKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var filePath = FilePathFor(key);
        if (!File.Exists(filePath))
        {
            return Task.FromResult(false);
        }

        try
        {
            File.Delete(filePath);
            return Task.FromResult(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return Task.FromResult(false);
        }
    }

    /// <summary>按会话 ID 安全删除目录下所有匹配的本地快照缓存文件，返回实际删除的文件数。</summary>
    public async Task<int> DeleteBySessionIdAsync(string sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionId) || !Directory.Exists(_directory))
        {
            return 0;
        }

        var normalizedId = sessionId.Trim();
        var deletedCount = 0;
        foreach (var filePath in Directory.EnumerateFiles(_directory, "*.kept.json"))
        {
            ct.ThrowIfCancellationRequested();
            var parsed = await TryReadRecordAsync(filePath, ct).ConfigureAwait(false);
            if (parsed is null || !string.Equals(parsed.Scope.SessionId, normalizedId, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    deletedCount++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex;
            }
        }

        return deletedCount;
    }

    public static IReadOnlyList<ChatRow> ToDetachedChatRows(KeptTranscriptRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var rows = new List<ChatRow>(record.Rows.Count);
        foreach (var r in record.Rows)
        {
            switch (r.Kind)
            {
                case KeptRowKind.User:
                    rows.Add(new UserMessageRow(r.ItemId, r.TurnSeq, r.ItemId, r.PrimaryText, 0, IsPending: false));
                    break;
                case KeptRowKind.Markdown:
                    rows.Add(new MarkdownBlockRow(r.ItemId, r.TurnSeq, r.ItemId, 0, 0, r.PrimaryText, true, 0, false, false));
                    break;
                case KeptRowKind.Reasoning:
                    rows.Add(new ReasoningBlockRow(
                        r.ItemId,
                        r.TurnSeq,
                        r.ItemId,
                        0,
                        r.PrimaryText,
                        true,
                        r.StartedAtUnixMs,
                        r.CompletedAtUnixMs));
                    break;
                case KeptRowKind.ToolCall:
                    rows.Add(new ToolCallRow(
                        r.ItemId,
                        r.TurnSeq,
                        r.ItemId,
                        r.CodeOrToolName ?? "tool",
                        r.SecondaryText ?? string.Empty,
                        r.IsError ? ToolPhase.Error : ToolPhase.Completed,
                        null,
                        r.IsError,
                        r.StartedAtUnixMs,
                        r.CompletedAtUnixMs));
                    break;
                case KeptRowKind.Command:
                    rows.Add(new CommandRow(r.ItemId, r.TurnSeq, r.ItemId, r.CodeOrToolName ?? "command", r.SecondaryText, !r.IsError, r.PrimaryText));
                    break;
                case KeptRowKind.Notice:
                    rows.Add(new NoticeRow(r.ItemId, r.TurnSeq, r.CodeOrToolName ?? "notice", r.PrimaryText, r.IsError));
                    break;
            }
        }
        return rows;
    }

    private string FilePathFor(SessionKey scope)
    {
        var raw = $"{scope.RuntimeId}__{scope.ProfileId}__{scope.SessionId}";
        var safe = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
        return Path.Combine(_directory, $"{safe[..24].ToLowerInvariant()}.kept.json");
    }

    private static KeptChatRow? TrySanitizeRow(ChatRow row) =>
        row switch
        {
            UserMessageRow u => new KeptChatRow(
                KeptRowKind.User,
                u.ItemId,
                u.TurnSeq,
                SecretRedactor.RedactAndTruncate(u.Text, 4000)),
            MarkdownBlockRow md => new KeptChatRow(
                KeptRowKind.Markdown,
                md.ItemId,
                md.TurnSeq,
                SecretRedactor.RedactAndTruncate(md.Markdown, 8000)),
            ReasoningBlockRow rs => new KeptChatRow(
                KeptRowKind.Reasoning,
                rs.ItemId,
                rs.TurnSeq,
                SecretRedactor.RedactAndTruncate(rs.Text, 4000),
                StartedAtUnixMs: rs.StartedAtUnixMs,
                CompletedAtUnixMs: rs.CompletedAtUnixMs),
            ToolCallRow tc => new KeptChatRow(
                KeptRowKind.ToolCall,
                tc.ItemId,
                tc.TurnSeq,
                SecretRedactor.RedactAndTruncate(tc.OutputSummary ?? string.Empty, 400),
                SecretRedactor.RedactAndTruncate(tc.ArgumentsJson, 200),
                SecretRedactor.RedactAndTruncate(tc.ToolName, 120),
                tc.IsError,
                tc.StartedAtUnixMs,
                tc.CompletedAtUnixMs),
            CommandRow cmd => new KeptChatRow(
                KeptRowKind.Command,
                cmd.ItemId,
                cmd.TurnSeq,
                SecretRedactor.RedactAndTruncate(cmd.ResultText ?? string.Empty, 400),
                cmd.Args is null ? null : SecretRedactor.RedactAndTruncate(cmd.Args, 400),
                SecretRedactor.RedactAndTruncate(cmd.Name, 120),
                cmd.Succeeded == false),
            NoticeRow n => new KeptChatRow(
                KeptRowKind.Notice,
                n.ItemId,
                n.TurnSeq,
                SecretRedactor.RedactAndTruncate(n.DiagnosticKey, 200),
                CodeOrToolName: SecretRedactor.RedactAndTruncate(n.Code, 120),
                IsError: n.IsError),
            _ => null,
        };
}
