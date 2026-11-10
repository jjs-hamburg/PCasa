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
    'chromiumembeddedframework.runtime.win-x64'
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
    $nuspec = Get-ChildItem -LiteralPath $folder -Filter '*.nuspec' -File | Select-Object -First 1
    $expression = ''
    $copyright = ''
    if ($null -ne $nuspec) {
        [xml]$metadata = Get-Content -LiteralPath $nuspec.FullName -Raw
        $expression = [string]$metadata.package.metadata.license.InnerText
        $copyright = [string]$metadata.package.metadata.copyright
    }
    $rows += [pscustomobject]@{
        DeclaredLicense = $expression
        Copyright = $copyright
        Package = $name
        Version = $version
        CopiedLicenseFiles = $copied -join '; '
    }
}
$rows | Sort-Object Package | Export-Csv -LiteralPath (Join-Path $PublishDir 'Licenses/NuGet-license-inventory.csv') -NoTypeInformation -Encoding utf8
# Cairo's distributed notices select the legacy MPL 1.1 option.
# Keep the complete original Mozilla license offline in every Windows installer,
# rather than merely mentioning MPL in a dependency table.
$mplPath = Join-Path $PublishDir 'Licenses/MPL-1.1.txt'
# Frozen official upstream Mozilla MPL 1.1 text is checked in, rather than
# downloading potentially changed text again for every release build.
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$mplSource = Join-Path $repoRoot 'license-baseline/libvips-8.18.7/components/cairo-COPYING-MPL.txt'
if (-not (Test-Path -LiteralPath $mplSource -PathType Leaf)) {
    throw 'Pinned source license file for Cairo MPL 1.1 missing'
}
Copy-Item -LiteralPath $mplSource -Destination $mplPath -Force
$mplText = Get-Content -LiteralPath $mplPath -Raw
$expectedMplSha256 = 'fa01277004aff314888151ea523bdf390992892e13523984f221695d48c7455b'
$actualMplSha256 = (Get-FileHash -LiteralPath $mplPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualMplSha256 -ne $expectedMplSha256 -or
    $mplText.Length -lt 10000 -or
    $mplText -notmatch 'MOZILLA PUBLIC LICENSE' -or
    $mplText -notmatch 'Version 1\.1') {
    throw "Pinned MPL 1.1 license bytes changed (actual SHA256: $actualMplSha256). A new license review is required."
}
# ExifTool's Strawberry Perl attribution archive must travel with the EXE.
foreach ($path in @(
    'exiftool/exiftool.exe',
    'exiftool/exiftool_files/LICENSE',
    'exiftool/exiftool_files/Licenses_Strawberry_Perl.zip',
    'Licenses/PCasa-GPL-3.0.txt',
    'Licenses/LGPL-3.0.txt',
    'Licenses/MPL-1.1.txt',
    'Licenses/Apache-2.0.txt',
    'Licenses/MIT-Microsoft.txt',
    'Licenses/MIT-NetVips.txt',
    'Licenses/THIRD-PARTY-NOTICES.md',
    'Licenses/MapLibre-LICENSE.txt'
)) {
    if (-not (Test-Path (Join-Path $PublishDir $path))) {
        throw "Required release license/binary file missing: $path"
    }
}
Write-Host "Copied license files from $($rows.Count) resolved NuGet packages."
Write-Host "Validated PCasa GPL, notices, MapLibre, ExifTool and Strawberry Perl archive."
