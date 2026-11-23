namespace DshMobile.Runtime.Migration;

/// <summary>旧版 Web Profile 中只读发现的单条会话记录（不含任何敏感内容，N-05-05）。</summary>
public sealed record WebProfileSessionEntry(
    string SessionId,
    long LastModifiedUnixMs,
    bool IsKnownToRemote);

/// <summary>旧 Web Profile（user-data/.dsh）只读盘点报告，绝不读取凭据内容、绝不修改原目录（N-05-05）。</summary>
public sealed record WebProfileInventoryReport(
    bool HasUserDshDirectory,
    bool HasCredentialsFile,
    IReadOnlyList<WebProfileSessionEntry> DiscoveredSessions,
    int RecoverableCount,
    int UnrecognizedOrLegacyCount);

/// <summary>
/// 升级首启时只读扫描 <c>user-data/.dsh</c>（N-05-05）：
/// 对照官方 <c>session/list</c> 区分可直接恢复会话与需迁移/不支持的旧记录，严禁读取凭据或写删原始目录。
/// </summary>
public static class WebProfileInventory
{
    public static WebProfileInventoryReport InspectReadOnly(
        string userDataDir,
        IReadOnlySet<string>? knownRemoteSessionIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDir);

        var dshDir = Path.Combine(userDataDir, ".dsh");
        if (!Directory.Exists(dshDir))
        {
            return new WebProfileInventoryReport(
                HasUserDshDirectory: false,
                HasCredentialsFile: false,
                DiscoveredSessions: [],
                RecoverableCount: 0,
                UnrecognizedOrLegacyCount: 0);
        }

        // 只检查凭据文件是否存在，严禁打开或解析其密钥内容（N-05-05 安全约束）。
        var hasCredentialsFile = File.Exists(Path.Combine(dshDir, ".credentials.yaml"));
        var discovered = new List<WebProfileSessionEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        ScanSessionDirectory(Path.Combine(dshDir, "sessions"), knownRemoteSessionIds, discovered, seen);
        ScanProjectsSessionsDirectory(Path.Combine(dshDir, "projects"), knownRemoteSessionIds, discovered, seen);

        var recoverable = 0;
        var legacy = 0;
        foreach (var entry in discovered)
        {
            if (entry.IsKnownToRemote)
            {
                recoverable++;
            }
            else
            {
                legacy++;
            }
        }

        return new WebProfileInventoryReport(
            HasUserDshDirectory: true,
            HasCredentialsFile: hasCredentialsFile,
            DiscoveredSessions: discovered
                .OrderByDescending(e => e.LastModifiedUnixMs)
                .ToArray(),
            RecoverableCount: recoverable,
            UnrecognizedOrLegacyCount: legacy);
    }

    private static void ScanSessionDirectory(
        string dirPath,
        IReadOnlySet<string>? knownRemoteIds,
        List<WebProfileSessionEntry> output,
        HashSet<string> seen)
    {
        if (!Directory.Exists(dirPath))
        {
            return;
        }
        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(dirPath))
            {
                var id = Path.GetFileName(subDir);
                if (string.IsNullOrWhiteSpace(id) || id.StartsWith('.'))
                {
                    continue;
                }
                if (!seen.Add(id))
                {
                    continue;
                }
                var mtime = new DateTimeOffset(Directory.GetLastWriteTimeUtc(subDir), TimeSpan.Zero).ToUnixTimeMilliseconds();
                var isKnown = knownRemoteIds?.Contains(id) ?? false;
                output.Add(new WebProfileSessionEntry(id, mtime, isKnown));
            }
            foreach (var file in Directory.EnumerateFiles(dirPath, "*.jsonl"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(id) || id.StartsWith('.'))
                {
                    continue;
                }
                if (!seen.Add(id))
                {
                    continue;
                }
                var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero).ToUnixTimeMilliseconds();
                var isKnown = knownRemoteIds?.Contains(id) ?? false;
                output.Add(new WebProfileSessionEntry(id, mtime, isKnown));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 只读探测遇到瞬态权限/IO异常时安全忽略，绝不改动目录。
        }
    }

    private static void ScanProjectsSessionsDirectory(
        string projectsDir,
        IReadOnlySet<string>? knownRemoteIds,
        List<WebProfileSessionEntry> output,
        HashSet<string> seen)
    {
        if (!Directory.Exists(projectsDir))
        {
            return;
        }
        try
        {
            foreach (var projectSub in Directory.EnumerateDirectories(projectsDir))
            {
                ScanSessionDirectory(Path.Combine(projectSub, "sessions"), knownRemoteIds, output, seen);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 只读探测失败时不抛异常、不修改任何文件。
        }
    }
}
