<#
.SYNOPSIS
    Sets the primary display to the largest mode up to -Width x -Height (CI: before the UI self-test).

.DESCRIPTION
    A GitHub Windows runner's screen is 1024x768, and Windows keeps a window from growing past the
    screen, so the self-test's 1440x900 main window is captured at about 1024x768. A larger screen gives
    screenshots at the sizes the self-test asks for. If the display can't be changed the screenshots are
    just smaller, so this warns rather than fails.
#>
[CmdletBinding()]
param(
    [int] $Width = 1920,
    [int] $Height = 1080
)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class ScreenSize
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    private const int CurrentSettings = -1;
    private const int PelsWidth = 0x80000;
    private const int PelsHeight = 0x100000;
    private const int Test = 0x2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettings(ref DevMode devMode, int flags);

    private static DevMode Empty()
    {
        var mode = new DevMode();
        mode.dmSize = (short)Marshal.SizeOf(typeof(DevMode));
        return mode;
    }

    public static string Current()
    {
        var mode = Empty();
        return EnumDisplaySettings(null, CurrentSettings, ref mode) ? mode.dmPelsWidth + "x" + mode.dmPelsHeight : "unknown";
    }

    public static List<string> Modes()
    {
        var modes = new List<string>();
        var mode = Empty();
        for (var i = 0; EnumDisplaySettings(null, i, ref mode); i++)
        {
            var text = mode.dmPelsWidth + "x" + mode.dmPelsHeight;
            if (!modes.Contains(text)) modes.Add(text);
        }
        return modes;
    }

    /// <summary>0 when the display now runs at width x height, otherwise ChangeDisplaySettings's result.</summary>
    public static int Set(int width, int height)
    {
        var mode = Empty();
        if (!EnumDisplaySettings(null, CurrentSettings, ref mode)) return -100;
        mode.dmPelsWidth = width;
        mode.dmPelsHeight = height;
        mode.dmFields = PelsWidth | PelsHeight;
        var tested = ChangeDisplaySettings(ref mode, Test);
        if (tested != 0) return tested;
        return ChangeDisplaySettings(ref mode, 0);
    }
}
'@

try {
    Write-Host "Screen: $([ScreenSize]::Current())"
    $modes = [ScreenSize]::Modes() | ForEach-Object {
        $w, $h = $_.Split('x') | ForEach-Object { [int] $_ }
        [pscustomobject] @{ Width = $w; Height = $h }
    }
    $best = $modes | Where-Object { $_.Width -le $Width -and $_.Height -le $Height } |
        Sort-Object { $_.Width * $_.Height } -Descending | Select-Object -First 1
    if (-not $best) {
        Write-Warning "No display mode up to ${Width}x${Height}; modes: $(($modes | ForEach-Object { "$($_.Width)x$($_.Height)" }) -join ', ')."
        return
    }
    $result = [ScreenSize]::Set($best.Width, $best.Height)
    if ($result -ne 0) { Write-Warning "Couldn't set the screen to $($best.Width)x$($best.Height) (result $result)." }
    Write-Host "Screen: $([ScreenSize]::Current())"
} catch {
    Write-Warning "Couldn't change the screen size: $($_.Exception.Message)"
}
