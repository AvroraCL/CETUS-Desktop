#Requires -Version 7
<#
.SYNOPSIS
    Render the CETUS README banner (dark + light variants).

    Layout: the original app icon on the left, title + tagline on the right,
    vertically centered on a 1200x360 canvas. The icon is drawn unchanged in
    both variants.

    Output:
      docs/banner-dark.png
      docs/banner-light.png
#>
[CmdletBinding()]
param([switch]$SkipCleanup)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root    = Split-Path -Parent $PSScriptRoot
$icon    = Join-Path $root "docs\CETUS-ico.png"
$outDark = Join-Path $root "docs\banner-dark.png"
$outLite = Join-Path $root "docs\banner-light.png"

$Width  = 1200
$Height = 360

$TileSize = 248
$TileX = 84
$TileY = [int](($Height - $TileSize) / 2)
$IconSize = 224
$IconX = $TileX + [int](($TileSize - $IconSize) / 2)
$IconY = $TileY + [int](($TileSize - $IconSize) / 2)
$TitleX = $TileX + $TileSize + 56
$TextRightEdge = $Width - 60

function Render-Banner {
    param(
        [string]$Background,
        [string]$TitleColor,
        [string]$TaglineColor,
        [string]$Output
    )

    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.Clear([System.Drawing.ColorTranslator]::FromHtml($Background))

        $iconImage = [System.Drawing.Image]::FromFile($icon)
        try {
            $graphics.DrawImage($iconImage, $IconX, $IconY, $IconSize, $IconSize)
        }
        finally {
            $iconImage.Dispose()
        }

        # Title + tagline
        $titleFont = [System.Drawing.Font]::new("Microsoft YaHei UI", 60, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $taglineFont = [System.Drawing.Font]::new("Microsoft YaHei UI", 21, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $titleBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($TitleColor))
        $taglineBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($TaglineColor))

        $titleText = "CETUS · 鲸鱼座"
        $taglineText = "将 DeepSeek Harness 带到 Windows 桌面的原生工作台。"

        $titleSize = $graphics.MeasureString($titleText, $titleFont)
        $taglineSize = $graphics.MeasureString($taglineText, $taglineFont)
        $gap = 26
        $totalHeight = $titleSize.Height + $gap + $taglineSize.Height
        $startY = ($Height - $totalHeight) / 2

        if ($TitleX + $titleSize.Width -gt $TextRightEdge) {
            throw "Title overflows the canvas (${Width}x${Height})."
        }
        if ($TitleX + $taglineSize.Width -gt $TextRightEdge) {
            throw "Tagline overflows the canvas (${Width}x${Height})."
        }

        $graphics.DrawString($titleText, $titleFont, $titleBrush, [float]$TitleX, [float]$startY)
        $graphics.DrawString($taglineText, $taglineFont, $taglineBrush, [float]$TitleX, [float]($startY + $titleSize.Height + $gap))

        $bitmap.Save($Output, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host "  wrote $Output ($Width x $Height, title width $([int]$titleSize.Width)px, tagline width $([int]$taglineSize.Width)px)"

        $titleFont.Dispose(); $taglineFont.Dispose()
        $titleBrush.Dispose(); $taglineBrush.Dispose()
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

Write-Host "==> rendering README banners"
Render-Banner -Background "#151517" `
    -TitleColor "#F5F7FA" -TaglineColor "#AAB7CC" -Output $outDark
Render-Banner -Background "#F5F7FA" `
    -TitleColor "#172033" -TaglineColor "#667085" -Output $outLite
Write-Host "DONE"
