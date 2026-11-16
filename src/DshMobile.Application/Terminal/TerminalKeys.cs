using System.Text;

namespace DshMobile.Application.Terminal;

/// <summary>
/// The extra-keys bar of the terminal (same keys as the legacy screen: ESC, TAB, arrows, HOME/END, PGUP/PGDN, ^C) as
/// the bytes a VT100/xterm terminal sends, and the latched CTRL modifier. Arrow / HOME / END keys follow the
/// emulator's DECCKM state (application cursor keys: <c>ESC O x</c>, otherwise <c>ESC [ x</c>), exactly like Termux's
/// KeyHandler that the legacy TerminalView uses.
/// </summary>
public static class TerminalKeys
{
    public static readonly IReadOnlyList<(string Label, string Key)> Bar =
    [
        ("ESC", "ESC"), ("TAB", "TAB"), ("↑", "UP"), ("↓", "DOWN"), ("←", "LEFT"), ("→", "RIGHT"),
        ("HOME", "HOME"), ("END", "END"), ("PGUP", "PGUP"), ("PGDN", "PGDN"), ("^C", "CTRL_C"),
    ];

    /// <summary>
    /// Every key the bar can show (vNext PR-08: the user picks and orders them). <see cref="Bar"/> is the default
    /// selection; the rest are control keys and the shell punctuation soft keyboards hide behind a long press.
    /// </summary>
    public static readonly IReadOnlyList<(string Label, string Key)> Catalog =
    [
        .. Bar,
        ("^D", "CTRL_D"), ("^Z", "CTRL_Z"), ("^L", "CTRL_L"), ("^R", "CTRL_R"),
        ("|", "TEXT:|"), ("/", "TEXT:/"), ("-", "TEXT:-"), ("~", "TEXT:~"), ("_", "TEXT:_"), ("*", "TEXT:*"),
    ];

    /// <summary>
    /// The saved bar layout (comma-separated key names, in order) → keys to show. Unknown or repeated names are
    /// dropped; an empty / missing / fully invalid layout is the default <see cref="Bar"/>.
    /// </summary>
    public static IReadOnlyList<(string Label, string Key)> ParseBar(string? saved)
    {
        if (string.IsNullOrWhiteSpace(saved))
        {
            return Bar;
        }
        var byKey = Catalog.ToDictionary(k => k.Key, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string, string)>();
        foreach (var name in saved.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (byKey.TryGetValue(name, out var key) && seen.Add(name))
            {
                result.Add(key);
            }
        }
        return result.Count == 0 ? Bar : result;
    }

    /// <summary>The layout string <see cref="ParseBar"/> reads back.</summary>
    public static string FormatBar(IEnumerable<string> keys) => string.Join(',', keys);

    /// <summary>Bytes (as a string of code units &lt; 0x80) for one bar key; null for an unknown key.</summary>
    public static string? Sequence(string key, bool applicationCursor)
    {
        string Cursor(char c) => (applicationCursor ? "\u001bO" : "\u001b[") + c;
        return key switch
        {
            "ESC" => "\u001b",
            "TAB" => "\t",
            "UP" => Cursor('A'),
            "DOWN" => Cursor('B'),
            "RIGHT" => Cursor('C'),
            "LEFT" => Cursor('D'),
            "HOME" => Cursor('H'),
            "END" => Cursor('F'),
            "PGUP" => "\u001b[5~",
            "PGDN" => "\u001b[6~",
            "CTRL_C" => "\u0003",
            "CTRL_D" => "\u0004",
            "CTRL_Z" => "\u001a",
            "CTRL_L" => "\u000c",
            "CTRL_R" => "\u0012",
            ['T', 'E', 'X', 'T', ':', var c] => c.ToString(),
            // Keyboard keys (soft / hardware) that are not on the bar, as Termux's KeyHandler sends them.
            "ENTER" => "\r",
            "BACKSPACE" => "\u007f",
            "DELETE" => "\u001b[3~",
            "INSERT" => "\u001b[2~",
            _ => null,
        };
    }

    /// <summary>
    /// Latched ALT (vNext PR-08): ESC before the next typed text (Meta sends ESC, as xterm / readline expect), then the
    /// latch releases. Empty text keeps the latch.
    /// </summary>
    public static (string Output, bool Released) ApplyAlt(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);
        return typed.Length == 0 ? (typed, false) : ("\u001b" + typed, true);
    }

    /// <summary>
    /// Latched CTRL, as in the legacy terminal (TerminalController.onCodePoint): the next typed <b>letter</b> becomes
    /// ^A..^Z and releases the latch; any other character is typed unchanged and the latch stays for the next one.
    /// </summary>
    public static (string Output, bool Released) ApplyCtrl(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);
        var sb = new StringBuilder(typed.Length);
        var released = false;
        foreach (var ch in typed)
        {
            var lower = char.ToLowerInvariant(ch);
            if (!released && lower is >= 'a' and <= 'z')
            {
                sb.Append((char)(lower - 'a' + 1));
                released = true;
            }
            else
            {
                sb.Append(ch);
            }
        }
        return (sb.ToString(), released);
    }
}
