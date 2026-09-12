<#
.SYNOPSIS
    Draws the WinVitals logo and packs it into a multi-size .ico plus PNGs.

.DESCRIPTION
    The logo is generated, not hand-drawn, so it can be regenerated at any size and
    nobody needs a design tool to change it. A rounded tile with a blue-to-teal
    gradient and a white pulse line: "vitals", readable at 16 pixels.

    Outputs:
      src/WinVitals.App/Assets/winvitals.ico   (256, 128, 64, 48, 32, 24, 16)
      src/WinVitals.App/Assets/logo-64.png     used in the navigation rail
      docs/logo.png                            256 px, for the README
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root   = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'src/WinVitals.App/Assets'
$docs   = Join-Path $root 'docs'
New-Item -ItemType Directory -Force -Path $assets, $docs | Out-Null

function New-RoundedRect([System.Drawing.RectangleF] $r, [float] $radius) {
    $d = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $path.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $path.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-Logo([int] $size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Tile
    $pad  = [float]($size * 0.03)
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)
    $tile = New-RoundedRect $rect ([float]($size * 0.22))
    $top    = [System.Drawing.Color]::FromArgb(255, 0x1E, 0x6F, 0xD9)   # blue
    $bottom = [System.Drawing.Color]::FromArgb(255, 0x12, 0xB5, 0xA4)   # teal
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $top, $bottom, 50.0
    $g.FillPath($brush, $tile)

    # Soft inner highlight along the top edge, for depth at large sizes.
    if ($size -ge 48) {
        $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
            $rect, ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), 90.0
        $g.FillPath($hl, $tile)
    }

    # Pulse line. Fewer points at tiny sizes so it stays a clean zigzag.
    $pts = if ($size -le 24) {
        @( @(0.12, 0.50), @(0.36, 0.50), @(0.46, 0.24), @(0.58, 0.76), @(0.68, 0.50), @(0.88, 0.50) )
    } else {
        @( @(0.12, 0.50), @(0.32, 0.50), @(0.40, 0.30), @(0.50, 0.74), @(0.59, 0.36), @(0.65, 0.50), @(0.88, 0.50) )
    }
    $points = foreach ($p in $pts) { New-Object System.Drawing.PointF ([float]($p[0] * $size)), ([float]($p[1] * $size)) }

    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([float]($size * 0.105))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLines($pen, [System.Drawing.PointF[]] $points)

    $g.Dispose()
    return $bmp
}

function Get-PngBytes([System.Drawing.Bitmap] $bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    # The leading comma stops PowerShell unrolling the byte[] into a stream of
    # single bytes on the way out of the function.
    return ,$ms.ToArray()
}

# --- ICO ---------------------------------------------------------------------
# PNG-compressed entries are valid for every size since Windows Vista, which
# avoids hand-building 32-bit DIBs with AND masks.
$sizes = 256, 128, 64, 48, 32, 24, 16
$entries = New-Object System.Collections.Generic.List[object]
foreach ($s in $sizes) {
    $bmp = Draw-Logo $s
    [byte[]] $bytes = Get-PngBytes $bmp
    $bmp.Dispose()
    $entries.Add([pscustomobject]@{ Size = $s; Bytes = $bytes })
}

$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16] 0)                 # reserved
$w.Write([uint16] 1)                 # type: icon
$w.Write([uint16] $entries.Count)

$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $w.Write([byte] $dim)            # width  (0 means 256)
    $w.Write([byte] $dim)            # height
    $w.Write([byte] 0)               # colour count
    $w.Write([byte] 0)               # reserved
    $w.Write([uint16] 1)             # planes
    $w.Write([uint16] 32)            # bits per pixel
    $w.Write([uint32] $e.Bytes.Length)
    $w.Write([uint32] $offset)
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) {
    [byte[]] $data = $e.Bytes
    $w.Write($data, 0, $data.Length)  # explicit overload: no guessing between byte[] / char[]
}
$w.Flush()

$icoPath = Join-Path $assets 'winvitals.ico'
[System.IO.File]::WriteAllBytes($icoPath, $ico.ToArray())
if ((Get-Item $icoPath).Length -ne $offset) {
    throw "ICO size mismatch: wrote $((Get-Item $icoPath).Length) bytes, directory expects $offset."
}

# --- PNGs --------------------------------------------------------------------
$logo64 = Draw-Logo 64
$logo64.Save((Join-Path $assets 'logo-64.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$logo64.Dispose()

$logo256 = Draw-Logo 256
$logo256.Save((Join-Path $docs 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$logo256.Dispose()

Write-Host "Wrote $icoPath ($([math]::Round((Get-Item $icoPath).Length / 1KB)) KB, $($sizes -join '/') px)"
Write-Host "Wrote $(Join-Path $assets 'logo-64.png') and $(Join-Path $docs 'logo.png')"
