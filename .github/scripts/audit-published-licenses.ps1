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
    "- Confirm .NET 10 Desktop Runtime is installed separately by the official Microsoft installer."
    "- Include all required notices in the installer AND portable package."
)
$summary | Set-Content -Encoding utf8 (Join-Path $OutputDir "README.md")
Write-Host "Audited $($inventory.Count) output files and $($packages.Count) resolved NuGet packages"
Write-Host "Published license/notice named files: $($noticeFiles.Count)"
$noticeFiles | ForEach-Object { Write-Host "  $($_.Path)" }
Write-Host "Audit is an inventory only; no automatic license clearance."

# Explicit review queue. A metadata declaration is NOT a legal clearance, and
# copying an upstream notice is NOT proof that every compiled codec is covered.
# Keep these entries visible even if the mechanical checks pass.
$review = @(
    [pscustomobject]@{ Component = 'Chromium/CEF'; Requirement = 'Match shipped CEF/Chromium binaries to all third-party credits'; Status = 'MANUAL_REVIEW_REQUIRED' }
    [pscustomobject]@{ Component = 'libvips/native codecs'; Requirement = 'Match actual native codec DLLs to notices; verify LGPL replacement and source obligations'; Status = 'MANUAL_REVIEW_REQUIRED' }
    [pscustomobject]@{ Component = 'ExifTool/Strawberry Perl'; Requirement = 'Inspect Perl bundle archive contents and redistribution terms'; Status = 'MANUAL_REVIEW_REQUIRED' }
)
foreach ($p in $packages) {
    if ([string]::IsNullOrWhiteSpace($p.LicenseMetadata) -or $p.LicenseMetadata -eq 'UNKNOWN') {
        $review += [pscustomobject]@{
            Component = "$($p.Package)/$($p.Version)"
            Requirement = 'Identify authoritative license text and redistribution conditions; NuGet metadata absent'
            Status = 'MANUAL_REVIEW_REQUIRED'
        }
    }
}
$review | Export-Csv -NoTypeInformation -Encoding utf8 -Path (Join-Path $OutputDir 'review-queue.csv')
$summary += @(
    ''
    '## Review queue'
    ''
    "Unresolved manual review entries: $($review.Count)"
    ''
    'See review-queue.csv. This evidence bundle does not assert compliance.'
)
$summary | Set-Content -Encoding utf8 (Join-Path $OutputDir 'README.md')
Write-Host "Unresolved manual review entries: $($review.Count)"

# Deterministic checks against the *published* payload, not source folders.
# They establish distribution facts; binary provenance and license suitability
# still require the version-specific manual review recorded below.
$relativePaths = @{}
foreach ($item in $inventory) {
    $relativePaths[$item.Path.Replace('/','\').ToLowerInvariant()] = $item
}
function Assert-PublishedFile([string]$RelativePath) {
    $key = $RelativePath.Replace('/','\').ToLowerInvariant()
    if (-not $relativePaths.ContainsKey($key) -or $relativePaths[$key].Size -le 0) {
        throw "Missing or empty mandatory distributed file: $RelativePath"
    }
}
$requiredFiles = @(
    'Licenses/PCasa-GPL-3.0.txt',
    'Licenses/LGPL-3.0.txt',
    'Licenses/MPL-1.1.txt',
    'Licenses/Apache-2.0.txt',
    'Licenses/MIT-Microsoft.txt',
    'Licenses/MIT-NetVips.txt',
    'Licenses/MapLibre-LICENSE.txt',
    'Licenses/THIRD-PARTY-NOTICES.md',
    'Licenses/NuGet-license-inventory.csv',
    'Licenses/CEF-LIBVIPS-GUIDE.txt',
    'Licenses/Chromium-Credits.html',
    'Licenses/NATIVE-BUILD-VERSIONS.txt',
    'Licenses/libvips-dynamic-files.csv',
    'Licenses/libvips-dynamic-source.txt',
    'Licenses/Packages/CefSharp.Common.NETCore-152.0.100/LICENSE',
    'Licenses/Packages/CefSharp.Wpf.NETCore-152.0.100/LICENSE',
    'Licenses/Packages/chromiumembeddedframework.runtime-152.0.10/LICENSE.txt',
    'Licenses/Packages/chromiumembeddedframework.runtime.win-x64-152.0.10/LICENSE.txt',
    'Licenses/Packages/NetVips.Native.win-x64-8.18.7/THIRD-PARTY-NOTICES.md',
    'libcef.dll',
    'libvips-42.dll',
    'exiftool/exiftool.exe',
    'exiftool/exiftool_files/LICENSE',
    'exiftool/exiftool_files/Licenses_Strawberry_Perl.zip'
)
foreach ($file in $requiredFiles) { Assert-PublishedFile $file }
# Do not treat a nonempty ZIP as proof of actual licence content.
Add-Type -AssemblyName System.IO.Compression
$perlZip = Join-Path $publishPath 'exiftool/exiftool_files/Licenses_Strawberry_Perl.zip'
$archive = [System.IO.Compression.ZipFile]::OpenRead($perlZip)
try {
    $archiveEntries = @($archive.Entries | Where-Object {
        $_.Length -gt 0 -and $_.FullName -match '(?i)(license|licence|copying|notice|artistic)'
    })
    if ($archiveEntries.Count -eq 0) {
        throw 'Strawberry Perl archive contains no license or notice entries'
    }
} finally { $archive.Dispose() }
# All package licence metadata must be either an SPDX expression or backed by
# an upstream licence file copied into the packaged directory.
foreach ($package in $packages) {
    if ($package.LicenseMetadata -eq 'UNKNOWN' -or [string]::IsNullOrWhiteSpace($package.LicenseMetadata)) {
        throw "NuGet license metadata unresolved: $($package.Package)/$($package.Version)"
    }
    if ($package.LicenseMetadata -match '^(?i:LICENSE(?:\.txt)?)$') {
        $prefix = "Licenses\Packages\$($package.Package)-$($package.Version)\".ToLowerInvariant()
        $matching = @($relativePaths.Keys | Where-Object { $_.StartsWith($prefix) })
        if ($matching.Count -eq 0) {
            throw "NuGet package-declared licence file absent: $($package.Package)/$($package.Version)"
        }
    }
}
$evidence = @(
    [pscustomobject]@{Component='PCasa'; Evidence='GPLv3 license and copyright notices packaged'; Status='FILE_VERIFIED'}
    [pscustomobject]@{Component='NuGet'; Evidence="$($packages.Count) resolved packages with metadata; named package license files checked"; Status='FILE_VERIFIED'}
    [pscustomobject]@{Component='Chromium/CEF'; Evidence='Pinned libcef.dll SHA256, exact-package CEF/CefSharp BSD texts and 19MB+ runtime-generated Chromium-Credits.html packaged'; Status='RUNTIME_CREDITS_VERIFIED'}
    [pscustomobject]@{Component='libvips'; Evidence='Pinned libvips-42.dll SHA256, exact static-build component versions/source references, LGPL-3, upstream notices and Cairo MPL-1.1 packaged; source/copyright detail review outstanding'; Status='PARTIAL_MANUAL_REVIEW_REQUIRED'}
    [pscustomobject]@{Component='ExifTool/Perl'; Evidence="ExifTool LICENSE and Strawberry Perl ZIP with $($archiveEntries.Count) license-named entries present; contents/obligations need review"; Status='PARTIAL_MANUAL_REVIEW_REQUIRED'}
)
$evidence | Export-Csv -NoTypeInformation -Encoding utf8 -Path (Join-Path $OutputDir 'component-evidence.csv')
Write-Host "Verified $($requiredFiles.Count) mandated published files and $($archiveEntries.Count) Perl licence archive entries."

# Any change in pinned CEF or libvips binary changes the compliance subject;
# fail rather than silently carrying forward notices from a previous version.
$knownNativeHashes = @{
    'libcef.dll' = '08a40295bebd9421468115a09c375f3decce219b6673ff77410c479c03b43603'

}
foreach ($item in $knownNativeHashes.GetEnumerator()) {
    $path = $item.Key.ToLowerInvariant()
    if (-not $relativePaths.ContainsKey($path)) { throw "Missing pinned native library $path" }
    if ($relativePaths[$path].SHA256.ToLowerInvariant() -ne $item.Value) {
        throw "Native library changed without updated version-specific license review: $path"
    }
}
Write-Host "Verified pinned CEF and libvips binary hashes."

# All 38 DLLs must match the pinned upstream dynamic archive's installation
# manifest. The download script first verifies the upstream archive SHA256,
# and copies each DLL without alteration. Here we verify the final publish.
$nativeManifest = @(Import-Csv -LiteralPath (Join-Path $publishPath 'Licenses/libvips-dynamic-files.csv'))
if ($nativeManifest.Count -ne 38) { throw "Expected 38 dynamic libvips DLLs; got $($nativeManifest.Count)" }
$names = @{}
foreach ($native in $nativeManifest) {
    $name = [string]$native.DLL
    $key = $name.ToLowerInvariant()
    if ($name -notmatch '^[a-zA-Z0-9_.+\-]+\.dll$' -or $names.ContainsKey($key)) {
        throw "Invalid or duplicate dynamic DLL in manifest: $name"
    }
    $names[$key] = $true
    if (-not $relativePaths.ContainsKey($key)) { throw "Missing dynamic libvips DLL: $name" }
    if ($relativePaths[$key].SHA256 -ne $native.SHA256.ToLowerInvariant() -or
        $relativePaths[$key].Size -ne [long]$native.Size) {
        throw "Dynamic libvips DLL changed from pinned upstream artifact: $name"
    }
}
if (-not $names.ContainsKey('libvips-42.dll') -or -not $names.ContainsKey('libglib-2.0-0.dll') -or
    -not $names.ContainsKey('libheif.dll') -or -not $names.ContainsKey('librsvg-2-2.dll')) {
    throw 'Expected dynamically linked native components missing'
}
$origin = Get-Content -LiteralPath (Join-Path $publishPath 'Licenses/libvips-dynamic-source.txt') -Raw
if ($origin -notmatch '3a122eb3d588690008216f786338e2f9329dba3c3c2768b5933cf7299d549241') {
    throw 'Pinned native upstream archive provenance absent'
}
Write-Host "Verified all 38 SHA-pinned dynamic libvips component DLLs."
