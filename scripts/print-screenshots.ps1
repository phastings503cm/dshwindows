<#
.SYNOPSIS
    Prints downscaled JPEG copies of self-test screenshots into the job log as base64, between
    ===BEGIN name=== / ===END name=== markers, so a reviewer (or an agent) can see how the UI rendered
    on a real Windows machine without downloading the artifact.

    Runs under Windows PowerShell (System.Drawing).
#>
param(
    [Parameter(Mandatory)] [string] $Folder,
    [string[]] $Names = @('chat-light', 'code-dark', 'settings-general-light'),
    [int] $MaxWidth = 1100
)

Add-Type -AssemblyName System.Drawing
foreach ($name in $Names) {
    $path = Join-Path $Folder "$name.png"
    if (-not (Test-Path $path)) { Write-Host "(no $name.png)"; continue }
    $source = [System.Drawing.Image]::FromFile($path)
    try {
        $scale = [Math]::Min(1.0, $MaxWidth / $source.Width)
        $width = [int]($source.Width * $scale)
        $height = [int]($source.Height * $scale)
        $bitmap = New-Object System.Drawing.Bitmap $width, $height
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($source, 0, 0, $width, $height)
        $graphics.Dispose()
        $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
        $parameters = New-Object System.Drawing.Imaging.EncoderParameters 1
        $parameters.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 72L
        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, $codec, $parameters)
        $bitmap.Dispose()
        $base64 = [Convert]::ToBase64String($stream.ToArray())
        Write-Host "===BEGIN $name.jpg ($width x $height)==="
        for ($i = 0; $i -lt $base64.Length; $i += 2000) {
            Write-Host $base64.Substring($i, [Math]::Min(2000, $base64.Length - $i))
        }
        Write-Host "===END $name.jpg==="
    }
    finally {
        $source.Dispose()
    }
}
