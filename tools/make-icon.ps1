# Generates src\icon.ico (PNG-compressed entries 16..256 px).
param([string]$Out = (Join-Path $PSScriptRoot '..\src\icon.ico'))
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = [single]($s * 0.2)
    $w = [single]($s - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r, $r, 180, 90)
    $path.AddArc($w - $r, 0, $r, $r, 270, 90)
    $path.AddArc($w - $r, $w - $r, $r, $r, 0, 90)
    $path.AddArc(0, $w - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 0, $s), ([System.Drawing.Color]::FromArgb(255, 72, 160, 255)), ([System.Drawing.Color]::FromArgb(255, 30, 90, 200))
    $g.FillPath($bg, $path)
    $g.SetClip($path)

    $sun = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 214, 90))
    $g.FillEllipse($sun, [single]($s * 0.62), [single]($s * 0.16), [single]($s * 0.2), [single]($s * 0.2))

    $back = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 255, 255, 255))
    $g.FillPolygon($back, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($s * 0.38), ($s * 1.0)),
        (New-Object System.Drawing.PointF ($s * 0.68), ($s * 0.44)),
        (New-Object System.Drawing.PointF ($s * 1.05), ($s * 1.0))))
    $front = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillPolygon($front, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF (-$s * 0.05), ($s * 1.0)),
        (New-Object System.Drawing.PointF ($s * 0.33), ($s * 0.38)),
        (New-Object System.Drawing.PointF ($s * 0.72), ($s * 1.0))))
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$pngs = $sizes | ForEach-Object { ,(New-IconPng $_) }

$fs = [System.IO.File]::Create([System.IO.Path]::GetFullPath($Out))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $b = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Host "icon -> $Out"
