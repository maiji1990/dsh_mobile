using System.Text.Json;
using System.Text.Json.Serialization;
using DshMobile.Agent.Protocol;

namespace DshMobile.Agent.Engines;

/// <summary>本机隐藏（移出列表）会话集合的持久化记录，仅存于本地缓存目录，不触碰服务端 `.dsh/sessions`。</summary>
public sealed record HiddenSessionsRecord(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("hiddenSessionIds")] IReadOnlyList<string> HiddenSessionIds);

/// <summary>
/// 本机永久隐藏会话存储：记录用户在本应用“删除/移出列表”的会话 ID，避免服务端无物理删除 API 时反复重新出现。
/// 通过 <see cref="AgentProtocolJsonContext"/> 进行 SourceGen 序列化，采用临时文件 + 原子覆盖落盘，线程安全。
/// </summary>
public sealed class HiddenSessionStore
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultFileName = "hidden-sessions.json";

    private readonly string _filePath;
    private readonly Lock _memoryLock = new();
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly HashSet<string> _hiddenIds = new(StringComparer.Ordinal);
    private bool _loaded;

    /// <summary>
    /// 创建本机隐藏会话存储。
    /// 若传入路径以 <c>.json</c> 结尾则直接作为文件路径；否则视为缓存目录并在其下使用 <see cref="DefaultFileName"/>。
    /// </summary>
    public HiddenSessionStore(string cacheDirectoryOrFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectoryOrFilePath);
        _filePath = cacheDirectoryOrFilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? cacheDirectoryOrFilePath
            : Path.Combine(cacheDirectoryOrFilePath, DefaultFileName);
    }

    public string FilePath => _filePath;

    /// <summary>从磁盘加载已隐藏会话列表；文件不存在或内容损坏时安静容忍，不抛异常。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadCoreLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>将指定会话加入本机永久隐藏集合并原子落盘。</summary>
    public async Task<bool> HideAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }
        var normalizedId = sessionId.Trim();

        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_loaded)
            {
                await LoadCoreLockedAsync(ct).ConfigureAwait(false);
            }

            string[] snapshotToPersist;
            lock (_memoryLock)
            {
                if (!_hiddenIds.Add(normalizedId))
                {
                    return false;
                }
                snapshotToPersist = _hiddenIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            }

            await PersistSnapshotLockedAsync(snapshotToPersist, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>将指定会话从本机永久隐藏集合移除并原子落盘。</summary>
    public async Task<bool> UnhideAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }
        var normalizedId = sessionId.Trim();

        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_loaded)
            {
                await LoadCoreLockedAsync(ct).ConfigureAwait(false);
            }

            string[] snapshotToPersist;
            lock (_memoryLock)
            {
                if (!_hiddenIds.Remove(normalizedId))
                {
                    return false;
                }
                snapshotToPersist = _hiddenIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            }

            await PersistSnapshotLockedAsync(snapshotToPersist, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>返回当前已隐藏会话 ID 集合的线程安全只读快照。</summary>
    public IReadOnlySet<string> GetHiddenIdsSnapshot()
    {
        lock (_memoryLock)
        {
            return new HashSet<string>(_hiddenIds, StringComparer.Ordinal);
        }
    }

    /// <summary>检查指定会话是否处于本机隐藏集合中。</summary>
    public bool IsHidden(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }
        lock (_memoryLock)
        {
            return _hiddenIds.Contains(sessionId.Trim());
        }
    }

    private async Task LoadCoreLockedAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            _loaded = true;
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize(json, AgentProtocolJsonContext.Default.HiddenSessionsRecord);
            if (parsed is not null && parsed.SchemaVersion == CurrentSchemaVersion && parsed.HiddenSessionIds is not null)
            {
                lock (_memoryLock)
                {
                    foreach (var sessionId in parsed.HiddenSessionIds)
                    {
                        if (!string.IsNullOrWhiteSpace(sessionId))
                        {
                            _hiddenIds.Add(sessionId.Trim());
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 文件损坏或无法读取时安静降级为空集合，不阻断启动与列表展示。
            _ = ex;
        }
        finally
        {
            _loaded = true;
        }
    }

    private async Task PersistSnapshotLockedAsync(IReadOnlyList<string> hiddenIds, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var record = new HiddenSessionsRecord(CurrentSchemaVersion, hiddenIds);
        var json = JsonSerializer.Serialize(record, AgentProtocolJsonContext.Default.HiddenSessionsRecord);
        var tempFilePath = $"{_filePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var fileStream = new FileStream(tempFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            await using (var writer = new StreamWriter(fileStream))
            {
                await writer.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
                fileStream.Flush(flushToDisk: true);
            }
            File.Move(tempFilePath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                try
                {
                    File.Delete(tempFilePath);
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
}
