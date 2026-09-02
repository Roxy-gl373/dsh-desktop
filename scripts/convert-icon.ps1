# convert-icon.ps1
# Converts any PNG/JPEG source into a multi-size Windows .ico (PNG-compressed entries).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File convert-icon.ps1 -Source <png> -Out <ico>
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $Source)) { throw "Source image not found: $Source" }

# Sizes stored in the .ico (Windows uses 0 to mean 256)
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngBuffers = New-Object System.Collections.Generic.List[byte[]]

$srcImg = [System.Drawing.Image]::FromFile($Source)
try {
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap -ArgumentList $s, $s
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.Clear([System.Drawing.Color]::Transparent)
            $g.DrawImage($srcImg, 0, 0, $s, $s)
        } finally { $g.Dispose() }

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngBuffers.Add($ms.ToArray())
        $ms.Dispose()
        $bmp.Dispose()
    }
} finally { $srcImg.Dispose() }

# Build ICO container without BinaryWriter (portable across PS versions)
$count = $sizes.Count
$headerLen = 6
$entryLen = 16
$dataOffset = $headerLen + ($entryLen * $count)

$ms2 = New-Object System.IO.MemoryStream

function Write-U16([System.IO.Stream]$s, [int]$v) {
    $b = [BitConverter]::GetBytes([UInt16]$v); $s.Write($b, 0, $b.Length)
}
function Write-U32([System.IO.Stream]$s, [int]$v) {
    $b = [BitConverter]::GetBytes([UInt32]$v); $s.Write($b, 0, $b.Length)
}

# ICONDIR
Write-U16 $ms2 0          # reserved
Write-U16 $ms2 1          # type = icon
Write-U16 $ms2 $count     # image count

$offset = $dataOffset
for ($i = 0; $i -lt $count; $i++) {
    $s = $sizes[$i]
    $bytes = $pngBuffers[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $ms2.WriteByte([Byte]$dim)            # width (0 => 256)
    $ms2.WriteByte([Byte]$dim)            # height (0 => 256)
    $ms2.WriteByte([Byte]0)               # color count
    $ms2.WriteByte([Byte]0)               # reserved
    Write-U16 $ms2 1                      # planes
    Write-U16 $ms2 32                     # bit count
    Write-U32 $ms2 $bytes.Length          # bytes in resource
    Write-U32 $ms2 $offset                # image offset
    $offset += $bytes.Length
}
foreach ($b in $pngBuffers) { $ms2.Write($b, 0, $b.Length) }

[System.IO.File]::WriteAllBytes($Out, $ms2.ToArray())
$ms2.Dispose()
Write-Output "Wrote $Out ($([System.IO.File]::ReadAllBytes($Out).Length) bytes, $count sizes)"
