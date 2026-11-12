using Android.Content;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using DshMobile.Application.Terminal;
using Java.Lang;

namespace DshMobile.App.Terminal;

/// <summary>
/// Keyboard input of the C# terminal. xterm.js's own hidden-textarea input drops and duplicates characters with
/// Android IMEs (first device run 2026-11-12: "stty size" arrived as "sttttty s ssizz"), so the WebView only displays
/// and this native view takes the keyboard instead — the approach of Termux's TerminalView: an InputConnection with
/// <c>TYPE_NULL</c> (IMEs send key events or plain commits, no autocorrect), composed text (e.g. pinyin) is sent when
/// committed, deletions become DEL bytes. Written for this repo (no Termux code copied).
/// </summary>
public sealed class TerminalInputView : View
{
    public TerminalInputView(Context context) : base(context)
    {
        Focusable = true;
        FocusableInTouchMode = true;
    }

    /// <summary>Typed text (letters, digits, CJK…): the activity applies the latched CTRL to it.</summary>
    public event Action<string>? Typed;

    /// <summary>A key with a fixed sequence (ENTER, BACKSPACE, arrows…), by <see cref="TerminalKeys"/> name.</summary>
    public event Action<string, bool>? Key;

    public override bool OnCheckIsTextEditor() => true;

    public override IInputConnection? OnCreateInputConnection(EditorInfo? outAttrs)
    {
        if (outAttrs is not null)
        {
            outAttrs.InputType = InputTypes.Null;
            outAttrs.ImeOptions = ImeFlags.NoFullscreen | ImeFlags.NoExtractUi;
        }
        return new Connection(this);
    }

    public void ShowKeyboard()
    {
        RequestFocus();
        var imm = (InputMethodManager?)Context!.GetSystemService(Context.InputMethodService);
        imm?.ShowSoftInput(this, ShowFlags.Implicit);
    }

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (e is null)
        {
            return base.OnKeyDown(keyCode, e);
        }
        if (NameOf(keyCode) is { } name)
        {
            Key?.Invoke(name, e.IsAltPressed);
            return true;
        }
        var unicode = e.GetUnicodeChar(e.MetaState & ~(MetaKeyStates.CtrlMask | MetaKeyStates.AltMask));
        if (unicode <= 0 || (unicode & KeyCharacterMap.CombiningAccent) != 0)
        {
            return base.OnKeyDown(keyCode, e); // BACK, volume, modifiers, dead keys …
        }
        var text = char.ConvertFromUtf32(unicode);
        if (e.IsCtrlPressed)
        {
            text = TerminalKeys.ApplyCtrl(text).Output;
        }
        Typed?.Invoke(e.IsAltPressed ? "\u001b" + text : text);
        return true;
    }

    private static string? NameOf(Keycode code) => code switch
    {
        Keycode.Enter or Keycode.NumpadEnter => "ENTER",
        Keycode.Del => "BACKSPACE",
        Keycode.ForwardDel => "DELETE",
        Keycode.Insert => "INSERT",
        Keycode.Tab => "TAB",
        Keycode.Escape => "ESC",
        Keycode.DpadUp => "UP",
        Keycode.DpadDown => "DOWN",
        Keycode.DpadLeft => "LEFT",
        Keycode.DpadRight => "RIGHT",
        Keycode.MoveHome => "HOME",
        Keycode.MoveEnd => "END",
        Keycode.PageUp => "PGUP",
        Keycode.PageDown => "PGDN",
        _ => null,
    };

    /// <summary>Full editor so composing IMEs work; whatever gets committed is sent and the buffer cleared.</summary>
    private sealed class Connection(TerminalInputView owner) : BaseInputConnection(owner, true)
    {
        public override bool CommitText(ICharSequence? text, int newCursorPosition)
        {
            base.CommitText(text, newCursorPosition);
            Flush();
            return true;
        }

        public override bool FinishComposingText()
        {
            base.FinishComposingText();
            Flush();
            return true;
        }

        public override bool DeleteSurroundingText(int beforeLength, int afterLength)
        {
            // Nothing is kept in the buffer, so a deletion is meant for the shell's line: one DEL per character.
            for (var i = 0; i < beforeLength; i++)
            {
                owner.Key?.Invoke("BACKSPACE", false);
            }
            return base.DeleteSurroundingText(beforeLength, afterLength);
        }

        private void Flush()
        {
            var editable = Editable;
            var text = editable?.ToString();
            if (!string.IsNullOrEmpty(text))
            {
                // Enter from some IMEs arrives as a committed "\n".
                owner.Typed?.Invoke(text.Replace("\n", "\r", StringComparison.Ordinal));
            }
            editable?.Clear();
        }
    }
}
