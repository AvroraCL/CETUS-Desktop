#Requires -Version 7
<#
.SYNOPSIS
    Render the CETUS README banner (dark + light variants).

    Layout: rounded app-icon card on the left, title + tagline on the right,
    both vertically centered on a 1200x360 canvas. Colors follow the window
    title-bar palette (#151517 dark / #F5F7FA light).

    Output:
      docs/banner-dark.png
      docs/banner-light.png
#>
[CmdletBinding()]
param(
    [switch]$SkipCleanup
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root    = Split-Path -Parent $PSScriptRoot
$icon    = Join-Path $root "docs\CETUS-ico.png"
$outDark = Join-Path $root "docs\banner-dark.png"
$outLite = Join-Path $root "docs\banner-light.png"

$Width  = 1200
$Height = 360

$CardSize = 248
$CardX = 84
$CardY = [int](($Height - $CardSize) / 2)
$IconSize = 224
$IconX = $CardX + [int](($CardSize - $IconSize) / 2)
$IconY = $CardY + [int](($CardSize - $IconSize) / 2)
$TitleX = $CardX + $CardSize + 56
$TextRightEdge = $Width - 60

function Convert-IconColor {
    <#
    .SYNOPSIS
        Prepare the icon for a banner background.
        - Light banner: the source icon as-is (dark line-art + bright stars).
        - Dark banner: a negative of the source (white line-art + dark stars),
          so the shape reads on a dark background while keeping the star detail
          instead of becoming a solid white silhouette.
    #>
    param(
        [string]$Source,
        [bool]$Invert
    )
    $sourceBitmap = [System.Drawing.Bitmap]::new($Source)
    if (-not $Invert) {
        # Returned as-is; the caller owns and disposes it.
        return $sourceBitmap
    }

    try {
        $out = [System.Drawing.Bitmap]::new($sourceBitmap.Width, $sourceBitmap.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        for ($y = 0; $y -lt $sourceBitmap.Height; $y++) {
            for ($x = 0; $x -lt $sourceBitmap.Width; $x++) {
                $pixel = $sourceBitmap.GetPixel($x, $y)
                $out.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(
                    $pixel.A,
                    [byte](255 - $pixel.R),
                    [byte](255 - $pixel.G),
                    [byte](255 - $pixel.B)))
            }
        }
        return $out
    }
    finally {
        $sourceBitmap.Dispose()
    }
}

function Render-Banner {
        param(
            [string]$Background,
            [bool]$InvertIcon,
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

        # App icon (inverted on dark so the dark line-art stays visible)
        $iconImage = Convert-IconColor -Source $icon -Invert $InvertIcon
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
Render-Banner -Background "#151517" -InvertIcon $true `
    -TitleColor "#F5F7FA" -TaglineColor "#AAB7CC" -Output $outDark
Render-Banner -Background "#F5F7FA" -InvertIcon $false `
    -TitleColor "#172033" -TaglineColor "#667085" -Output $outLite
Write-Host "DONE"
