using System.Text;

namespace Dsh.Core;

public enum TerminalKey
{
    Up, Down, Right, Left, Home, End, PageUp, PageDown, Insert, Delete,
    Backspace, Enter, Tab, Escape,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}

[Flags]
public enum TerminalModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Control = 4,
}

/// <summary>Turns key presses into the bytes an xterm-compatible terminal sends. ConPTY translates
/// these back into console key events for Windows programs, so PowerShell's PSReadLine sees
/// Ctrl+Left as Ctrl+Left and bash under Git Bash sees the escape sequence it expects.</summary>
public static class TerminalInput
{
    private const string Esc = "\u001B";

    /// <summary>Bytes for a special key, or null when the key has no terminal encoding.</summary>
    public static byte[]? Encode(TerminalKey key, TerminalModifiers modifiers = TerminalModifiers.None, bool applicationCursorKeys = false)
    {
        // xterm modifier parameter: 1 + shift + 2·alt + 4·ctrl.
        var mod = 1 + (modifiers.HasFlag(TerminalModifiers.Shift) ? 1 : 0)
                    + (modifiers.HasFlag(TerminalModifiers.Alt) ? 2 : 0)
                    + (modifiers.HasFlag(TerminalModifiers.Control) ? 4 : 0);

        string? Cursor(char final)
        {
            if (mod > 1) return $"{Esc}[1;{mod}{final}";
            return applicationCursorKeys ? $"{Esc}O{final}" : $"{Esc}[{final}";
        }

        string Tilde(int code) => mod > 1 ? $"{Esc}[{code};{mod}~" : $"{Esc}[{code}~";
        string Ss3(char final) => mod > 1 ? $"{Esc}[1;{mod}{final}" : $"{Esc}O{final}";

        var text = key switch
        {
            TerminalKey.Up => Cursor('A'),
            TerminalKey.Down => Cursor('B'),
            TerminalKey.Right => Cursor('C'),
            TerminalKey.Left => Cursor('D'),
            TerminalKey.Home => Cursor('H'),
            TerminalKey.End => Cursor('F'),
            TerminalKey.PageUp => Tilde(5),
            TerminalKey.PageDown => Tilde(6),
            TerminalKey.Insert => Tilde(2),
            TerminalKey.Delete => Tilde(3),
            // DEL for Backspace (what Windows Terminal sends); Ctrl+Backspace is ^H, the
            // "delete word" chord PSReadLine and readline both understand.
            TerminalKey.Backspace => modifiers.HasFlag(TerminalModifiers.Control) ? "\b"
                : modifiers.HasFlag(TerminalModifiers.Alt) ? Esc + "\u007F" : "\u007F",
            TerminalKey.Enter => modifiers.HasFlag(TerminalModifiers.Alt) ? Esc + "\r" : "\r",
            TerminalKey.Tab => modifiers.HasFlag(TerminalModifiers.Shift) ? $"{Esc}[Z" : "\t",
            TerminalKey.Escape => Esc,
            TerminalKey.F1 => Ss3('P'),
            TerminalKey.F2 => Ss3('Q'),
            TerminalKey.F3 => Ss3('R'),
            TerminalKey.F4 => Ss3('S'),
            TerminalKey.F5 => Tilde(15),
            TerminalKey.F6 => Tilde(17),
            TerminalKey.F7 => Tilde(18),
            TerminalKey.F8 => Tilde(19),
            TerminalKey.F9 => Tilde(20),
            TerminalKey.F10 => Tilde(21),
            TerminalKey.F11 => Tilde(23),
            TerminalKey.F12 => Tilde(24),
            _ => null,
        };
        return text is null ? null : Encoding.ASCII.GetBytes(text);
    }

    /// <summary>Ctrl+character → its control code (^A = 0x01 … ^Z = 0x1A, ^@ ^[ ^\ ^] ^^ ^_ ^?), or
    /// null when the combination has no control code.</summary>
    public static byte? ControlCode(char character) => character switch
    {
        >= 'a' and <= 'z' => (byte)(character - 'a' + 1),
        >= 'A' and <= 'Z' => (byte)(character - 'A' + 1),
        '@' or ' ' or '2' => 0x00,
        '[' or '3' => 0x1B,
        '\\' or '4' => 0x1C,
        ']' or '5' => 0x1D,
        '^' or '6' => 0x1E,
        '_' or '/' or '7' or '-' => 0x1F,
        '?' or '8' => 0x7F,
        _ => null,
    };

    /// <summary>Typed text as UTF-8; Alt sends the ESC-prefixed form readline uses for word motion.</summary>
    public static byte[] Text(string text, bool alt = false) =>
        Encoding.UTF8.GetBytes(alt ? Esc + text : text);

    /// <summary>Clipboard text ready to send: newlines become carriage returns (what Enter sends),
    /// and the paste is bracketed when the program asked for that, so a shell can tell a pasted
    /// command from a typed one and not run it line by line.</summary>
    public static byte[] Paste(string text, bool bracketed)
    {
        var normalized = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (bracketed)
        {
            // A pasted end marker would let the clipboard break out of the bracket.
            normalized = normalized.Replace($"{Esc}[201~", "");
            normalized = $"{Esc}[200~{normalized}{Esc}[201~";
        }
        return Encoding.UTF8.GetBytes(normalized);
    }
}
