using System.Text;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// Reads a file another component may still be appending to (the Relay's <c>logs/relay.log</c>) the way RelayLogs
/// itself reads it: <see cref="FileShare.ReadWrite"/>. <see cref="File.ReadAllText(string)"/> refuses to share with a
/// writer, so on Windows a poll that met an append threw "being used by another process" (CI run 43833151580).
/// </summary>
internal static class SharedRead
{
    public static string Text(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Every log file of a Relay logs directory, joined.</summary>
    public static string Logs(string logsDir) => string.Join("\n", Directory.GetFiles(logsDir).Select(Text));
}
