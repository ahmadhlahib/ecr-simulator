# Draws the ECR Simulator icon (assets\ecr.ico with 16-256 px, assets\ecr.png). Created by Ahmad El Lahib.
# Run: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function Draw([int]$s) {
    $b = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $u = $s / 256.0
    # tile
    $tile = RoundRect (4 * $u) (4 * $u) (248 * $u) (248 * $u) (52 * $u)
    $grad = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.PointF]::new(0, 0)), ([Drawing.PointF]::new(0, $s)), ([Drawing.Color]::FromArgb(18, 132, 226)), ([Drawing.Color]::FromArgb(0, 70, 150))
    $g.FillPath($grad, $tile)
    $white = [Drawing.Brushes]::White
    # display pole + customer display
    $g.FillPath($white, (RoundRect (74 * $u) (40 * $u) (108 * $u) (62 * $u) (12 * $u)))
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(24, 32, 40))), (RoundRect (84 * $u) (50 * $u) (88 * $u) (42 * $u) (6 * $u)))
    $green = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(120, 255, 160))
    $g.FillRectangle($green, 94 * $u, 64 * $u, 68 * $u, 14 * $u)
    $g.FillRectangle($white, 118 * $u, 100 * $u, 20 * $u, 22 * $u)
    # register body (trapezoid) + drawer
    $body = New-Object Drawing.Drawing2D.GraphicsPath
    $body.AddPolygon([Drawing.PointF[]]@(
        [Drawing.PointF]::new(58 * $u, 122 * $u), [Drawing.PointF]::new(198 * $u, 122 * $u),
        [Drawing.PointF]::new(214 * $u, 186 * $u), [Drawing.PointF]::new(42 * $u, 186 * $u)))
    $g.FillPath($white, $body)
    $g.FillPath($white, (RoundRect (36 * $u) (194 * $u) (184 * $u) (32 * $u) (8 * $u)))
    if ($s -ge 32) {
        # keys
        $key = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(0, 92, 175))
        foreach ($row in 0, 1) { foreach ($col in 0..3) {
            $g.FillRectangle($key, (76 + $col * 28 + $row * -4) * $u, (136 + $row * 22) * $u, 18 * $u, 12 * $u)
        } }
        $g.FillRectangle($key, 108 * $u, 206 * $u, 40 * $u, 8 * $u)
    }
    $g.Dispose(); return $b
}

# Classic 32-bit DIB entry (BITMAPINFOHEADER, bottom-up BGRA, AND mask): System.Drawing.Icon cannot read PNG entries.
function Dib([Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32](2 * $s)); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]0); $w.Write([uint32]0); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) {
        $c = $bmp.GetPixel($x, $y); $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
    } }
    $maskRow = [int]([math]::Ceiling($s / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskRow * $s)))   # all 0: the alpha channel decides
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = Draw $s
    if ($s -eq 256) {
        $bmp.Save((Join-Path $assets 'ecr.png'))
        $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $data = $ms.ToArray()
    } else { $data = Dib $bmp }
    $bmp.Dispose(); , $data
}

# ICO file: header + directory + images (DIB up to 128 px, PNG for 256 px).
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
[IO.File]::WriteAllBytes((Join-Path $assets 'ecr.ico'), $out.ToArray())
"icon written: " + (Join-Path $assets 'ecr.ico')
