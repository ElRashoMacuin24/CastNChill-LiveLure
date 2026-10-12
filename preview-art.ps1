# Renders the HUD pixel art in LiveLure\HudArt.cs to a PNG, so the art can be checked without launching the game.
# Draws the dial with the handle on it at a few rod angles, plus the loose sprites, at the in-game pixel size.
#
#   .\mod-src\preview-art.ps1 [-Out path.png]
param([string]$Out = (Join-Path $env:TEMP 'livelure-art.png'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$src = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'LiveLure\HudArt.cs'))

function Get-Grid([string]$name) {
    $m = [regex]::Match($src, "string\[\] $name\s*=\s*\{(.*?)\};", 'Singleline')
    if (-not $m.Success) { throw "grid $name not found" }
    $rows = [regex]::Matches($m.Groups[1].Value, '"([^"]*)"') | ForEach-Object { $_.Groups[1].Value }
    $w = $rows[0].Length
    for ($i = 0; $i -lt $rows.Count; $i++) { if ($rows[$i].Length -ne $w) { throw "$name row $i is $($rows[$i].Length) wide, expected $w" } }
    , $rows
}
function Get-Const([string]$name) { [double]([regex]::Match($src, "$name\s*=\s*([\d.]+)f").Groups[1].Value) }

$palette = @{}
foreach ($e in (Get-Grid 'Palette')) {
    $v = [Convert]::ToUInt32($e.Substring(2), 16)
    $palette[[string]$e[0]] = [System.Drawing.Color]::FromArgb($v -band 0xFF, ($v -shr 24) -band 0xFF, ($v -shr 16) -band 0xFF, ($v -shr 8) -band 0xFF)
}
function New-Sprite([string[]]$rows) {
    $bmp = New-Object System.Drawing.Bitmap $rows[0].Length, $rows.Count
    for ($y = 0; $y -lt $rows.Count; $y++) {
        for ($x = 0; $x -lt $rows[$y].Length; $x++) {
            $c = $palette[[string]$rows[$y][$x]]
            if ($null -eq $c) { throw "unknown palette letter '$($rows[$y][$x])'" }
            $bmp.SetPixel($x, $y, $c)
        }
    }
    $bmp
}

# The dial is computed, not drawn: mirror HudArt.DialPixel.
function New-Dial([double]$lowest, [double]$highest, [double]$rest) {
    $n = 48; $bmp = New-Object System.Drawing.Bitmap $n, $n
    for ($y = 0; $y -lt $n; $y++) { for ($x = 0; $x -lt $n; $x++) {
        $dx = $x + 0.5 - $n / 2; $dy = $n / 2 - ($y + 0.5); $r = [Math]::Sqrt($dx * $dx + $dy * $dy)
        $deg = [Math]::Atan2($dx, $dy) * 57.29578
        $c = if ($r -gt 23.2) { @(0, 0, 0, 0) } elseif ($r -gt 22.1) { @(255, 22, 22, 31) } elseif ($r -gt 20.2) { @(255, 92, 104, 148) }
             elseif ($r -gt 19.4) { @(255, 36, 41, 62) }
             elseif ($r -gt 15.2) { if ([Math]::Abs($deg - $rest) -lt 3.2) { @(255, 244, 246, 251) } elseif ($deg -ge $highest -and $deg -le $lowest) { @(240, 104, 196, 210) } else { @(255, 47, 54, 82) } }
             elseif ($r -gt 14.4) { @(255, 36, 41, 62) } else { @(255, 24, 29, 48) }
        $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($c[0], $c[1], $c[2], $c[3]))
    } }
    $bmp
}

$unit = 4
$handle = New-Sprite (Get-Grid 'Handle'); $crank = New-Sprite (Get-Grid 'Crank')
$mouseR = New-Sprite (Get-Grid 'MouseRight'); $mouseL = New-Sprite (Get-Grid 'MouseLeft'); $fish = New-Sprite (Get-Grid 'Fish')
$px = Get-Const 'HandlePivotX'; $py = Get-Const 'HandlePivotY'; $hx = Get-Const 'ReelHubX'; $hy = Get-Const 'ReelHubY'
$cx = Get-Const 'CrankPivotX'; $cy = Get-Const 'CrankPivotY'
$dial = New-Dial 60 -15 40

$angles = @(60, 40, 10, -15)   # degrees from upright toward the water: dipped, at rest, raised, fully raised
$cell = 260
$canvas = New-Object System.Drawing.Bitmap ($cell * ($angles.Count + 1)), ($cell * 2)
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
foreach ($row in 0, 1) {
    $bg = if ($row -eq 0) { [System.Drawing.Color]::FromArgb(255, 62, 96, 128) } else { [System.Drawing.Color]::FromArgb(255, 224, 232, 240) }
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $bg), 0, $row * $cell, $canvas.Width, $cell)
    for ($i = 0; $i -lt $angles.Count; $i++) {
        $ox = $i * $cell + $cell / 2; $oy = $row * $cell + $cell / 2 + 10
        $mirror = if ($row -eq 0) { 1 } else { -1 }      # second row: angler facing the other way
        $state = $g.Save()
        $g.TranslateTransform($ox, $oy); $g.ScaleTransform($mirror * $unit, $unit)
        $g.DrawImage($dial, -24, -24, 48, 48)
        $g.RotateTransform($angles[$i])
        $g.DrawImage($handle, [float](-$px), [float](-$py), $handle.Width, $handle.Height)
        $g.TranslateTransform([float]($hx - $px), [float]($hy - $py)); $g.RotateTransform(-35 + $i * 70)
        $g.DrawImage($crank, [float](-$cx), [float](-$cy), $crank.Width, $crank.Height)
        $g.Restore($state)
    }
    $ox = $angles.Count * $cell + 40; $oy = $row * $cell + 60
    $g.DrawImage($mouseR, $ox, $oy, $mouseR.Width * $unit, $mouseR.Height * $unit)
    $g.DrawImage($mouseL, $ox + 60, $oy, $mouseL.Width * $unit, $mouseL.Height * $unit)
    $g.DrawImage($handle, $ox + 130, $oy - 30, $handle.Width * $unit, $handle.Height * $unit)
    $g.DrawImage($fish, $ox, $oy + 80, $fish.Width * 3, $fish.Height * 3)
}
$g.Dispose()
$canvas.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
"wrote $Out ($($canvas.Width)x$($canvas.Height))"
