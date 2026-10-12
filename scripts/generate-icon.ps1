[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destination = Join-Path $root 'src\AiUsageTray.Desktop\assets\app.ico'
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$images = [Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size * 4, $size * 4)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $frame = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#fd8ea1'), 2)
    $arc = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#fd8ea1'), 2.8)
    $needle = [Drawing.Pen]::new([Drawing.Color]::White, 2.8)
    $background = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#292929'))
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform($size / 8.0, $size / 8.0)
        $path.AddArc(2, 2, 16, 16, 180, 90)
        $path.AddArc(14, 2, 16, 16, 270, 90)
        $path.AddArc(14, 14, 16, 16, 0, 90)
        $path.AddArc(2, 14, 16, 16, 90, 90)
        $path.CloseFigure()
        $graphics.FillPath($background, $path)
        $graphics.DrawPath($frame, $path)
        $arc.StartCap = $arc.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $needle.StartCap = $needle.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawArc($arc, 6, 6, 20, 20, 145, 250)
        $graphics.DrawLine($needle, 16, 19, 22, 12)
        $graphics.FillEllipse([Drawing.Brushes]::White, 13.5, 16.5, 5, 5)
        $small = [Drawing.Bitmap]::new($size, $size)
        $scaled = [Drawing.Graphics]::FromImage($small)
        $stream = [IO.MemoryStream]::new()
        try {
            $scaled.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $scaled.DrawImage($bitmap, 0, 0, $size, $size)
            $small.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            $images.Add($stream.ToArray())
        } finally { $stream.Dispose(); $scaled.Dispose(); $small.Dispose() }
    } finally {
        $background.Dispose(); $needle.Dispose(); $arc.Dispose(); $frame.Dispose()
        $path.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
}
$file = [IO.File]::Create($destination)
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length); $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }
    foreach ($image in $images) { $writer.Write($image) }
} finally { $writer.Dispose(); $file.Dispose() }
Get-FileHash -LiteralPath $destination -Algorithm SHA256
