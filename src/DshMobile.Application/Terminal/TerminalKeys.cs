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
            // Keyboard keys (soft / hardware) that are not on the bar, as Termux's KeyHandler sends them.
            "ENTER" => "\r",
            "BACKSPACE" => "\u007f",
            "DELETE" => "\u001b[3~",
            "INSERT" => "\u001b[2~",
            _ => null,
        };
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
