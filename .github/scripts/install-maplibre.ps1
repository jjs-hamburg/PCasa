param(
    [string]$Destination = "src/PCasa.App/Resources"
)
$ErrorActionPreference = "Stop"
# Build-time download only. The published application runs bundled MapLibre
# without relying on CDN availability. Do not update version implicitly.
$version = "5.9.0"
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$base = "https://cdn.jsdelivr.net/npm/maplibre-gl@$version/dist"
$files = @(
    @{ Name = "maplibre-gl.js"; Min = 450000 },
    @{ Name = "maplibre-gl.css"; Min = 15000 }
)
foreach ($file in $files) {
    $path = Join-Path $Destination $file.Name
    Invoke-WebRequest -Uri "$base/$($file.Name)" -OutFile $path -MaximumRetryCount 3 -RetryIntervalSec 3
    $length = (Get-Item $path).Length
    if ($length -lt $file.Min) {
        throw "Bundled MapLibre resource too small: $($file.Name) ($length bytes)"
    }
    Write-Host "Bundled MapLibre $version $($file.Name) ($length bytes)"
}
if (-not (Select-String -Path (Join-Path $Destination "maplibre-gl.js") -Pattern "maplibregl" -Quiet)) {
    throw "Unrecognizable MapLibre JavaScript."
}
