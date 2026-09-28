<#
.SYNOPSIS
    Builds DSH for Windows release artifacts: a self-contained publish per architecture, a portable
    zip, an Inno Setup installer, and SHA256SUMS.txt.

.DESCRIPTION
    The release workflow runs exactly this script, so a local run produces the same files:

        ./scripts/package.ps1                      # x64 and arm64, version from git
        ./scripts/package.ps1 -Arch x64 -SkipInstaller

    The version is major.minor from Directory.Build.props (VersionPrefix) plus the number of commits
    on HEAD, so every push to the default branch gets a unique, increasing version.
#>
[CmdletBinding()]
param(
    [string] $Version = '',
    [ValidateSet('x64', 'arm64')]
    [string[]] $Arch = @('x64', 'arm64'),
    [string] $Output = 'artifacts',
    [switch] $SkipInstaller,
    [switch] $SkipReadyToRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root $Output
New-Item -ItemType Directory -Force -Path $out | Out-Null

if (-not $Version) {
    [xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
    $prefix = @($props.Project.PropertyGroup | ForEach-Object { $_.VersionPrefix } | Where-Object { $_ })[0]
    $parts = $prefix.Split('.')
    $count = (git -C $root rev-list --count HEAD).Trim()
    $Version = "$($parts[0]).$($parts[1]).$count"
}
Write-Host "Packaging DSH $Version for $($Arch -join ', ')"

$iscc = $null
if (-not $SkipInstaller) {
    $command = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($command) { $iscc = $command.Source }
    if (-not $iscc) {
        $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
            Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) was not found. Install it (choco install innosetup) or pass -SkipInstaller.' }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($a in $Arch) {
    $rid = "win-$a"
    # Publish into a folder named DSH so the portable zip unpacks to DSH\DSH.exe.
    $publish = Join-Path $out "publish\$rid\DSH"
    if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

    $readyToRun = if ($SkipReadyToRun) { 'false' } else { 'true' }
    dotnet publish (Join-Path $root 'src\Dsh.App\Dsh.App.csproj') -c Release -r $rid --self-contained true `
        "-p:Version=$Version" "-p:PublishReadyToRun=$readyToRun" -p:DebugType=none -p:DebugSymbols=false -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

    $zip = Join-Path $out "DSH-$Version-$rid-portable.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    Write-Host "  $zip"

    if (-not $SkipInstaller) {
        & $iscc /Q "/DAppVersion=$Version" "/DArch=$a" "/DSourceDir=$publish" "/DOutputDir=$out" (Join-Path $root 'installer\DSH.iss')
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for $rid" }
        Write-Host "  $(Join-Path $out "DSH-$Version-$rid-setup.exe")"
    }
}

$sums = Get-ChildItem $out -File | Where-Object { $_.Name -like "DSH-$Version-*" -and $_.Extension -in '.zip', '.exe' } | Sort-Object Name |
    ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
Set-Content -Path (Join-Path $out 'SHA256SUMS.txt') -Value $sums -Encoding ascii
Write-Host "  $(Join-Path $out 'SHA256SUMS.txt')"

if ($env:GITHUB_OUTPUT) { "version=$Version" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
