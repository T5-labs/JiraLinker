<#
.SYNOPSIS
  Converts a source PNG into a multi-resolution Windows .ico (app + tray icon).
.DESCRIPTION
  Produces a PNG-compressed .ico containing 16/24/32/48/64/128/256 px images,
  which Windows uses to pick the crispest size for each context.
.EXAMPLE
  .\scripts\make-icon.ps1 -Source .\assets\icon-source.png -Out .\assets\app.ico
#>
param(
    [string]$Source = "$PSScriptRoot\..\assets\icon-source.png",
    [string]$Out    = "$PSScriptRoot\..\assets\app.ico"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $Source)) { throw "Source image not found: $Source" }

# Read via bytes/stream (works over UNC paths like \\wsl$\ that Image.FromFile rejects).
$srcBytes = [System.IO.File]::ReadAllBytes((Convert-Path $Source))
$srcStream = New-Object System.IO.MemoryStream (,$srcBytes)
$src = [System.Drawing.Image]::FromStream($srcStream)
try {
    $sizes = 16, 24, 32, 48, 64, 128, 256
    $images = @()
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($src, 0, 0, $s, $s)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $images += , @($s, $ms.ToArray())
    }
} finally { $src.Dispose() }

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter -ArgumentList $ms
try {
    # ICONDIR header
    $bw.Write([uint16]0)               # reserved
    $bw.Write([uint16]1)               # type = icon
    $bw.Write([uint16]$images.Count)   # image count

    # ICONDIRENTRY for each image
    $offset = 6 + (16 * $images.Count)
    foreach ($img in $images) {
        $size = [int]$img[0]; $data = [byte[]]$img[1]
        $dim = [byte]($(if ($size -ge 256) { 0 } else { $size }))
        $bw.Write($dim)                # width  (0 = 256)
        $bw.Write($dim)                # height (0 = 256)
        $bw.Write([byte]0)             # palette count
        $bw.Write([byte]0)             # reserved
        $bw.Write([uint16]1)           # color planes
        $bw.Write([uint16]32)          # bits per pixel
        $bw.Write([uint32]$data.Length)
        $bw.Write([uint32]$offset)
        $offset += $data.Length
    }
    # Image data (PNG-encoded)
    foreach ($img in $images) { $bw.Write([byte[]]$img[1]) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($Out, $ms.ToArray())
} finally { $bw.Dispose(); $ms.Dispose() }

Write-Host "Wrote $Out ($($images.Count) sizes)" -ForegroundColor Green
