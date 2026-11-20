# Renders Forge.App/Assets/forge.ico (16-256 px PNG frames) with the same mark
# the tray icon draws at runtime (Forge.App/Tray/IconFactory.cs). Windows PowerShell 5.1.
# The mark ("Stack"): three server slabs stepping down into an F on an ink tile, on a 32 px grid.
# From 24 px up the top slab gets an ember status light and the middle one a dark, unlit one.
param([string]$Out = (Join-Path $PSScriptRoot '..\Forge.App\Assets\forge.ico'))
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
  $d = 2 * $r
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc($x, $y, $d, $d, 180, 90)
  $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
  $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $path.CloseFigure()
  return ,$path
}

function New-Frame([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::Transparent)
  $s = $size / 32.0
  $ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 18, 23, 34))
  $steel = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 245, 247, 250))
  $ember = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 107, 44))
  $g.FillPath($ink, (New-RoundedRect (1*$s) (1*$s) (30*$s) (30*$s) (7*$s)))
  $g.FillPath($steel, (New-RoundedRect (8*$s) (8*$s) (16*$s) (4*$s) (1.5*$s)))
  $g.FillPath($steel, (New-RoundedRect (8*$s) (14*$s) (12*$s) (4*$s) (1.5*$s)))
  $g.FillPath($steel, (New-RoundedRect (8*$s) (20*$s) (6*$s) (4*$s) (1.5*$s)))
  if ($size -ge 24) {
    $g.FillEllipse($ember, 20.5*$s, 8.75*$s, 2.5*$s, 2.5*$s)
    $g.FillEllipse($ink, 16.5*$s, 14.75*$s, 2.5*$s, 2.5*$s)
  }
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 48, 64, 256
$frames = $sizes | ForEach-Object { ,(New-Frame $_) }
$fs = [System.IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $Out)).Path + '\' + (Split-Path $Out -Leaf))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
  $bw.Write([Byte]$dim); $bw.Write([Byte]$dim); $bw.Write([Byte]0); $bw.Write([Byte]0)
  $bw.Write([UInt16]1); $bw.Write([UInt16]32)
  $bw.Write([UInt32]$frames[$i].Length); $bw.Write([UInt32]$offset)
  $offset += $frames[$i].Length
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Dispose()
Write-Output "Wrote $Out"
