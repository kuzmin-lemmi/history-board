$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$assetDirectory = Join-Path $PSScriptRoot 'Assets'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
$visual = [System.Windows.Media.DrawingVisual]::new()
$context = $visual.RenderOpen()
function IconBrush([string]$hex) { return [System.Windows.Media.BrushConverter]::new().ConvertFromString($hex) }
$cream = IconBrush '#F3EDDA'
$gold = IconBrush '#DFA75B'
$context.DrawRoundedRectangle((IconBrush '#365749'), $null, [System.Windows.Rect]::new(4,4,120,120), 28,28)
$context.DrawGeometry($cream, $null, [System.Windows.Media.Geometry]::Parse('M32,48 L64,28 L96,48 Z'))
$context.DrawRoundedRectangle($cream,$null,[System.Windows.Rect]::new(32,52,64,6),2,2)
foreach ($columnX in @(39,60,81)) { $context.DrawRectangle($cream,$null,[System.Windows.Rect]::new($columnX,63,8,25)) }
$context.DrawRoundedRectangle($cream,$null,[System.Windows.Rect]::new(31,92,65,7),2,2)
$arrowPen = [System.Windows.Media.Pen]::new($gold,7)
$arrowPen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
$arrowPen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
$context.DrawLine($arrowPen,[System.Windows.Point]::new(73,94),[System.Windows.Point]::new(102,66))
$context.DrawGeometry($gold,$null,[System.Windows.Media.Geometry]::Parse('M91,65 L109,58 L103,77 Z'))
$context.Close()
$pngFrames = [System.Collections.Generic.List[byte[]]]::new()
$sizes = @(256,128,64,48,32,24,16)
foreach ($size in $sizes) {
    $scaled = [System.Windows.Media.DrawingVisual]::new()
    $drawing = $scaled.RenderOpen()
    $drawing.PushTransform([System.Windows.Media.ScaleTransform]::new($size/128.0,$size/128.0))
    $drawing.DrawDrawing($visual.Drawing)
    $drawing.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($scaled)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memory = [System.IO.MemoryStream]::new()
    $encoder.Save($memory)
    $pngFrames.Add($memory.ToArray())
    $memory.Dispose()
}
[System.IO.File]::WriteAllBytes((Join-Path $assetDirectory 'brand.png'), $pngFrames[0])
$icon = [System.IO.File]::Create((Join-Path $assetDirectory 'app.ico'))
$writer = [System.IO.BinaryWriter]::new($icon)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index=0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$pngFrames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $pngFrames[$index].Length
    }
    foreach ($frame in $pngFrames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
Write-Host 'Иконка создана: 7 размеров от 16 до 256 пикселей.'
