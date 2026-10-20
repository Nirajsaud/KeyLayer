param([string]$Out = "$PSScriptRoot\art", [string]$Logo = "$PSScriptRoot\..\assets\logo.png", [switch]$Preview)
# Draws the installer's artwork from the logo: the tall picture on the welcome and finish pages (in the sizes Inno
# Setup picks from for each display scaling), the small logo in the corner of the other pages, and the logo shown when
# uninstalling. Run with Windows PowerShell; -Preview also writes preview.png to look at.
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory $Out -Force | Out-Null
$mark = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Logo))

$accent = [System.Drawing.Color]::FromArgb(74, 222, 128)  # green, like the app's highlights
$top = [System.Drawing.Color]::FromArgb(17, 19, 26)
$bottom = [System.Drawing.Color]::FromArgb(9, 44, 33)

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}

function Graphics($bmp) {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g
}

# The logo, cropped around the mark, inside a rounded square.
function DrawLogo($g, [float]$x, [float]$y, [float]$size, [int]$crop) {
    $src = New-Object System.Drawing.Rectangle((518 - $crop / 2), (499 - $crop / 2), $crop, $crop)
    $path = RoundRect $x $y $size $size ($size * 0.22)
    $state = $g.Save(); $g.SetClip($path)
    $g.DrawImage($mark, (New-Object System.Drawing.RectangleF($x, $y, $size, $size)), $src, 'Pixel')
    $g.Restore($state)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(60, 255, 255, 255)), ([Math]::Max(1, $size / 64))
    $g.DrawPath($pen, $path)
}

function SaveBmp($bmp, [string]$name) {
    # 24-bit BMP: what every Inno Setup version accepts
    $flat = New-Object System.Drawing.Bitmap $bmp.Width, $bmp.Height, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($flat); $g.DrawImage($bmp, 0, 0, $bmp.Width, $bmp.Height); $g.Dispose()
    $flat.Save((Join-Path $Out $name), [System.Drawing.Imaging.ImageFormat]::Bmp); $flat.Dispose()
}

# ---- welcome / finish picture
function Wizard([int]$w, [int]$h) {
    $s = $w / 164.0
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = Graphics $bmp
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle(0, 0, $w, $h)), $top, $bottom, 90
    $g.FillRectangle($bg, 0, 0, $w, $h)

    # A field of keycaps, a few of them lit up
    $key = 26 * $s; $gap = 6 * $s; $lit = @{ '1,1' = 'Fn'; '3,2' = 'K'; '2,4' = 'L'; '4,0' = 'Ctrl' }
    $font = New-Object System.Drawing.Font 'Segoe UI Semibold', (8 * $s), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    for ($row = 0; $row -lt 7; $row++) {
        for ($col = -1; $col -lt 6; $col++) {
            $x = $col * ($key + $gap) + ($row % 2) * ($key / 2) - 4 * $s
            $y = 172 * $s + $row * ($key + $gap)
            $r = RoundRect $x $y $key $key (5 * $s)
            $label = $lit["$row,$col"]
            if ($label) {
                for ($i = 3; $i -ge 1; $i--) {  # soft glow
                    $glow = RoundRect ($x - $i * 2 * $s) ($y - $i * 2 * $s) ($key + $i * 4 * $s) ($key + $i * 4 * $s) (7 * $s)
                    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(18, $accent))), $glow)
                }
                $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, $accent))), $r)
                $g.DrawPath((New-Object System.Drawing.Pen $accent, (1.2 * $s)), $r)
                $g.DrawString($label, $font, (New-Object System.Drawing.SolidBrush $accent), (New-Object System.Drawing.RectangleF($x, $y, $key, $key)), $fmt)
            }
            else {
                $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(12, 255, 255, 255))), $r)
                $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(34, 255, 255, 255)), (1 * $s)), $r)
            }
        }
    }
    # Fade the keys into the background at the top of the field (in the background's own colour there) and at the bottom
    $t = (166 * $s) / $h
    $here = [System.Drawing.Color]::FromArgb([int]($top.R + ($bottom.R - $top.R) * $t), [int]($top.G + ($bottom.G - $top.G) * $t), [int]($top.B + ($bottom.B - $top.B) * $t))
    $fadeTop = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF(0, (166 * $s), $w, (30 * $s))), $here, ([System.Drawing.Color]::FromArgb(0, $here)), 90
    $g.FillRectangle($fadeTop, 0, 166 * $s, $w, 30 * $s)
    $fadeBottom = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF(0, ($h - 70 * $s), $w, (70 * $s))), ([System.Drawing.Color]::FromArgb(0, $bottom)), $bottom, 90
    $g.FillRectangle($fadeBottom, 0, $h - 70 * $s, $w, 70 * $s + 1)

    # Logo with a soft glow, the name and a line
    $size = 64 * $s; $lx = ($w - $size) / 2; $ly = 34 * $s
    for ($i = 5; $i -ge 1; $i--) {
        $glow = RoundRect ($lx - $i * 3 * $s) ($ly - $i * 3 * $s) ($size + $i * 6 * $s) ($size + $i * 6 * $s) ($size * 0.3)
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(10, $accent))), $glow)
    }
    DrawLogo $g $lx $ly $size 760
    $title = New-Object System.Drawing.Font 'Segoe UI Semibold', (19 * $s), ([System.Drawing.GraphicsUnit]::Pixel)
    $g.DrawString('KeyLayer', $title, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF(0, (108 * $s), $w, (28 * $s))), $fmt)
    $tag = New-Object System.Drawing.Font 'Segoe UI', (9.5 * $s), ([System.Drawing.GraphicsUnit]::Pixel)
    $g.DrawString('Your keys. New jobs.', $tag, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(170, 190, 200, 195))), (New-Object System.Drawing.RectangleF(0, (136 * $s), $w, (16 * $s))), $fmt)
    $g.Dispose()
    $bmp
}

# ---- small logo for the top-right corner of the other pages (they have a white header)
function Small([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = Graphics $bmp
    $g.Clear([System.Drawing.Color]::White)
    $size = [Math]::Min($w, $h) * 0.86
    DrawLogo $g (($w - $size) / 2) (($h - $size) / 2) $size 680
    $g.Dispose()
    $bmp
}

$wizardSizes = @(@(164, 314), @(192, 386), @(246, 459), @(273, 522), @(328, 628), @(410, 785))
$smallSizes = @(@(55, 55), @(64, 68), @(83, 80), @(92, 97), @(110, 106), @(138, 140))
foreach ($z in $wizardSizes) { $b = Wizard $z[0] $z[1]; SaveBmp $b "wizard-$($z[0]).bmp"; if ($Preview -and $z[0] -eq 328) { $b.Save((Join-Path $Out 'preview-wizard.png')) }; $b.Dispose() }
foreach ($z in $smallSizes) { $b = Small $z[0] $z[1]; SaveBmp $b "small-$($z[0]).bmp"; $b.Dispose() }

# Logo for the goodbye window when uninstalling (installed next to KeyLayer.exe so the uninstaller can show it)
$b = New-Object System.Drawing.Bitmap 128, 128
$g = Graphics $b; $g.Clear([System.Drawing.Color]::White); DrawLogo $g 4 4 120 680; $g.Dispose()
SaveBmp $b 'goodbye-logo.bmp'; $b.Dispose()
$mark.Dispose()
"artwork written to $Out"
