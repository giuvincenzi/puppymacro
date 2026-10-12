# Draws the icons File Explorer shows for PuppyMacro's files (approved design: the Design canvas "PuppyMacro Import and
# Export", Icons B) and writes them to PuppyMacro/Assets/FileIcons:
#   PuppyMacroItems.ico   .puppyitems (loops, macros and remaps to share): a page with the paw on it
#   PuppyMacroBackup.ico  .puppymacro (backup): an archive box with the paw
# Each .ico holds PNG images at 16 to 256 px, drawn with WPF from the shapes below (a 100 x 100 canvas).
# -Preview <folder> also writes each size as a PNG there, to look at them.
param([string]$Preview)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$out = Join-Path $PSScriptRoot '..\PuppyMacro\Assets\FileIcons'
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256

function Brush([string]$hex) {
    $brush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($hex))
    $brush.Freeze()
    $brush
}

function Pen([string]$hex, [double]$width) {
    $pen = New-Object System.Windows.Media.Pen ((Brush $hex), $width)
    $pen.LineJoin = 'Round'
    $pen.Freeze()
    $pen
}

function Path([string]$data) { [System.Windows.Media.Geometry]::Parse($data) }

# The paw of PuppyMacro's icon: four toes and the pad, in a 100 x 100 box.
function Paw {
    $group = New-Object System.Windows.Media.GeometryGroup
    foreach ($toe in @(@(27, 47, 7, 9.5, -25), @(40.5, 30, 7.5, 10, 0), @(59.5, 30, 7.5, 10, 0), @(73, 47, 7, 9.5, 25))) {
        $ellipse = New-Object System.Windows.Media.EllipseGeometry ((New-Object System.Windows.Point $toe[0], $toe[1]), $toe[2], $toe[3])
        $ellipse.Transform = New-Object System.Windows.Media.RotateTransform ($toe[4], $toe[0], $toe[1])
        $group.Children.Add($ellipse)
    }
    $group.Children.Add((Path 'M50 46c11 0 21 9 22 20 1 8-5 13-12 11-5-1-15-1-20 0-7 2-13-3-12-11 1-11 11-20 22-20z'))
    $group
}

# The paw at (x, y), size s, in the 100 x 100 canvas.
function DrawPaw($dc, [string]$color, [double]$x, [double]$y, [double]$s) {
    $dc.PushTransform((New-Object System.Windows.Media.MatrixTransform ($s / 100), 0, 0, ($s / 100), $x, $y))
    $dc.DrawGeometry((Brush $color), $null, (Paw))
    $dc.Pop()
}

function DrawItems($dc) {
    $dc.DrawGeometry((Brush '#FFFFFF'), (Pen '#8A8A8A' 2), (Path 'M20 3h42l22 22v69a3 3 0 0 1-3 3H20a3 3 0 0 1-3-3V6a3 3 0 0 1 3-3z'))
    $dc.DrawGeometry((Brush '#E8E8E8'), (Pen '#8A8A8A' 2), (Path 'M62 3v19a3 3 0 0 0 3 3h19'))
    DrawPaw $dc '#0F6CBD' 22.5 33 56
}

function DrawBackup($dc) {
    $dc.DrawGeometry((Brush '#3A3A40'), (Pen '#1D1D21' 2), (New-Object System.Windows.Media.RectangleGeometry ((New-Object System.Windows.Rect 10, 30, 80, 62), 4, 4)))
    $dc.DrawGeometry((Brush '#4A4A52'), (Pen '#1D1D21' 2), (New-Object System.Windows.Media.RectangleGeometry ((New-Object System.Windows.Rect 6, 14, 88, 20), 4, 4)))
    $dc.DrawGeometry((Brush '#1D1D21'), $null, (New-Object System.Windows.Media.RectangleGeometry ((New-Object System.Windows.Rect 38, 22, 24, 6), 3, 3)))
    DrawPaw $dc '#60CDFF' 28 38 44
}

function Png([scriptblock]$draw, [int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform ($size / 100), ($size / 100)))
    & $draw $dc
    $dc.Pop()
    $dc.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap ($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    $stream.ToArray()
}

# An .ico with PNG images (Windows Vista and later): the header, one 16-byte entry per image, then the images.
function WriteIco([string]$name, [scriptblock]$draw) {
    $images = @($sizes | ForEach-Object { , (Png $draw $_) })
    $file = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $file
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $size = $sizes[$i]
        $side = if ($size -ge 256) { 0 } else { $size }
        $writer.Write([byte]$side); $writer.Write([byte]$side); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write($image) }
    $writer.Flush()
    New-Item -ItemType Directory -Force $out | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $out "$name.ico"), $file.ToArray())
    Write-Host "Wrote $name.ico"

    if ($Preview) {
        New-Item -ItemType Directory -Force $Preview | Out-Null
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            [System.IO.File]::WriteAllBytes((Join-Path $Preview "$name-$($sizes[$i]).png"), $images[$i])
        }
    }
}

WriteIco 'PuppyMacroItems' ${function:DrawItems}
WriteIco 'PuppyMacroBackup' ${function:DrawBackup}
