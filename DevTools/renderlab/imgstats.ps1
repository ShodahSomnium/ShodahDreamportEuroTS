# Region statistics of an image (PNG or WebP, via WPF): mean sRGB, mean luma (Rec.709 on sRGB values, 0-255),
# luma standard deviation, mean HSV saturation (0-1), and the linear-light mean converted back to sRGB.
#   powershell -File imgstats.ps1 <image> "name:x0,y0,x1,y1" ["name:x0,y0,x1,y1" ...]
param([string]$Image, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Regions)
Add-Type -AssemblyName PresentationCore
Add-Type -TypeDefinition @"
using System;
public static class RlStats
{
    static double Lin(double c) { c /= 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
    static double Srgb(double l) { return 255.0 * (l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055); }
    public static string Region(byte[] px, int stride, int x0, int y0, int x1, int y1)
    {
        double r = 0, g = 0, b = 0, y = 0, yy = 0, s = 0, lr = 0, lg = 0, lb = 0; long n = 0;
        for (int j = y0; j < y1; j++)
            for (int i = x0; i < x1; i++)
            {
                int o = j * stride + i * 4;
                double B = px[o], G = px[o + 1], R = px[o + 2];
                double L = 0.2126 * R + 0.7152 * G + 0.0722 * B;
                double mx = Math.Max(R, Math.Max(G, B)), mn = Math.Min(R, Math.Min(G, B));
                r += R; g += G; b += B; y += L; yy += L * L; s += mx > 0 ? (mx - mn) / mx : 0;
                lr += Lin(R); lg += Lin(G); lb += Lin(B); n++;
            }
        double my = y / n;
        return string.Format("mean sRGB {0:0},{1:0},{2:0}  luma {3:0.0} sd {4:0.0}  sat {5:0.00}  (linear mean as sRGB {6:0},{7:0},{8:0})",
            r / n, g / n, b / n, my, Math.Sqrt(Math.Max(0, yy / n - my * my)), s / n, Srgb(lr / n), Srgb(lg / n), Srgb(lb / n));
    }
}
"@
$s = [System.IO.File]::OpenRead($Image)
$d = [System.Windows.Media.Imaging.BitmapDecoder]::Create($s, [System.Windows.Media.Imaging.BitmapCreateOptions]::None, [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
$f = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($d.Frames[0], [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
$w = $f.PixelWidth; $h = $f.PixelHeight; $stride = $w * 4
$px = New-Object byte[] ($stride * $h)
$f.CopyPixels($px, $stride, 0)
$s.Close()
"$([System.IO.Path]::GetFileName($Image)) ${w}x${h}"
foreach ($r in $Regions) {
    $name, $rect = $r.Split(':')
    $c = $rect.Split(',') | ForEach-Object { [int]$_ }
    "  {0,-14} {1}" -f $name, [RlStats]::Region($px, $stride, $c[0], $c[1], $c[2], $c[3])
}
