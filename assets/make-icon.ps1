# Génère l'icône de Bébène : le chat en pixel art (grille 32×32, fond transparent),
# même style que le chat du bureau : pelage noir, contour bleuté, longues oreilles dressées, yeux verts, collier orange + grelot.
# Sortie : assets\icone-bebene.png (1024 px = chaque pixel du dessin agrandi ×32, sans lissage)
Add-Type -AssemblyName System.Drawing
function C($r, $g, $b) { [Drawing.Color]::FromArgb(255, $r, $g, $b) }
function P($x, $y) { New-Object Drawing.PointF $x, $y }
$N = 32
$bmp = New-Object Drawing.Bitmap $N, $N
$g = [Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'None'; $g.PixelOffsetMode = 'Half'; $g.Clear([Drawing.Color]::Transparent)

$FUR = C 21 21 29; $INNER = C 112 58 80; $GREEN = C 96 214 128; $GREENDK = C 46 140 64
$PUPIL = C 8 8 12; $NOSE = C 236 140 160; $COLLAR = C 217 96 70; $BELL = C 250 210 90; $WHISK = C 118 120 150
$furB = New-Object Drawing.SolidBrush $FUR   # (PowerShell ignore la casse : $fur écraserait $FUR)

# corps (épaules) puis tête
$g.FillEllipse($furB, 7, 22, 18, 14)
# longues oreilles dressées
$g.FillPolygon($furB, [Drawing.PointF[]]@((P 5 15), (P 7 0), (P 14 9)))
$g.FillPolygon($furB, [Drawing.PointF[]]@((P 27 15), (P 25 0), (P 18 9)))
$g.FillEllipse($furB, 5, 7, 22, 18)
$g.FillEllipse($furB, 4, 15, 6, 6); $g.FillEllipse($furB, 22, 15, 6, 6)          # joues
$innerB = New-Object Drawing.SolidBrush $INNER
$g.FillPolygon($innerB, [Drawing.PointF[]]@((P 8 11), (P 8 3), (P 12 9)))
$g.FillPolygon($innerB, [Drawing.PointF[]]@((P 24 11), (P 24 3), (P 20 9)))
$g.Dispose()

# contour d'un pixel (bleuté) + reflet sur le dessus du pelage, comme le sprite du bureau
$OUT = C 52 54 86; $HI = C 62 64 92
$w = $N; $px = New-Object 'int[]' ($N * $N)
for ($y = 0; $y -lt $N; $y++) { for ($x = 0; $x -lt $N; $x++) { $px[$y * $N + $x] = $bmp.GetPixel($x, $y).ToArgb() } }
$isFur = { param($v) $v -eq $FUR.ToArgb() -or $v -eq $INNER.ToArgb() }
for ($y = 0; $y -lt $N; $y++) { for ($x = 0; $x -lt $N; $x++) {
    $v = $px[$y * $N + $x]
    if ((($v -shr 24) -band 0xFF) -eq 0) {
        $near = ($x -gt 0 -and (& $isFur $px[$y * $N + $x - 1])) -or ($x -lt $N - 1 -and (& $isFur $px[$y * $N + $x + 1])) -or
                ($y -gt 0 -and (& $isFur $px[($y - 1) * $N + $x])) -or ($y -lt $N - 1 -and (& $isFur $px[($y + 1) * $N + $x]))
        if ($near) { $bmp.SetPixel($x, $y, $OUT) }
    } elseif ($v -eq $FUR.ToArgb() -and $y -gt 0 -and (($px[($y - 1) * $N + $x] -shr 24) -band 0xFF) -eq 0) { $bmp.SetPixel($x, $y, $HI) }
} }

# visage et collier, pixel par pixel
function Px($x, $y, $c) { $bmp.SetPixel($x, $y, $c) }
foreach ($ex in 9, 19) {                       # yeux 4×5 : vert, bord foncé en haut, pupille fendue, reflet
    for ($yy = 13; $yy -le 17; $yy++) { for ($xx = $ex; $xx -le $ex + 3; $xx++) {
        $corner = ($yy -eq 13 -or $yy -eq 17) -and ($xx -eq $ex -or $xx -eq $ex + 3)
        if (-not $corner) { Px $xx $yy $(if ($yy -eq 13) { $GREENDK } else { $GREEN }) }
    } }
    Px ($ex + 2) 14 $PUPIL; Px ($ex + 2) 15 $PUPIL; Px ($ex + 2) 16 $PUPIL
    Px ($ex + 1) 14 (C 255 255 255)
}
Px 15 19 $NOSE; Px 16 19 $NOSE
foreach ($s in -1, 1) { $x0 = if ($s -lt 0) { 4 } else { 27 }; Px $x0 19 $WHISK; Px ($x0 + $s) 19 $WHISK; Px $x0 21 $WHISK; Px ($x0 + $s) 21 $WHISK }
for ($xx = 10; $xx -le 21; $xx++) { Px $xx 24 $COLLAR; Px $xx 25 $COLLAR }
# médaillon : petit poisson argenté (corps 3×2, œil, queue en V)
$FISH = C 170 205 235; $FIN = C 120 160 205
for ($xx = 14; $xx -le 16; $xx++) { Px $xx 26 $FISH; Px $xx 27 $FISH }
Px 14 26 (C 30 40 60); Px 17 25 $FIN; Px 17 28 $FIN; Px 17 26 $FIN; Px 17 27 $FIN

# agrandissement ×32 sans lissage
$big = New-Object Drawing.Bitmap 1024, 1024
$g2 = [Drawing.Graphics]::FromImage($big); $g2.InterpolationMode = 'NearestNeighbor'; $g2.PixelOffsetMode = 'Half'
$g2.DrawImage($bmp, 0, 0, 1024, 1024); $g2.Dispose()
$out = Join-Path $PSScriptRoot 'icone-bebene.png'
$big.Save($out, [Drawing.Imaging.ImageFormat]::Png); $big.Dispose(); $bmp.Dispose()
"OK $out"
