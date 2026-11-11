// Ported from DSHBox v1.5.1 (7c9d33c): common/.../LogRedactor.kt, sandbox-manager/.../DshWriterLockSweeper.kt and
// DefaultSandboxManager.ingestDshWebLaunchToken (GPL-3.0-or-later). Parity: tests/TestVectors/log-redactor.json.

using System.Text.RegularExpressions;

namespace DshMobile.Runtime.Hosting;

/// <summary>Central redaction: every guest / DSH output line passes through here before it is logged.</summary>
public static partial class LogRedactor
{
    [GeneratedRegex(@"(?i)(api[_-]?key\s*[:=]\s*)[A-Za-z0-9_\-]+")]
    private static partial Regex ApiKey();

    [GeneratedRegex(@"(?i)(authorization\s*[:=]\s*)Bearer\s+[A-Za-z0-9_\-.]+")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?i)(cookie\s*[:=]\s*)[^\s;]+")]
    private static partial Regex Cookie();

    [GeneratedRegex(@"(?i)(password\s*[:=]\s*)\S+")]
    private static partial Regex Password();

    [GeneratedRegex(@"(?i)(token\s*[:=]\s*)\S+")]
    private static partial Regex Token();

    [GeneratedRegex(@"sk-[A-Za-z0-9_\-]+")]
    private static partial Regex SecretKey();

    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = ApiKey().Replace(text, "$1***");
        text = Bearer().Replace(text, "$1***");
        text = Cookie().Replace(text, "$1***");
        text = Password().Replace(text, "$1***");
        text = Token().Replace(text, "$1***");
        return SecretKey().Replace(text, "sk-***");
    }
}

/// <summary>DSH prints <c>dsh web: http://…?token=…</c> once listening; the token is read from the RAW line.</summary>
public static class DshLaunchToken
{
    private const string Marker = "dsh web: http";

    public static string? TryParse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var idx = line.IndexOf(Marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }
        var at = line.IndexOf("token=", idx, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }
        var start = at + "token=".Length;
        var end = line.IndexOf('&', start);
        var token = (end < 0 ? line[start..] : line[start..end]).Trim();
        return token.Length == 0 ? null : token;
    }
}

/// <summary>
/// Removes DSH writer locks (<c>*.lock</c> under <c>user-data/.dsh</c>, depth ≤ 6) whose recorded pid is dead. DSH's
/// atomic writes wait only 2 s and never clear foreign locks, so one forced kill would otherwise break every later
/// start. Skipped entirely when /proc liveness cannot be checked (better keep locks than delete live ones).
/// </summary>
public static class DshWriterLockSweeper
{
    private const int MaxDepth = 6;
    private const int PidReadBytes = 32;
    private const int PidMaxDigits = 10;

    public sealed record Result(int Scanned, IReadOnlyList<string> Removed, bool Skipped);

    public static Result Sweep(string workspaceDir, Func<long, bool> isProcessAlive, Func<bool> selfCheck)
    {
        ArgumentNullException.ThrowIfNull(isProcessAlive);
        ArgumentNullException.ThrowIfNull(selfCheck);
        var root = Path.Combine(workspaceDir, ".dsh");
        if (!Directory.Exists(root))
        {
            return new Result(0, [], false);
        }
        if (!selfCheck())
        {
            return new Result(0, [], true);
        }
        var locks = new List<string>();
        Collect(root, 0, locks);
        var removed = new List<string>();
        foreach (var lockFile in locks)
        {
            if (ReadPid(lockFile) is not { } pid || isProcessAlive(pid))
            {
                continue;
            }
            try
            {
                File.Delete(lockFile);
                removed.Add(Path.GetRelativePath(workspaceDir, lockFile).Replace(Path.DirectorySeparatorChar, '/'));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return new Result(locks.Count, removed, false);
    }

    /// <summary>Real /proc liveness (Android / Linux).</summary>
    public static Result SweepOnThisDevice(string workspaceDir) =>
        Sweep(workspaceDir, pid => Directory.Exists($"/proc/{pid}"), () => Directory.Exists($"/proc/{Environment.ProcessId}"));

    private static void Collect(string dir, int depth, List<string> into)
    {
        if (depth > MaxDepth)
        {
            return;
        }
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(dir).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var child in children)
        {
            if (Directory.Exists(child))
            {
                Collect(child, depth + 1, into);
            }
            else if (child.EndsWith(".lock", StringComparison.Ordinal))
            {
                into.Add(child);
            }
        }
    }

    private static long? ReadPid(string lockFile)
    {
        string text;
        try
        {
            using var fs = File.OpenRead(lockFile);
            var buffer = new byte[PidReadBytes];
            var n = fs.Read(buffer);
            text = n <= 0 ? "" : System.Text.Encoding.ASCII.GetString(buffer, 0, n);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var t = text.Trim();
        if (t.Length == 0 || t.Length > PidMaxDigits || !t.All(char.IsAsciiDigit))
        {
            return null;
        }
        return long.TryParse(t, out var pid) && pid > 0 ? pid : null;
    }
}
