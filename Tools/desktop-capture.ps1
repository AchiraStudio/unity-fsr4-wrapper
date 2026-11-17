<#
.SYNOPSIS
  Watches <OutDir>\ext for *.req files written by the FSR showcase automation and answers each with desktop screenshots.

.DESCRIPTION
  Frame-generated frames only exist in the swapchain, so Unity's ScreenCapture (which reads the rendered backbuffer) never sees them.
  A request file "<name>.req" holds "count,intervalSeconds". For each request this script grabs <count> screenshots of the primary
  display as ext\<name>_00.png ... and then creates "<name>.done". It exits when OutDir\done.flag appears or after -TimeoutSeconds.

.EXAMPLE
  powershell -File Tools\desktop-capture.ps1 -OutDir Screenshots\run1
#>
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$TimeoutSeconds = 3600
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System.Runtime.InteropServices;
public static class Dpi { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
"@
[void][Dpi]::SetProcessDPIAware()

$ext = Join-Path $OutDir "ext"
New-Item -ItemType Directory -Force -Path $ext | Out-Null
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
Write-Host "Watching $ext  (screen $($bounds.Width)x$($bounds.Height))"

function Grab([string]$path) {
    $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

while ((Get-Date) -lt $deadline) {
    foreach ($req in Get-ChildItem -Path $ext -Filter *.req -ErrorAction SilentlyContinue) {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($req.Name)
        $done = Join-Path $ext "$name.done"
        if (Test-Path $done) { continue }
        $parts = (Get-Content $req.FullName -Raw).Trim().Split(',')
        $count = [int]$parts[0]
        $interval = [double]::Parse($parts[1], [System.Globalization.CultureInfo]::InvariantCulture)
        for ($i = 0; $i -lt $count; $i++) {
            Grab (Join-Path $ext ("{0}_{1:00}.png" -f $name, $i))
            Start-Sleep -Milliseconds ([int]($interval * 1000))
        }
        Set-Content -Path $done -Value (Get-Date -Format o)
        Write-Host "captured $count x $name"
    }
    if (Test-Path (Join-Path $OutDir "done.flag")) { break }
    Start-Sleep -Milliseconds 100
}
Write-Host "Watcher finished."
