using System.Text;

namespace Dsh.Core.Tests.Terminal;

public class TerminalInputTests
{
    private static string Encode(TerminalKey key, TerminalModifiers modifiers = TerminalModifiers.None, bool app = false) =>
        Encoding.ASCII.GetString(TerminalInput.Encode(key, modifiers, app)!);

    [Fact]
    public void ArrowsUseCsiUnlessApplicationMode()
    {
        Assert.Equal("\u001B[A", Encode(TerminalKey.Up));
        Assert.Equal("\u001BOA", Encode(TerminalKey.Up, app: true));
    }

    [Fact]
    public void ModifiedArrowsCarryTheXtermModifier()
    {
        // Ctrl+Left is PSReadLine's "back one word".
        Assert.Equal("\u001B[1;5D", Encode(TerminalKey.Left, TerminalModifiers.Control));
        Assert.Equal("\u001B[1;2C", Encode(TerminalKey.Right, TerminalModifiers.Shift));
        // Modified keys never use the SS3 form, even in application mode.
        Assert.Equal("\u001B[1;3A", Encode(TerminalKey.Up, TerminalModifiers.Alt, app: true));
    }

    [Fact]
    public void EditingKeys()
    {
        Assert.Equal("\u007F", Encode(TerminalKey.Backspace));
        Assert.Equal("\b", Encode(TerminalKey.Backspace, TerminalModifiers.Control));
        Assert.Equal("\u001B[3~", Encode(TerminalKey.Delete));
        Assert.Equal("\u001B[3;5~", Encode(TerminalKey.Delete, TerminalModifiers.Control));
        Assert.Equal("\r", Encode(TerminalKey.Enter));
        Assert.Equal("\t", Encode(TerminalKey.Tab));
        Assert.Equal("\u001B[Z", Encode(TerminalKey.Tab, TerminalModifiers.Shift));
        Assert.Equal("\u001B", Encode(TerminalKey.Escape));
    }

    [Fact]
    public void FunctionKeys()
    {
        Assert.Equal("\u001BOP", Encode(TerminalKey.F1));
        Assert.Equal("\u001B[15~", Encode(TerminalKey.F5));
        Assert.Equal("\u001B[24~", Encode(TerminalKey.F12));
        Assert.Equal("\u001B[1;5P", Encode(TerminalKey.F1, TerminalModifiers.Control));
    }

    [Theory]
    [InlineData('c', 0x03)]
    [InlineData('C', 0x03)]
    [InlineData('a', 0x01)]
    [InlineData('z', 0x1A)]
    [InlineData(' ', 0x00)]
    [InlineData('[', 0x1B)]
    [InlineData('\\', 0x1C)]
    [InlineData('_', 0x1F)]
    public void ControlCodes(char typed, int expected) => Assert.Equal((byte)expected, TerminalInput.ControlCode(typed));

    [Fact]
    public void ControlCodeIsNullForUnmappedCharacters() => Assert.Null(TerminalInput.ControlCode('!'));

    [Fact]
    public void AltPrefixesEscape() =>
        Assert.Equal("\u001Bb", Encoding.UTF8.GetString(TerminalInput.Text("b", alt: true)));

    [Fact]
    public void PasteNormalizesNewlines()
    {
        Assert.Equal("one\rtwo\rthree", Encoding.UTF8.GetString(TerminalInput.Paste("one\r\ntwo\nthree", bracketed: false)));
    }

    [Fact]
    public void BracketedPasteWrapsAndCannotBeEscaped()
    {
        var pasted = Encoding.UTF8.GetString(TerminalInput.Paste("rm -rf x\u001B[201~\nwhoami", bracketed: true));
        Assert.Equal("\u001B[200~rm -rf x\rwhoami\u001B[201~", pasted);
    }
}
