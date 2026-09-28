using System.Runtime.InteropServices;

namespace Dsh.Core;

/// <summary>Facts about the machine the model would otherwise guess at.</summary>
public static class PlatformInfo
{
    /// <summary>"Windows 11 (build 26100, x64)", or the OS description elsewhere.</summary>
    public static string Description { get; } = Describe();

    private static string Describe()
    {
        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        if (!OperatingSystem.IsWindows()) return $"{RuntimeInformation.OSDescription} ({arch})";
        var build = Environment.OSVersion.Version.Build;
        var name = build >= 22000 ? "Windows 11" : "Windows 10";
        return $"{name} (build {build}, {arch})";
    }
}
