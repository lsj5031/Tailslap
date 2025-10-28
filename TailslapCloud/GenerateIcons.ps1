# PowerShell script to generate simple .ico files for the tray icons
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

Create-Icon "IconIdle.ico" ([System.Drawing.Color]::Green)
Create-Icon "IconWork1.ico" ([System.Drawing.Color]::Yellow)
Create-Icon "IconWork2.ico" ([System.Drawing.Color]::Orange)

Write-Host "Icons generated successfully!"
