<#
.SYNOPSIS
    Prints the path of the Inno Setup compiler (ISCC.exe), or nothing when none is installed.

.DESCRIPTION
    Looks on PATH first, then in every "Inno Setup *" folder under Program Files, Program Files
    (x86) and %LOCALAPPDATA%\Programs, so Inno Setup 6 and 7 (32- or 64-bit edition) are all found.
    The newest compiler wins. Works in Windows PowerShell 5.1 and PowerShell 7.
#>
$command = Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if ($command) { return $command.Source }

$roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)})
if ($env:LOCALAPPDATA) { $roots += Join-Path $env:LOCALAPPDATA 'Programs' }

$roots | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique |
    ForEach-Object { Get-ChildItem -Path $_ -Directory -Filter 'Inno Setup*' -ErrorAction SilentlyContinue } |
    ForEach-Object { Join-Path $_.FullName 'ISCC.exe' } |
    Where-Object { Test-Path $_ } |
    Sort-Object -Descending -Property @(
        {
            $v = (Get-Item $_).VersionInfo
            '{0:D5}.{1:D5}.{2:D5}' -f $v.FileMajorPart, $v.FileMinorPart, $v.FileBuildPart
        },
        # Without version resources, "Inno Setup 7" still sorts above "Inno Setup 6".
        { Split-Path -Leaf (Split-Path -Parent $_) }
    ) |
    Select-Object -First 1
