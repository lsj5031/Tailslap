#!/usr/bin/env pwsh
# Generate tray animation icons: ..\Icons\Chewing1-4.ico
Add-Type -AssemblyName System.Drawing

function Create-Icon {
    param($Path, $Color)
    $bmp = New-Object System.Drawing.Bitmap(32, 32)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $brush = New-Object System.Drawing.SolidBrush($Color)
    $g.FillEllipse($brush, 2, 2, 28, 28)
    $g.Dispose()
    $icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
    $stream = [System.IO.File]::Create($Path)
    $icon.Save($stream)
    $stream.Close()
    $icon.Dispose()
    $bmp.Dispose()
}

$iconsDir = Join-Path $PSScriptRoot "..\Icons"
if (-not (Test-Path $iconsDir)) { New-Item -ItemType Directory -Path $iconsDir | Out-Null }

Create-Icon (Join-Path $iconsDir "Chewing1.ico") ([System.Drawing.Color]::LimeGreen)
Create-Icon (Join-Path $iconsDir "Chewing2.ico") ([System.Drawing.Color]::YellowGreen)
Create-Icon (Join-Path $iconsDir "Chewing3.ico") ([System.Drawing.Color]::Orange)
Create-Icon (Join-Path $iconsDir "Chewing4.ico") ([System.Drawing.Color]::Tomato)

Write-Host "Chewing icons generated at $iconsDir"
