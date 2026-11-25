# Crops a region of an image (PNG or WebP) and saves it as PNG, optionally scaled (nearest neighbour).
#   powershell -File imgcrop.ps1 <in> <out.png> x y w h [scale]
param([string]$In, [string]$Out, [int]$X, [int]$Y, [int]$W, [int]$H, [double]$Scale = 1)
Add-Type -AssemblyName PresentationCore
$s = [System.IO.File]::OpenRead($In)
$d = [System.Windows.Media.Imaging.BitmapDecoder]::Create($s, [System.Windows.Media.Imaging.BitmapCreateOptions]::None, [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
$c = New-Object System.Windows.Media.Imaging.CroppedBitmap($d.Frames[0], (New-Object System.Windows.Int32Rect($X, $Y, $W, $H)))
$src = $c
if ($Scale -ne 1) {
    $t = New-Object System.Windows.Media.ScaleTransform($Scale, $Scale)
    $tb = New-Object System.Windows.Media.Imaging.TransformedBitmap($c, $t)
    $src = $tb
}
$e = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$e.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($src))
$o = [System.IO.File]::Create($Out)
$e.Save($o)
$o.Close(); $s.Close()
"wrote $Out"
