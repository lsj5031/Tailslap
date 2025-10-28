#!/usr/bin/env powershell
param(
    [string]$IconsDir = (Join-Path $PSScriptRoot "..\Icons"),
    [string[]]$Files = @("Chewing1.ico","Chewing2.ico","Chewing3.ico","Chewing4.ico"),
    [int]$Tolerance = 8
)

Add-Type -AssemblyName System.Drawing

function Test-ColorsClose([System.Drawing.Color]$c1, [System.Drawing.Color]$c2, [int]$tol) {
    return ([math]::Abs($c1.R - $c2.R) -le $tol -and 
            [math]::Abs($c1.G - $c2.G) -le $tol -and 
            [math]::Abs($c1.B - $c2.B) -le $tol)
}

foreach ($name in $Files) {
    $inPath = Join-Path $IconsDir $name
    if (-not (Test-Path $inPath)) { Write-Host "Skip missing $inPath"; continue }

    try {
        $icon = New-Object System.Drawing.Icon($inPath)
    } catch {
        Write-Warning "Failed to load $inPath as icon: $_"; continue
    }

    $srcBmp = $icon.ToBitmap()
    $w = $srcBmp.Width; $h = $srcBmp.Height

    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($srcBmp, 0, 0, $w, $h)
    $g.Dispose()

    $samples = @(
        $bmp.GetPixel(0,0),
        $bmp.GetPixel($w-1,0),
        $bmp.GetPixel(0,$h-1),
        $bmp.GetPixel($w-1,$h-1)
    )
    $bg = $samples | Group-Object { $_.ToArgb() } | Sort-Object Count -Descending | Select-Object -First 1 | ForEach-Object { [System.Drawing.Color]::FromArgb([int]$_.Name) }

    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $p = $bmp.GetPixel($x, $y)
            if (Test-ColorsClose $p $bg $Tolerance) {
                $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(0, $p.R, $p.G, $p.B))
            }
        }
    }

    try { Copy-Item $inPath "$inPath.bak" -Force } catch { }

    $hicon = $bmp.GetHicon()
    try {
        $outIcon = [System.Drawing.Icon]::FromHandle($hicon)
        $fs = [System.IO.File]::Create($inPath)
        $outIcon.Save($fs)
        $fs.Close()
    } finally { $icon.Dispose(); $srcBmp.Dispose(); $bmp.Dispose() }

    Write-Host "Processed $inPath (bg=$($bg.ToString()))"
}

Write-Host "Done. If tray still shows halo, try increasing -Tolerance (e.g., -Tolerance 16)."
