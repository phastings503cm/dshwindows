using System.Text;

namespace Dsh.Core;

/// <summary>How a text file was stored, so an edit can write it back the same way: Windows
/// projects are full of CRLF files and BOMs, and silently converting either turns a one-line edit
/// into a whole-file diff.</summary>
public readonly record struct TextFileFormat(bool HasBom, bool UsesCrlf)
{
    public static TextFileFormat Default => new(false, false);
}

public static class FileText
{
    /// <summary>A fixed set of locks that paths are spread over (by hash), so the number of locks does not grow with the number of
    /// files an agent touches in a long session. Two files that share one merely take turns.</summary>
    private static readonly object[] Stripes = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();

    /// <summary>The object to <c>lock</c> while reading a file to change it and writing it back, so two agents editing one
    /// file (subagents run side by side) can't both start from the same text and lose one of the edits. (Re-entrant: a
    /// caller that holds it may call <see cref="Write"/>, which takes it again.)</summary>
    public static object LockFor(string path)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return Stripes[(uint)comparer.GetHashCode(Path.GetFullPath(path)) % (uint)Stripes.Length];
    }

    /// <summary>Read a file as UTF-8 text. Null when it can't be read or isn't valid UTF-8 (binary).
    /// The returned text has any BOM removed and keeps its original line endings.</summary>
    public static (string Text, TextFileFormat Format)? Read(string path)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path)) return null;
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return Decode(bytes);
    }

    public static (string Text, TextFileFormat Format)? Decode(byte[] bytes)
    {
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var body = bom ? bytes.AsSpan(3) : bytes.AsSpan();
        if (body.IndexOf((byte)0) >= 0) return null; // binary
        string text;
        try
        {
            text = TextUtil.Utf8Strict.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        return (text, new TextFileFormat(bom, text.Contains("\r\n", StringComparison.Ordinal)));
    }

    /// <summary>Write <paramref name="text"/> in <paramref name="format"/>: LF line breaks are
    /// converted to CRLF for a CRLF file, and the BOM is kept when there was one. Atomic when the
    /// file already exists.</summary>
    public static void Write(string path, string text, TextFileFormat format)
    {
        if (format.UsesCrlf) text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var encoding = format.HasBom ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true) : TextUtil.Utf8NoBom;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // One temp name per path, reused by the next write if this one dies half-way (a file named at random would be left
        // behind for good): writers of one path take turns under LockFor.
        var temp = path + ".dsh-tmp";
        lock (LockFor(path))
        {
            try
            {
                File.WriteAllText(temp, text, encoding);
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                // (A disk that fills up part-way through the write leaves half a file under this name: nothing else will ever use it.)
                try { File.Delete(temp); } catch { /* best effort */ }
                throw;
            }
        }
    }
}
