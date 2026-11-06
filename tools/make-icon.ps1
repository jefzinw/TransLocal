# Gera assets\TransLocal.ico (16..256 px) desenhando o ícone com System.Drawing.
# Uso: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
param([string]$Out = (Join-Path $PSScriptRoot '..\assets\TransLocal.ico'))

Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Fundo: quadrado arredondado com degradê azul
    $bg = New-RoundedPath 0 0 ($s - 1) ($s - 1) ([Math]::Max(2, $s * 0.22))
    $rect = New-Object System.Drawing.RectangleF(0, 0, $s, $s)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
        [System.Drawing.Color]::FromArgb(255, 18, 132, 255),
        [System.Drawing.Color]::FromArgb(255, 88, 60, 220), 45.0)
    $g.FillPath($brush, $bg)

    # Monitor (contorno branco) + base
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [Math]::Max(1.4, $s * 0.075))
    $pen.LineJoin = 'Round'
    $mx = $s * 0.20; $my = $s * 0.20; $mw = $s * 0.60; $mh = $s * 0.42
    $mon = New-RoundedPath $mx $my $mw $mh ([Math]::Max(1, $s * 0.05))
    $g.DrawPath($pen, $mon)
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillRectangle($white, $s * 0.46, $my + $mh, $s * 0.08, $s * 0.12)
    $g.FillRectangle($white, $s * 0.33, $my + $mh + $s * 0.11, $s * 0.34, [Math]::Max(1.2, $s * 0.06))

    # Seta de transmissão dentro da tela
    $cx = $s * 0.50; $cy = $my + $mh / 2
    $a = $s * 0.13
    $pts = @(
        (New-Object System.Drawing.PointF(($cx - $a * 0.8), ($cy - $a))),
        (New-Object System.Drawing.PointF(($cx + $a * 1.0), $cy)),
        (New-Object System.Drawing.PointF(($cx - $a * 0.8), ($cy + $a)))
    )
    $g.FillPolygon($white, $pts)

    $g.Dispose()
    return $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER (altura dobrada: XOR + AND)
    $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $zeros = New-Object byte[] ($maskRow * $s)
    $bw.Write($zeros)
    $bw.Flush()
    # A vírgula impede o PowerShell de "desenrolar" o array de bytes no retorno.
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @()
foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz
    if ($sz -ge 128) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += , $ms.ToArray()
    } else {
        $images += , (Get-DibBytes $bmp)
    }
    $bmp.Dispose()
}

$icoStream = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($icoStream)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $images[$i].Length
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$len); $w.Write([int]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Flush()
$full = [System.IO.Path]::GetFullPath($Out)
[System.IO.File]::WriteAllBytes($full, $icoStream.ToArray())
Write-Output "Icone gerado: $full ($($icoStream.Length) bytes)"
