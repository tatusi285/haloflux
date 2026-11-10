param(
    [string]$Video = "www\media\scene.mp4",
    [int]$Fps = 15,
    [int]$Width = 1920
)

# Rebuilds www/media/frames from a video file.
# The bottom 12% is cropped because screen recordings usually carry a progress bar there.

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Video)) { throw "video not found: $Video" }

$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { throw "ffmpeg not found on PATH" }

$out = Join-Path (Split-Path $Video) "frames"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

& $ffmpeg -y -v error -i $Video -vf "crop=iw:ih*0.88:0:0,fps=$Fps,scale=${Width}:-2" -q:v 5 "$out\f_%04d.jpg"

$count = (Get-ChildItem $out -Filter *.jpg | Measure-Object).Count
$size = [math]::Round(((Get-ChildItem $out -Filter *.jpg | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "frames: $count  size: $size MB  -> $out"
