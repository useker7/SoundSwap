# Generates PNGs and a multi-size ICO of the logo (same geometry as assets/logo.svg).
#   powershell -ExecutionPolicy Bypass -File assets\make-icons.ps1
Add-Type -AssemblyName System.Drawing
$out = $PSScriptRoot
$bars = @(@(8, 12, 20), @(12, 8, 24), @(16, 10, 22), @(20, 7, 25), @(24, 12, 20))

function Render([int]$size, [bool]$square) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $k = $size / 32.0
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x25, 0x63, 0xEB))
    if ($square) { $g.FillRectangle($blue, 0, 0, $size, $size) }
    else { $g.FillEllipse($blue, 1 * $k, 1 * $k, 30 * $k, 30 * $k) }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), (2.6 * $k)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    foreach ($b in $bars) { $g.DrawLine($pen, $b[0] * $k, $b[1] * $k, $b[0] * $k, $b[2] * $k) }
    $g.Dispose()
    return $bmp
}

function PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

# Windows ICO (entries with embedded PNGs)
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { , (PngBytes (Render $s $false)) }
$fs = [System.IO.File]::Create((Join-Path $out "SoundSwap.ico"))
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()

# For the mobile app icons: a circle (round) and a full square (iOS rounds it itself)
(Render 1024 $false).Save((Join-Path $out "logo-1024.png"), [System.Drawing.Imaging.ImageFormat]::Png)
(Render 1024 $true).Save((Join-Path $out "logo-square-1024.png"), [System.Drawing.Imaging.ImageFormat]::Png)

# iOS app icon: must not have transparency (alpha channel)
$sq = Render 1024 $true
$rgb = New-Object System.Drawing.Bitmap 1024, 1024, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g = [System.Drawing.Graphics]::FromImage($rgb); $g.DrawImage($sq, 0, 0, 1024, 1024); $g.Dispose()
$iosDir = Join-Path $out "..\ios\SoundSwap\Assets.xcassets\AppIcon.appiconset"
New-Item -ItemType Directory -Force $iosDir | Out-Null
$rgb.Save((Join-Path $iosDir "icon-1024.png"), [System.Drawing.Imaging.ImageFormat]::Png)
Get-ChildItem $out -File | ForEach-Object { "{0,-24} {1,8:N0} B" -f $_.Name, $_.Length }
