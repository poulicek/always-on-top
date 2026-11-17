# Generates app.ico (Start / Task Manager / shortcut icon) with PNG-compressed entries.
# Charcoal rounded tile with a white filled pin taken from
# the Windows icon font (Segoe Fluent Icons E840 + E841) and a thin accent-blue frame hint behind it.
# Usage: powershell -File tools\Make-Icon.ps1 [-Out ..\app.ico] [-PreviewPng tools\icon-preview.png]
param(
    [string]$Out = (Join-Path $PSScriptRoot "..\app.ico"),
    [string]$PreviewPng = (Join-Path $PSScriptRoot "icon-preview.png")
)

Add-Type -AssemblyName System.Drawing

function C([string]$hex, [int]$a = 255) {
    $hex = $hex.TrimStart('#')
    return [System.Drawing.Color]::FromArgb($a, [Convert]::ToInt32($hex.Substring(0, 2), 16),
        [Convert]::ToInt32($hex.Substring(2, 2), 16), [Convert]::ToInt32($hex.Substring(4, 2), 16))
}

function PF([float]$x, [float]$y) { return New-Object System.Drawing.PointF -ArgumentList $x, $y }

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$fontName = 'Segoe Fluent Icons'
if (-not ((New-Object System.Drawing.Text.InstalledFontCollection).Families | Where-Object Name -eq $fontName)) { $fontName = 'Segoe MDL2 Assets' }

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$size
    $u = $s / 256.0

    # tile
    $pad = 8 * $u
    $bg = New-RoundedPath $pad $pad ($s - 2 * $pad) ($s - 2 * $pad) (56 * $u)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush((PF 0 0), (PF $s $s), (C '#2E3138'), (C '#15171B'))
    $g.FillPath($grad, $bg)
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush((PF 0 $pad), (PF 0 ($s * 0.55)), (C '#FFFFFF' 55), (C '#FFFFFF' 0))
    $g.FillPath($hl, $bg)
    $edgePen = New-Object System.Drawing.Pen (C '#FFFFFF' 40), ([Math]::Max(1.0, 2 * $u))
    $g.DrawPath($edgePen, $bg)

    # accent window frame (the border the app draws around pinned windows)
    if ($size -ge 32) {
        $frame = New-RoundedPath (52 * $u) (60 * $u) (152 * $u) (136 * $u) (16 * $u)
        $framePen = New-Object System.Drawing.Pen (C '#3A96DD'), ([Math]::Max(1.5, 10 * $u))
        $g.DrawPath($framePen, $frame)
    }

    # pin glyph
    $em = 150 * $u
    if ($size -lt 32) { $em = 200 * $u }
    $font = New-Object System.Drawing.Font $fontName, $em, ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat ([System.Drawing.StringFormat]::GenericTypographic)
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $box = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $white = New-Object System.Drawing.SolidBrush (C '#FFFFFF')
    $g.DrawString([string][char]0xE841, $font, $white, $box, $fmt)
    $g.DrawString([string][char]0xE840, $font, $white, $box, $fmt)

    $g.Dispose()
    return $bmp
}

$sizes = @(256, 64, 48, 32, 24, 16)
$pngs = @()
foreach ($sz in $sizes) {
    $bmp = Draw-Icon $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@{ Size = $sz; Bytes = $ms.ToArray() }
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$p.Bytes.Length); $bw.Write([uint32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $bw.Write($p.Bytes) }
$bw.Flush(); $fs.Close()

# preview sheet
$sheet = New-Object System.Drawing.Bitmap 420, 300
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear((C '#F3F3F3'))
$x = 10
foreach ($sz in @(256, 48, 32, 16)) {
    $b = Draw-Icon $sz
    $g.DrawImage($b, $x, 20, $sz, $sz)
    $x += $sz + 12
    $b.Dispose()
}
$sheet.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Wrote $Out and $PreviewPng"
