# Renders Forge.App/Assets/forge.ico (16-256 px PNG frames) with the same mark
# the tray icon draws at runtime (Forge.App/Tray/IconFactory.cs). Windows PowerShell 5.1.
param([string]$Out = (Join-Path $PSScriptRoot '..\Forge.App\Assets\forge.ico'))
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::Transparent)
  $s = $size / 32.0
  $r = 7 * $s; $x = 1 * $s; $w = 30 * $s
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc($x, $x, 2*$r, 2*$r, 180, 90)
  $path.AddArc($x + $w - 2*$r, $x, 2*$r, 2*$r, 270, 90)
  $path.AddArc($x + $w - 2*$r, $x + $w - 2*$r, 2*$r, 2*$r, 0, 90)
  $path.AddArc($x, $x + $w - 2*$r, 2*$r, 2*$r, 90, 90)
  $path.CloseFigure()
  $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 36, 52, 71))), $path)
  $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
  $g.FillRectangle($white, 9*$s, 7*$s, 5*$s, 18*$s)
  $g.FillRectangle($white, 9*$s, 7*$s, 14*$s, 5*$s)
  $g.FillRectangle($white, 9*$s, 14*$s, 11*$s, 4*$s)
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
