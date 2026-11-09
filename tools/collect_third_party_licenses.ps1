param(
    [Parameter(Mandatory=$true)][string]$AssetsFile,
    [Parameter(Mandatory=$true)][string]$PublishDir
)
$ErrorActionPreference = 'Stop'
$assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json
$root = Join-Path $env:USERPROFILE '.nuget/packages'
$dest = Join-Path $PublishDir 'Licenses/Packages'
New-Item -ItemType Directory -Path $dest -Force | Out-Null
$required = @(
    'CefSharp.Common.NETCore',
    'CefSharp.Wpf.NETCore',
    'chromiumembeddedframework.runtime',
    'chromiumembeddedframework.runtime.win-x64',
    'NetVips.Native.win-x64'
)
$rows = @()
foreach ($entry in $assets.libraries.PSObject.Properties) {
    if ($entry.Value.type -ne 'package') { continue }
    $parts = $entry.Name -split '/', 2
    $name = $parts[0]; $version = $parts[1]
    $folder = Join-Path (Join-Path $root $name.ToLowerInvariant()) $version
    if (-not (Test-Path $folder)) { throw "NuGet package missing: $name/$version" }
    $candidate = @(Get-ChildItem -LiteralPath $folder -File -Recurse |
        Where-Object {
            $_.Name -match '^(LICENSE|LICENCE|NOTICE|COPYING)([._-].*)?$' -or
            $_.Name -match '^THIRD[-_ ]PARTY[-_ ]NOTICES'
        } | Where-Object { $_.Length -lt 5000000 })
    $copied = @()
    foreach ($item in $candidate) {
        $rel = [System.IO.Path]::GetRelativePath($folder, $item.FullName)
        $target = Join-Path (Join-Path $dest "$name-$version") $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $item.FullName -Destination $target -Force
        $copied += $rel
    }
    if ($name -in $required -and $copied.Count -eq 0) {
        throw "Required upstream license material missing: $name/$version"
    }
    $rows += [pscustomobject]@{
        Package = $name
        Version = $version
        CopiedLicenseFiles = $copied -join '; '
    }
}
$rows | Sort-Object Package | Export-Csv -LiteralPath (Join-Path $PublishDir 'Licenses/NuGet-license-inventory.csv') -NoTypeInformation -Encoding utf8
# ExifTool's Strawberry Perl attribution archive must travel with the EXE.
foreach ($path in @(
    'exiftool/exiftool.exe',
    'exiftool/exiftool_files/LICENSE',
    'exiftool/exiftool_files/Licenses_Strawberry_Perl.zip',
    'Licenses/PCasa-GPL-3.0.txt',
    'Licenses/LGPL-3.0.txt',
    'Licenses/THIRD-PARTY-NOTICES.md',
    'Licenses/MapLibre-LICENSE.txt'
)) {
    if (-not (Test-Path (Join-Path $PublishDir $path))) {
        throw "Required release license/binary file missing: $path"
    }
}
Write-Host "Copied license files from $($rows.Count) resolved NuGet packages."
Write-Host "Validated PCasa GPL, notices, MapLibre, ExifTool and Strawberry Perl archive."
