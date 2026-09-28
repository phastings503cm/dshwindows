namespace Dsh.Core.Tests;

/// <summary>A unique scratch folder, deleted when disposed.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory(string prefix = "dsh-test")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    /// <summary>Absolute path of <paramref name="relative"/> inside this folder.</summary>
    public string this[string relative] => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>Write a file (creating parent folders) and return its full path.</summary>
    public string Write(string relative, string contents)
    {
        var full = this[relative];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}
