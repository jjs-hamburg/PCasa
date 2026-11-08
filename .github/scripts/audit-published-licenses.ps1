param(
    [Parameter(Mandatory=$true)][string]$PublishDir,
    [Parameter(Mandatory=$true)][string]$ProjectAssetsFile,
    [string]$OutputDir = "license-audit"
)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$publishPath = (Resolve-Path $PublishDir).Path
$assets = Get-Content -LiteralPath $ProjectAssetsFile -Raw | ConvertFrom-Json
$inventory = @(
    Get-ChildItem -LiteralPath $publishPath -File -Recurse | ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath($publishPath, $_.FullName)
        [pscustomobject]@{
            Path = $relative
            Size = $_.Length
            SHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        }
    }
)
$inventory | Export-Csv -NoTypeInformation -Encoding utf8 -Path (Join-Path $OutputDir "publish-files.csv")
$packages = @()
foreach ($item in $assets.libraries.PSObject.Properties) {
    if ($item.Value.type -ne "package") { continue }
    $parts = $item.Name -split '/', 2
    $packageId = $parts[0]
    $version = $parts[1]
    $nugetRoot = Join-Path $env:USERPROFILE ".nuget/packages"
    $pkgDir = Join-Path (Join-Path $nugetRoot $packageId.ToLowerInvariant()) $version
    $nuspec = Get-ChildItem $pkgDir -Filter "*.nuspec" -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $license = "UNKNOWN"
    $licenseUrl = ""
    if ($null -ne $nuspec) {
        [xml]$xml = Get-Content -LiteralPath $nuspec.FullName -Raw
        if ($null -ne $xml.package.metadata.license) {
            $license = [string]$xml.package.metadata.license.InnerText
        }
        if ($null -ne $xml.package.metadata.licenseUrl) {
            $licenseUrl = [string]$xml.package.metadata.licenseUrl
        }
    }
    $notices = @(
        Get-ChildItem $pkgDir -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '(?i)^(license|licence|copying|notice|third.party|third_party)' } |
        ForEach-Object { [System.IO.Path]::GetRelativePath($pkgDir, $_.FullName) }
    )
    $packages += [pscustomobject]@{
        Package = $packageId
        Version = $version
        LicenseMetadata = $license
        LicenseUrl = $licenseUrl
        BundledLicenseFiles = ($notices -join '; ')
    }
}
$packages | Sort-Object Package | Export-Csv -NoTypeInformation -Encoding utf8 -Path (Join-Path $OutputDir "nuget-dependency-licenses.csv")
$noticeFiles = @(
    $inventory | Where-Object { $_.Path -match '(?i)(license|licence|notice|third.party|third_party|copying)' }
)
$nativeFiles = @(
    $inventory | Where-Object { $_.Path -match '(?i)\.(dll|exe|so|dat|bin|pak)$' }
)
$summary = @(
    "# PCasa binary licensing audit: file-level facts"
    ""
    "This is an evidence collection only, NOT legal clearance."
    ""
    "Published file count: $($inventory.Count)"
    "Native/binary candidate count: $($nativeFiles.Count)"
    "NuGet package count: $($packages.Count)"
    "License/notice-named files in published folder: $($noticeFiles.Count)"
    ""
    "## Actual published notice files"
    ""
    $(if ($noticeFiles.Count -eq 0) {"(none)"} else {$noticeFiles.Path | ForEach-Object { "- `$_" }})
    ""
    "## Required manual review before GPL-3.0-only release"
    ""
    "- Verify all NuGet resolved direct + transitive package licenses."
    "- Verify Chromium Embedded Framework and Chromium third-party attributions in the actual distribution."
    "- Verify libvips Windows binary bundled codec/component licenses and LGPL replacement/source conditions."
    "- Verify ExifTool + packaged Perl runtime, their Artistic/GPL alternatives and source obligations."
    "- Verify .NET 10 Windows self-contained redistributable and third party notices."
    "- Include all required notices in the installer AND portable package."
)
$summary | Set-Content -Encoding utf8 (Join-Path $OutputDir "README.md")
Write-Host "Audited $($inventory.Count) output files and $($packages.Count) resolved NuGet packages"
Write-Host "Published license/notice named files: $($noticeFiles.Count)"
$noticeFiles | ForEach-Object { Write-Host "  $($_.Path)" }
Write-Host "Audit is an inventory only; no automatic license clearance."
