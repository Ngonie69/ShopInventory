Add-Type -AssemblyName System.Drawing

function Create-FaviconPng {
    param(
        [int]$Size,
        [string]$OutputPath
    )
    
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'HighQuality'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.InterpolationMode = 'HighQualityBicubic'
    
    # Background - dark navy rounded rect
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($Size, $Size)),
        [System.Drawing.Color]::FromArgb(255, 26, 26, 46),
        [System.Drawing.Color]::FromArgb(255, 22, 33, 62)
    )
    
    $radius = [int]($Size * 0.19)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $radius * 2, $radius * 2, 180, 90)
    $path.AddArc($Size - $radius * 2, 0, $radius * 2, $radius * 2, 270, 90)
    $path.AddArc($Size - $radius * 2, $Size - $radius * 2, $radius * 2, $radius * 2, 0, 90)
    $path.AddArc(0, $Size - $radius * 2, $radius * 2, $radius * 2, 90, 90)
    $path.CloseFigure()
    $g.FillPath($bgBrush, $path)
    
    # Letter K - white, bold
    $fontSize = [int]($Size * 0.6)
    $font = New-Object System.Drawing.Font('Arial', $fontSize, [System.Drawing.FontStyle]::Bold)
    $whiteBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = 'Center'
    $sf.LineAlignment = 'Center'
    $rect = New-Object System.Drawing.RectangleF(0, 0, $Size, $Size)
    $g.DrawString('K', $font, $whiteBrush, $rect, $sf)
    
    $g.Dispose()
    $bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    
    Write-Host "Created: $OutputPath"
}

# Create main favicon
Create-FaviconPng -Size 32 -OutputPath 'ShopInventory.Web\wwwroot\favicon.png'

# Create PWA icons
$sizes = @(72, 96, 128, 144, 152, 192, 384, 512)
foreach ($s in $sizes) {
    $outPath = "ShopInventory.Web\wwwroot\images\icons\icon-${s}x${s}.png"
    Create-FaviconPng -Size $s -OutputPath $outPath
}

Write-Host "All icons created successfully!"
