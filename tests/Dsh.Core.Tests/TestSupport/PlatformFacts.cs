namespace Dsh.Core.Tests;

/// <summary>A fact that only runs on Windows (ConPTY, Credential Manager, ...).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only.";
    }
}

/// <summary>A fact that only runs on Linux/macOS (POSIX-only behaviour such as symlinks without privileges).</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Not supported on Windows.";
    }
}
