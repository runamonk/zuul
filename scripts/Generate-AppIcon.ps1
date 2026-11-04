$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path $PSScriptRoot '../src/Zuul/Assets'
[IO.Directory]::CreateDirectory($assetDirectory) | Out-Null

# Vector geometry rendered at 4x resolution, then downsampled for each Windows icon size.
$canvas = [Drawing.Bitmap]::new(1024, 1024)
$graphics = [Drawing.Graphics]::FromImage($canvas)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::Transparent)
$graphics.ScaleTransform(4, 4)

# A dark tile with a prompt on it: you type, it does.
$tile = [Drawing.Drawing2D.GraphicsPath]::new()
$tile.AddArc(16, 16, 96, 96, 180, 90)
$tile.AddArc(144, 16, 96, 96, 270, 90)
$tile.AddArc(144, 144, 96, 96, 0, 90)
$tile.AddArc(16, 144, 96, 96, 90, 90)
$tile.CloseFigure()
$background = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#202020'))
$graphics.FillPath($background, $tile)
$background.Dispose(); $tile.Dispose()

$pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#60CDFF'), 26)
$pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
$pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
$graphics.DrawLines($pen, [Drawing.PointF[]]@([Drawing.PointF]::new(70, 78), [Drawing.PointF]::new(124, 128), [Drawing.PointF]::new(70, 178)))
$graphics.DrawLine($pen, 146, 178, 190, 178)
$pen.Dispose()
$graphics.Dispose()

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($canvas, [Drawing.Rectangle]::new(0, 0, $size, $size))
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $frames += ,$stream.ToArray()
    if ($size -eq 256) { $bitmap.Save((Join-Path $assetDirectory 'Zuul.png'), [Drawing.Imaging.ImageFormat]::Png) }
    $stream.Dispose(); $g.Dispose(); $bitmap.Dispose()
}
$canvas.Dispose()

$file = [IO.File]::Create((Join-Path $assetDirectory 'Zuul.ico'))
$writer = [IO.BinaryWriter]::new($file)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Dispose()
Write-Output 'Generated Zuul.png and Zuul.ico (16-256px).'
