#Requires -Version 5.1
<#
.SYNOPSIS
    Renders the branding the Windows installer wears: the WixUI banner and
    dialog bitmaps, the application icon, and an RTF rendition of LICENSE.
    Nothing here is checked in, because committed binaries would imply someone
    drew them on purpose.

.PARAMETER OutputDirectory
    Where banner.bmp, dialog.bmp, slaw.ico and License.rtf are written.

.EXAMPLE
    ./installer/New-InstallerAssets.ps1 -OutputDirectory out/installer-assets
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$LicensePath = (Join-Path $PSScriptRoot '..\LICENSE')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

# Studio palette (see src/Slaw/Desktop/StudioTheme.cs)
$GradientTop = [System.Drawing.ColorTranslator]::FromHtml('#a21caf')
$GradientBottom = [System.Drawing.ColorTranslator]::FromHtml('#6d28d9')

# A waveform that has never been mastered
$BarHeights = @(0.30, 0.55, 0.85, 0.50, 1.00, 0.70, 0.40, 0.90, 0.60, 0.35, 0.75, 0.45, 0.95, 0.55, 0.30)

function New-Canvas([int]$Width, [int]$Height, [System.Drawing.Imaging.PixelFormat]$Format) {
    $bitmap = New-Object System.Drawing.Bitmap($Width, $Height, $Format)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    return @{ Bitmap = $bitmap; Graphics = $graphics }
}

function New-RoundedRectanglePath([float]$X, [float]$Y, [float]$Width, [float]$Height, [float]$Radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-AccentBrush([float]$X, [float]$Y, [float]$Width, [float]$Height, [float]$Angle) {
    $bounds = New-Object System.Drawing.RectangleF($X, $Y, $Width, $Height)
    return New-Object System.Drawing.Drawing2D.LinearGradientBrush($bounds, $GradientTop, $GradientBottom, $Angle)
}

# Draws vertically centred bars across a band of the canvas
function Add-Waveform($Graphics, [float]$Left, [float]$CentreY, [float]$Width, [float]$MaxHeight, [float[]]$Heights, [int]$Alpha) {
    $slot = $Width / $Heights.Count
    $barWidth = [Math]::Max(1.5, $slot * 0.55)
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb($Alpha, 255, 255, 255))
    for ($index = 0; $index -lt $Heights.Count; $index++) {
        $barHeight = [Math]::Max($barWidth, $Heights[$index] * $MaxHeight)
        $x = $Left + $index * $slot + ($slot - $barWidth) / 2
        $bar = New-RoundedRectanglePath $x ($CentreY - $barHeight / 2) $barWidth $barHeight ($barWidth / 2)
        $Graphics.FillPath($brush, $bar)
        $bar.Dispose()
    }
    $brush.Dispose()
}

function Save-Bitmap($Canvas, [string]$FileName) {
    $Canvas.Graphics.Dispose()
    $Canvas.Bitmap.Save((Join-Path $OutputDirectory $FileName), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $Canvas.Bitmap.Dispose()
}

# The app mark: a rounded gradient tile carrying a waveform
function Add-AppMark($Graphics, [float]$X, [float]$Y, [float]$Size) {
    $tile = New-RoundedRectanglePath $X $Y $Size $Size ($Size * 0.22)
    $brush = New-AccentBrush $X $Y $Size $Size 45
    $Graphics.FillPath($brush, $tile)
    $brush.Dispose()
    $tile.Dispose()
    $markBars = [float[]]@(0.35, 0.70, 1.00, 0.55, 0.80)
    Add-Waveform $Graphics ($X + $Size * 0.18) ($Y + $Size / 2) ($Size * 0.64) ($Size * 0.56) $markBars 255
}

$format24 = [System.Drawing.Imaging.PixelFormat]::Format24bppRgb

# Welcome / exit dialog: 493x312. WixUI draws its text from x=180 on, so the
# branded panel stays left of that and the rest stays light for black text.
$dialog = New-Canvas 493 312 $format24
$dialog.Graphics.Clear([System.Drawing.Color]::White)
$panelBrush = New-AccentBrush 0 0 164 312 90
$dialog.Graphics.FillRectangle($panelBrush, 0, 0, 164, 312)
$panelBrush.Dispose()
Add-Waveform $dialog.Graphics 14 230 136 90 $BarHeights 110
$titleFont = New-Object System.Drawing.Font('Segoe UI Semibold', 28, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$taglineFont = New-Object System.Drawing.Font('Segoe UI', 11, [System.Drawing.FontStyle]::Italic, [System.Drawing.GraphicsUnit]::Pixel)
$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
$softWhite = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(210, 255, 255, 255))
$dialog.Graphics.DrawString('slaw', $titleFont, $white, 16, 24)
$dialog.Graphics.DrawString("Beyond Deterministic`nProduction", $taglineFont, $softWhite, 18, 64)
$titleFont.Dispose()
$taglineFont.Dispose()
$white.Dispose()
$softWhite.Dispose()
Save-Bitmap $dialog 'dialog.bmp'

# Banner atop the inner dialogs: 493x58. Title text runs on the left, the mark sits right.
$banner = New-Canvas 493 58 $format24
$banner.Graphics.Clear([System.Drawing.Color]::White)
Add-AppMark $banner.Graphics 437 9 40
Save-Bitmap $banner 'banner.bmp'

# Application icon: PNG-compressed entries in an ICO container
$iconSizes = @(16, 24, 32, 48, 64, 128, 256)
$pngImages = foreach ($size in $iconSizes) {
    $canvas = New-Canvas $size $size ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $canvas.Graphics.Clear([System.Drawing.Color]::Transparent)
    Add-AppMark $canvas.Graphics 0 0 $size
    $canvas.Graphics.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $canvas.Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.Bitmap.Dispose()
    , $stream.ToArray()
}

$iconStream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($iconStream)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$iconSizes.Count)
$imageOffset = 6 + 16 * $iconSizes.Count
for ($index = 0; $index -lt $iconSizes.Count; $index++) {
    $dimension = if ($iconSizes[$index] -ge 256) { 0 } else { $iconSizes[$index] }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$pngImages[$index].Length)
    $writer.Write([UInt32]$imageOffset)
    $imageOffset += $pngImages[$index].Length
}
foreach ($png in $pngImages) { $writer.Write([byte[]]$png) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'slaw.ico'), $iconStream.ToArray())
$writer.Dispose()

# LICENSE as RTF, the only text format the WixUI license dialog accepts
$licenseText = (Get-Content -Raw -LiteralPath $LicensePath) -replace "`r`n", "`n"
$rtfBody = New-Object System.Text.StringBuilder
foreach ($character in $licenseText.ToCharArray()) {
    switch ($character) {
        '\' { [void]$rtfBody.Append('\\') }
        '{' { [void]$rtfBody.Append('\{') }
        '}' { [void]$rtfBody.Append('\}') }
        "`n" { [void]$rtfBody.Append("\par`n") }
        default {
            if ([int]$character -gt 127) { [void]$rtfBody.Append("\u$([int]$character)?") }
            else { [void]$rtfBody.Append($character) }
        }
    }
}
$rtf = "{\rtf1\ansi\deff0{\fonttbl{\f0\fswiss Segoe UI;}}\viewkind4\uc1\pard\f0\fs18 $rtfBody}"
[System.IO.File]::WriteAllText((Join-Path $OutputDirectory 'License.rtf'), $rtf, [System.Text.Encoding]::ASCII)

Write-Host "[assets] installer artwork written to $OutputDirectory"
