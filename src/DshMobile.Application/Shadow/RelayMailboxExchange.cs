using System.Text;
using System.Text.RegularExpressions;

namespace DshMobile.Application.Shadow;

/// <summary>One request a probe hands to the live Relay: channel, request file, exact text, and the response id.</summary>
public sealed record RelayProbeRequest(bool Control, string File, string Text, string ResponseId);

/// <summary>What came back: whether a mailbox existed at all, and each answered request's response text.</summary>
public sealed record RelayExchangeResult(bool MailboxPresent, string RunDir, IReadOnlyDictionary<RelayProbeRequest, string> Answers);

/// <summary>
/// Writes probe requests into the live Relay mailbox the way the guest CLI does (<c>.&lt;name&gt;.part</c> then
/// rename; Relay skips dot names), waits for the answers and cleans up after itself: only files named after the probe
/// requests are created or deleted, stale answers are removed first, unclaimed requests are taken back.
/// </summary>
public static partial class RelayMailboxExchange
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<RelayExchangeResult> RunAsync(string pilotEntryDir, IReadOnlyList<RelayProbeRequest> requests,
        TimeProvider time, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pilotEntryDir);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(time);
        var run = Path.Combine(pilotEntryDir, "run");
        var inbox = Path.Combine(run, "inbox");
        var outbox = Path.Combine(run, "outbox");
        var controlInbox = Path.Combine(run, "control-inbox");
        var controlOutbox = Path.Combine(run, "control-outbox");
        var answers = new Dictionary<RelayProbeRequest, string>();
        if (!Directory.Exists(inbox) || !Directory.Exists(outbox) || !Directory.Exists(controlInbox) || !Directory.Exists(controlOutbox))
        {
            return new RelayExchangeResult(false, run, answers);
        }

        string InboxOf(RelayProbeRequest r) => Path.Combine(r.Control ? controlInbox : inbox, r.File);
        string PartOf(RelayProbeRequest r) => Path.Combine(r.Control ? controlInbox : inbox, "." + r.File + ".part");
        string ResponseOf(RelayProbeRequest r) => Path.Combine(r.Control ? controlOutbox : outbox, r.ResponseId + ".json");

        try
        {
            foreach (var r in requests)
            {
                File.Delete(ResponseOf(r)); // a stale answer from an earlier probe must not count
            }
            foreach (var r in requests)
            {
                await File.WriteAllTextAsync(PartOf(r), r.Text, Utf8, ct).ConfigureAwait(false);
                File.Move(PartOf(r), InboxOf(r), overwrite: true);
            }
            var deadline = time.GetUtcNow() + timeout;
            while (answers.Count < requests.Count && time.GetUtcNow() < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), time, ct).ConfigureAwait(false);
                foreach (var r in requests.Where(r => !answers.ContainsKey(r)))
                {
                    if (TryRead(ResponseOf(r)) is { } text)
                    {
                        answers[r] = text;
                    }
                }
            }
        }
        finally
        {
            foreach (var r in requests)
            {
                TryDelete(InboxOf(r)); // still unclaimed: take it back
                TryDelete(PartOf(r));
                TryDelete(ResponseOf(r));
            }
        }
        return new RelayExchangeResult(true, run, answers);
    }

    /// <summary>Response timing is the only field that legitimately differs between two runs.</summary>
    public static string NormalizeElapsed(string responseText) => ElapsedPattern().Replace(responseText, "\"elapsedMs\":0", 1);

    [GeneratedRegex("\"elapsedMs\":-?[0-9]+")]
    private static partial Regex ElapsedPattern();

    /// <summary>A short, single-line excerpt for the report.</summary>
    public static string Excerpt(string s, int max = 160) => (s.Length <= max ? s : s[..max] + "…").ReplaceLineEndings(" ");

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Utf8) : null;
        }
        catch (IOException)
        {
            return null; // being renamed into place: next round
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort; never fails the probe
        }
    }
}
