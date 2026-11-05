param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = $PSScriptRoot
$output = Join-Path $root 'assets'
[void][System.IO.Directory]::CreateDirectory($output)
$sourcePath = Join-Path (Split-Path -Parent $root) '大胆国风UI重构_第二轮_20261104/02_宋人书斋_全新布局_2K.png'
$source = [System.Drawing.Bitmap]::new($sourcePath)

function Export-Crop([string]$name, [int]$x, [int]$y, [int]$w, [int]$h) {
    $bitmap = $source.Clone([System.Drawing.Rectangle]::new($x,$y,$w,$h), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try { $bitmap.Save((Join-Path $output $name), [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
}

function Export-InkCrop([string]$name, [int]$x, [int]$y, [int]$w, [int]$h, [int[]]$points = @()) {
    $bitmap = $source.Clone([System.Drawing.Rectangle]::new($x,$y,$w,$h), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
    if ($points.Length -gt 0) {
        $vertices = [System.Drawing.Point[]]::new($points.Length / 2)
        for ($i=0; $i -lt $vertices.Length; $i++) { $vertices[$i] = [System.Drawing.Point]::new($points[$i*2],$points[$i*2+1]) }
        $shape.AddPolygon($vertices)
    }
    try {
        for ($row=0; $row -lt $h; $row++) {
            for ($col=0; $col -lt $w; $col++) {
                $pixel = $bitmap.GetPixel($col,$row)
                $minimum = [Math]::Min([Math]::Min([int]$pixel.R,[int]$pixel.G),[int]$pixel.B)
                $alpha = [Math]::Clamp((235.0-$minimum)/160.0,0.0,1.0)
                if ($points.Length -gt 0 -and -not $shape.IsVisible($col,$row)) { $alpha=0 }
                if ($alpha -le 0) { $bitmap.SetPixel($col,$row,[System.Drawing.Color]::Transparent); continue }
                # Unmatte against warm paper instead of retaining its rectangular pixels.
                $r = [int][Math]::Clamp(($pixel.R-235*(1-$alpha))/$alpha,0.0,255.0)
                $g = [int][Math]::Clamp(($pixel.G-235*(1-$alpha))/$alpha,0.0,255.0)
                $b = [int][Math]::Clamp(($pixel.B-235*(1-$alpha))/$alpha,0.0,255.0)
                $bitmap.SetPixel($col,$row,[System.Drawing.Color]::FromArgb([int]($alpha*255),$r,$g,$b))
            }
        }
        $bitmap.Save((Join-Path $output $name),[System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $shape.Dispose(); $bitmap.Dispose() }
}

function Export-BlankComponent([string]$name, [int]$x, [int]$y, [int]$w, [int]$h, [int[]]$points, [int]$patchX, [int]$patchY, [int]$patchW, [int]$patchH, [int]$tileX, [int]$tileY, [int]$tileW, [int]$tileH) {
    $bitmap = [System.Drawing.Bitmap]::new($w,$h,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $vertices = [System.Drawing.Point[]]::new($points.Length / 2)
    for ($i=0; $i -lt $vertices.Length; $i++) { $vertices[$i] = [System.Drawing.Point]::new($points[$i*2], $points[$i*2+1]) }
    $shape.AddPolygon($vertices)
    $tile = $source.Clone([System.Drawing.Rectangle]::new($tileX,$tileY,$tileW,$tileH), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $brush = [System.Drawing.TextureBrush]::new($tile,[System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    try {
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.SetClip($shape)
        $g.DrawImage($source,[System.Drawing.Rectangle]::new(0,0,$w,$h),[System.Drawing.Rectangle]::new($x,$y,$w,$h),[System.Drawing.GraphicsUnit]::Pixel)
        # Replace all source control/icon/text pixels inside the decorative outline.
        $g.FillRectangle($brush,$patchX,$patchY,$patchW,$patchH)
        $bitmap.Save((Join-Path $output $name),[System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $brush.Dispose(); $tile.Dispose(); $shape.Dispose(); $g.Dispose(); $bitmap.Dispose() }
}

try {
    Export-Crop 'desk-paper.png' 500 0 512 64
    Export-Crop 'page-paper.png' 1170 120 128 96
    Export-Crop 'nav-paper.png' 100 535 48 42
    Export-Crop 'wood-edge.png' 54 770 12 200
    Export-InkCrop 'bamboo-top.png' 0 0 307 103
    Export-InkCrop 'bamboo-side.png' 0 103 64 802
    Export-InkCrop 'bamboo-foot.png' 0 907 54 288
    Export-InkCrop 'bamboo-page.png' 2345 120 116 117
    Export-InkCrop 'bamboo-insight.png' 2165 790 296 133
    Export-InkCrop 'inkstone.png' 2400 1200 160 240 @(126,10,150,17,160,68,160,240,0,240,33,205,55,165,83,140,114,119,125,104,124,70)
    Export-Crop 'book-top.png' 308 64 2252 56
    Export-Crop 'book-bottom.png' 308 1352 2252 62
    Export-Crop 'book-left.png' 308 120 98 1232
    Export-Crop 'book-right.png' 2485 120 75 1232
    Export-Crop 'book-spine.png' 1359 115 37 1237
    Export-Crop 'landscape.png' 407 941 950 356
    Export-BlankComponent 'bookmark-idle.png' 64 431 310 174 @(8,4,273,4,280,20,298,28,298,145,278,155,272,166,8,166) 25 24 249 122 100 535 48 42
    Export-BlankComponent 'bookmark-active.png' 64 250 310 180 @(8,8,274,8,280,25,298,34,298,145,282,152,273,169,8,169) 25 24 249 139 100 535 48 42
    Export-BlankComponent 'seal-blank.png' 2270 1168 189 175 @(20,7,154,7,160,20,178,28,178,148,166,154,155,166,25,166,15,150,11,144,11,31,19,24) 26 33 141 115 2342 1202 24 14
    Write-Output '原稿组件制作完成：19 个 PNG；所有新文件位于原型 assets/。'
} finally { $source.Dispose() }