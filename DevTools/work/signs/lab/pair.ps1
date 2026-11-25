# pair.ps1 <dir> <out.png> <name1> [name2 ...] : before_shot_<n> | after2_shot_<n> rows, each 640x360
param([string]$dir, [string]$out, [Parameter(ValueFromRemainingArguments=$true)][string[]]$names)
Add-Type -AssemblyName System.Drawing
$o = New-Object System.Drawing.Bitmap 1280, (360 * $names.Count)
$g = [System.Drawing.Graphics]::FromImage($o)
$font = New-Object System.Drawing.Font "Arial", 14
for ($i = 0; $i -lt $names.Count; $i++) {
  $k = 0
  foreach ($p in @("before", "after2")) {
    $f = Join-Path $dir ($p + "_shot_" + $names[$i] + ".png")
    if (Test-Path $f) { $im = [System.Drawing.Image]::FromFile($f); $g.DrawImage($im, $k * 640, $i * 360, 640, 360); $im.Dispose() }
    $g.DrawString($p.Replace("after2","after") + " " + $names[$i], $font, [System.Drawing.Brushes]::Yellow, $k * 640 + 6, $i * 360 + 6)
    $k++
  }
}
$o.Save($out)
