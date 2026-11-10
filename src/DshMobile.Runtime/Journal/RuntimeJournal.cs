using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshMobile.Runtime.Journal;

/// <summary>One state transition, as persisted.</summary>
public sealed record RuntimeJournalEntry(
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("from")] RuntimeState From,
    [property: JsonPropertyName("to")] RuntimeState To,
    [property: JsonPropertyName("harness")] HarnessState Harness,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage = null,
    [property: JsonPropertyName("implementation")] string? Implementation = null);

/// <summary>Durable, append-only record of runtime transitions (plan section 5: "每次状态转移写 durable journal").</summary>
public interface IRuntimeJournal
{
    void Append(RuntimeJournalEntry entry);

    /// <summary>Last entry that could be read back, or null for a fresh install / unreadable journal.</summary>
    RuntimeJournalEntry? ReadLast();

    IReadOnlyList<RuntimeJournalEntry> ReadTail(int max);
}

public sealed class InMemoryRuntimeJournal : IRuntimeJournal
{
    private readonly List<RuntimeJournalEntry> _entries = [];
    private readonly Lock _gate = new();

    public void Append(RuntimeJournalEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    public RuntimeJournalEntry? ReadLast()
    {
        lock (_gate)
        {
            return _entries.Count == 0 ? null : _entries[^1];
        }
    }

    public IReadOnlyList<RuntimeJournalEntry> ReadTail(int max)
    {
        lock (_gate)
        {
            return _entries.Skip(Math.Max(0, _entries.Count - max)).ToList();
        }
    }
}

/// <summary>
/// JSON-lines journal with flush-to-disk per append. A torn last line (power loss mid-write) is skipped on read;
/// the file is compacted to the last <see cref="KeepLines"/> entries when it grows past <see cref="CompactAtBytes"/>.
/// </summary>
public sealed class FileRuntimeJournal : IRuntimeJournal
{
    public const int KeepLines = 500;
    public const long CompactAtBytes = 512 * 1024;

    private readonly string _path;
    private readonly Lock _gate = new();

    public FileRuntimeJournal(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
    }

    public void Append(RuntimeJournalEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, RuntimeJournalJsonContext.Default.RuntimeJournalEntry) + "\n";
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            if (new FileInfo(_path).Length > CompactAtBytes)
            {
                Compact();
            }
        }
    }

    public RuntimeJournalEntry? ReadLast() => ReadTail(1) is [var last] ? last : null;

    public IReadOnlyList<RuntimeJournalEntry> ReadTail(int max)
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return [];
            }
            var parsed = new List<RuntimeJournalEntry>();
            foreach (var line in File.ReadLines(_path))
            {
                if (TryParse(line) is { } e)
                {
                    parsed.Add(e);
                }
            }
            return parsed.Skip(Math.Max(0, parsed.Count - max)).ToList();
        }
    }

    private static RuntimeJournalEntry? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(line, RuntimeJournalJsonContext.Default.RuntimeJournalEntry);
        }
        catch (JsonException)
        {
            return null; // torn or foreign line
        }
    }

    private void Compact()
    {
        var keep = File.ReadLines(_path).Where(l => TryParse(l) is not null).TakeLast(KeepLines).ToList();
        var tmp = _path + ".tmp";
        File.WriteAllLines(tmp, keep);
        File.Move(tmp, _path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(RuntimeJournalEntry))]
internal sealed partial class RuntimeJournalJsonContext : JsonSerializerContext;
