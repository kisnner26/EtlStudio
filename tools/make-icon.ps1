# Genera assets\EtlStudio.ico (16-256 px) y assets\EtlStudio-512.png dibujando el logo por codigo.
# Logo: cuadrado redondeado azul (degradado) + cilindro blanco (Data Warehouse) + destello (IA).
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'assets'; New-Item -ItemType Directory -Force $out | Out-Null

function New-Logo([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # fondo: squircle con degradado
    $m = $s * 0.03; $w = $s - 2 * $m; $r = $s * 0.235
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, $r * 2, $r * 2, 180, 90); $path.AddArc($m + $w - $r * 2, $m, $r * 2, $r * 2, 270, 90)
    $path.AddArc($m + $w - $r * 2, $m + $w - $r * 2, $r * 2, $r * 2, 0, 90); $path.AddArc($m, $m + $w - $r * 2, $r * 2, $r * 2, 90, 90); $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF($m, $m, $w, $w)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(255, 77, 163, 255), [System.Drawing.Color]::FromArgb(255, 0, 72, 200), 65)
    $g.FillPath($grad, $path)
    # brillo suave superior
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(70, 255, 255, 255), [System.Drawing.Color]::FromArgb(0, 255, 255, 255), 90)
    $g.FillPath($hl, $path)

    # cilindro (Data Warehouse)
    $cw = $s * 0.44; $cx = $s * 0.5 - $cw / 2 - $s * 0.03; $cy = $s * 0.27; $ch = $s * 0.46; $eh = $s * 0.13
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $shade = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 226, 238, 255))
    $g.FillRectangle($shade, $cx, $cy + $eh / 2, $cw, $ch - $eh)
    $g.FillEllipse($shade, $cx, $cy + $ch - $eh, $cw, $eh)
    $g.FillEllipse($white, $cx, $cy, $cw, $eh)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(120, 0, 72, 200), [Math]::Max(1.0, $s * 0.017))
    foreach ($k in 1, 2) {
        $yy = $cy + ($ch - $eh) * $k / 3
        $g.DrawArc($pen, $cx, $yy, $cw, $eh, 0, 180)
    }

    # destello (IA), arriba a la derecha
    $sx = $s * 0.72; $sy = $s * 0.30; $R = $s * 0.145; $ri = $R * 0.30
    $pts = New-Object 'System.Drawing.PointF[]' 8
    for ($i = 0; $i -lt 8; $i++) {
        $ang = [Math]::PI / 4 * $i - [Math]::PI / 2; $rad = if ($i % 2 -eq 0) { $R } else { $ri }
        $pts[$i] = New-Object System.Drawing.PointF(($sx + $rad * [Math]::Cos($ang)), ($sy + $rad * [Math]::Sin($ang)))
    }
    $star = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 214, 10))
    $edge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 0, 72, 200), [Math]::Max(1.0, $s * 0.02))
    $g.DrawPolygon($edge, $pts); $g.FillPolygon($star, $pts)
    $g.Dispose(); return $bmp
}

function Get-PngBytes($bmp) { $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); return ,([byte[]]$ms.ToArray()) }

$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = @{}
foreach ($sz in $sizes) { $b = New-Logo $sz; $frames[$sz] = Get-PngBytes $b; $b.Dispose() }
$big = New-Logo 512; $big.Save((Join-Path $out 'EtlStudio-512.png')); $big.Dispose()

# ICO con cuadros PNG
$ms = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($sz in $sizes) {
    $bytes = $frames[$sz]; $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$bytes.Length); $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($sz in $sizes) { $bw.Write($frames[$sz]) }
[System.IO.File]::WriteAllBytes((Join-Path $out 'EtlStudio.ico'), $ms.ToArray())
"Icono generado: " + (Join-Path $out 'EtlStudio.ico')
