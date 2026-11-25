# Mean absolute difference between two same-sized images (0-255 per channel), and the share of pixels that differ by
# more than 8 in any channel: the lab's noise floor (wind, birds) against a real change.
#   powershell -File imgdiff.ps1 <a> <b>
param([string]$A, [string]$B)
Add-Type -AssemblyName PresentationCore
Add-Type -TypeDefinition @"
public static class RlDiff
{
    public static string Compare(byte[] a, byte[] b)
    {
        double sum = 0; long big = 0, n = a.Length / 4;
        for (long i = 0; i < n; i++)
        {
            int m = 0;
            for (int c = 0; c < 3; c++) { int d = a[i * 4 + c] - b[i * 4 + c]; if (d < 0) d = -d; sum += d; if (d > m) m = d; }
            if (m > 8) big++;
        }
        return string.Format("mean abs diff {0:0.00} / 255, pixels differing by more than 8: {1:0.00} %", sum / (n * 3), 100.0 * big / n);
    }
}
"@
function Load([string]$path) {
    $s = [System.IO.File]::OpenRead($path)
    $d = [System.Windows.Media.Imaging.BitmapDecoder]::Create($s, [System.Windows.Media.Imaging.BitmapCreateOptions]::None, [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    $f = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($d.Frames[0], [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $px = New-Object byte[] ($f.PixelWidth * $f.PixelHeight * 4)
    $f.CopyPixels($px, $f.PixelWidth * 4, 0)
    $s.Close()
    return ,$px
}
$pa = Load $A; $pb = Load $B
if ($pa.Length -ne $pb.Length) { "different sizes"; exit 1 }
[RlDiff]::Compare($pa, $pb)
