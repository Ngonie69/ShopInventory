# Renders the Kefalos temple mark (see ShopInventory.Web/wwwroot/images/kefalos-app-icon.svg)
# to the PNG sizes referenced by wwwroot/manifest.json.
Add-Type -AssemblyName System.Drawing

# GDI+ resolves relative paths against the process working directory rather than the
# PowerShell location, so every output path is made absolute up front.
$RepoRoot = $PSScriptRoot
$IconDir = Join-Path $RepoRoot 'ShopInventory.Web\wwwroot\images\icons'
if (-not (Test-Path $IconDir)) { New-Item -ItemType Directory -Path $IconDir -Force | Out-Null }

$Navy = [System.Drawing.Color]::FromArgb(255, 11, 23, 40)    # #0B1728
$Gold = [System.Drawing.Color]::FromArgb(255, 245, 179, 1)   # #F5B301

# The mark is authored on a 512x512 grid, ink spanning x 20..492 and y 34..474.
$MarkCenterX = 256
$MarkCenterY = 254

function Add-Rect {
    param([System.Drawing.Drawing2D.GraphicsPath]$Path, [single]$X, [single]$Y, [single]$W, [single]$H)
    $Path.AddRectangle((New-Object System.Drawing.RectangleF($X, $Y, $W, $H)))
}

function New-MarkPath {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath

    # Pediment
    $path.AddPolygon(@(
        (New-Object System.Drawing.PointF(256, 34)),
        (New-Object System.Drawing.PointF(470, 148)),
        (New-Object System.Drawing.PointF(42, 148))
    ))

    # Entablature
    Add-Rect $path 36 158 440 26
    Add-Rect $path 58 192 396 16

    # Columns
    Add-Rect $path 64 216 34 170
    Add-Rect $path 112 216 34 170
    Add-Rect $path 366 216 34 170
    Add-Rect $path 414 216 34 170

    # Kefalos K
    $path.AddPolygon(@(
        (New-Object System.Drawing.PointF(173.5, 216)),
        (New-Object System.Drawing.PointF(218.5, 216)),
        (New-Object System.Drawing.PointF(218.5, 274)),
        (New-Object System.Drawing.PointF(274.5, 216)),
        (New-Object System.Drawing.PointF(331.5, 216)),
        (New-Object System.Drawing.PointF(250.5, 295)),
        (New-Object System.Drawing.PointF(338.5, 386)),
        (New-Object System.Drawing.PointF(277.5, 386)),
        (New-Object System.Drawing.PointF(218.5, 321)),
        (New-Object System.Drawing.PointF(218.5, 386)),
        (New-Object System.Drawing.PointF(173.5, 386))
    ))

    # Stepped stylobate
    Add-Rect $path 58 398 396 22
    Add-Rect $path 38 428 436 20
    Add-Rect $path 20 456 472 18

    return $path
}

# Favicon variant: columns and stylobate steps are dropped so the temple still reads
# at 16-32px. Mirrors ShopInventory.Web/wwwroot/favicon.svg.
function New-SimpleMarkPath {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath

    $path.AddPolygon(@(
        (New-Object System.Drawing.PointF(256, 46)),
        (New-Object System.Drawing.PointF(456, 158)),
        (New-Object System.Drawing.PointF(56, 158))
    ))
    Add-Rect $path 48 172 416 34
    Add-Rect $path 40 436 432 34

    # The K, enlarged to carry the mark on its own.
    $k = New-Object System.Drawing.Drawing2D.GraphicsPath
    $k.AddPolygon(@(
        (New-Object System.Drawing.PointF(173.5, 216)),
        (New-Object System.Drawing.PointF(218.5, 216)),
        (New-Object System.Drawing.PointF(218.5, 274)),
        (New-Object System.Drawing.PointF(274.5, 216)),
        (New-Object System.Drawing.PointF(331.5, 216)),
        (New-Object System.Drawing.PointF(250.5, 295)),
        (New-Object System.Drawing.PointF(338.5, 386)),
        (New-Object System.Drawing.PointF(277.5, 386)),
        (New-Object System.Drawing.PointF(218.5, 321)),
        (New-Object System.Drawing.PointF(218.5, 386)),
        (New-Object System.Drawing.PointF(173.5, 386))
    ))
    $matrix = New-Object System.Drawing.Drawing2D.Matrix
    $matrix.Translate(-15.05, 7.3)
    $matrix.Scale(1.0588, 1.0588)
    $k.Transform($matrix)
    $path.AddPath($k, $false)
    $matrix.Dispose()
    $k.Dispose()

    return $path
}

function Create-IconPng {
    param(
        [int]$Size,
        [string]$OutputPath,
        # Maskable icons are cropped by the platform, so the mark stays inside the safe circle.
        [single]$MarkScale = 0.62,
        [switch]$Simple
    )

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'HighQuality'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'

    $g.Clear($Navy)

    $centerY = if ($Simple) { 258 } else { $MarkCenterY }

    # Work in the 512x512 authoring grid, then scale the mark about its optical centre.
    $g.ScaleTransform($Size / 512.0, $Size / 512.0)
    $g.TranslateTransform($MarkCenterX, $centerY)
    $g.ScaleTransform($MarkScale, $MarkScale)
    $g.TranslateTransform(-$MarkCenterX, -$centerY)

    $path = if ($Simple) { New-SimpleMarkPath } else { New-MarkPath }
    $goldBrush = New-Object System.Drawing.SolidBrush($Gold)
    $g.FillPath($goldBrush, $path)

    $goldBrush.Dispose()
    $path.Dispose()
    $g.Dispose()
    $bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    Write-Host "Created: $OutputPath"
}

# Browser tab favicon: no platform mask, so the simplified mark can breathe wider.
Create-IconPng -Size 32 -MarkScale 0.84 -Simple -OutputPath (Join-Path $RepoRoot 'ShopInventory.Web\wwwroot\favicon.png')

# PWA icons, declared "any maskable" in manifest.json
$sizes = @(72, 96, 128, 144, 152, 192, 384, 512)
foreach ($s in $sizes) {
    Create-IconPng -Size $s -OutputPath (Join-Path $IconDir "icon-${s}x${s}.png")
}

Write-Host "All icons created successfully!"
