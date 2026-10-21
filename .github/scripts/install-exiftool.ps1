param(
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$version = '13.59'
$expectedSha256 = '44b512b25af500724ba579d0a53c8fc5851628b692dd5e5d94ae4a15c2cba9ec'
$url = "https://community.chocolatey.org/api/v2/package/exiftool/$($version).0"
$work = Join-Path $env:RUNNER_TEMP ('pcasa-exiftool-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    $packageArchive = Join-Path $work 'package.zip'
    Invoke-WebRequest -Uri $url -OutFile $packageArchive -MaximumRedirection 10
    Expand-Archive -Path $packageArchive -DestinationPath (Join-Path $work 'package')
    $archive = Join-Path $work "package/tools/exiftool-$($version)_64.zip"
    if (-not (Test-Path -LiteralPath $archive)) {
        throw 'Pinned Chocolatey package did not contain the official 64-bit archive.'
    }
    $actualSha = (Get-FileHash -Path $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $archiveBytes = (Get-Item -LiteralPath $archive).Length
    if ($archiveBytes -lt 10000000) {
        throw "ExifTool download did not return the expected archive ($archiveBytes bytes)."
    }
    if ($actualSha -ne $expectedSha256) {
        throw "ExifTool archive SHA256 mismatch. Expected $expectedSha256, actual $actualSha"
    }
    Expand-Archive -Path $archive -DestinationPath (Join-Path $work 'unpacked')
    $source = Join-Path $work "unpacked/exiftool-$($version)_64"
    if (-not (Test-Path (Join-Path $source 'exiftool(-k).exe'))) {
        throw 'ExifTool Windows executable was not found in verified archive.'
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -Path (Join-Path $source 'exiftool_files') -Destination (Join-Path $Destination 'exiftool_files') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $source 'exiftool(-k).exe') -Destination (Join-Path $Destination 'exiftool.exe') -Force
    & (Join-Path $Destination 'exiftool.exe') -ver
    if ($LASTEXITCODE -ne 0) { throw 'Bundled ExifTool failed its smoke test.' }
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
