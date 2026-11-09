param(
    [Parameter(Mandatory=$true)][string]$PublishDir
)
$ErrorActionPreference = 'Stop'
$uri = 'https://github.com/libvips/build-win64-mxe/releases/download/v8.18.7/vips-dev-x64-web-8.18.7.zip'
$expectedArchive = '3a122eb3d588690008216f786338e2f9329dba3c3c2768b5933cf7299d549241'
$publish = (Resolve-Path -LiteralPath $PublishDir).Path
$work = Join-Path $env:RUNNER_TEMP 'pcasa-dynamic-libvips-8.18.7'
New-Item -ItemType Directory -Force $work | Out-Null
$zip = Join-Path $work 'libvips.zip'
Invoke-WebRequest -Uri $uri -OutFile $zip -MaximumRedirection 10
$sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sha -ne $expectedArchive) { throw "Upstream dynamic libvips archive hash mismatch: $sha" }
$extract = Join-Path $work 'extract'
Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
$dirs = @(Get-ChildItem $extract -Directory -Recurse | Where-Object { $_.Name -eq 'bin' })
if ($dirs.Count -ne 1) { throw "Expected one upstream bin directory, found $($dirs.Count)" }
$dlls = @(Get-ChildItem $dirs[0].FullName -File -Filter '*.dll')
if ($dlls.Count -ne 38) { throw "Expected 38 upstream dynamic DLLs, got $($dlls.Count)" }
$manifest = @()
foreach ($dll in $dlls) {
    $destination = Join-Path $publish $dll.Name
    Copy-Item -LiteralPath $dll.FullName -Destination $destination -Force
    $sourceHash = (Get-FileHash -LiteralPath $dll.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $sourceHash) { throw "Failed to copy native DLL without changes: $($dll.Name)" }
    $manifest += [pscustomobject]@{ DLL=$dll.Name; SHA256=$sourceHash; Size=$dll.Length }
}
$licenses = Join-Path $publish 'Licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
$manifest | Sort-Object DLL | Export-Csv -NoTypeInformation -Encoding utf8 -LiteralPath (Join-Path $licenses 'libvips-dynamic-files.csv')
@(
    'libvips 8.18.7 Windows x64: original upstream dynamic web distribution'
    "Archive: $uri"
    "Archive SHA256: $expectedArchive"
    'Every distributed native DLL is byte-identical to the archive; see libvips-dynamic-files.csv'
    'Version-specific source and build inputs:'
    'https://github.com/libvips/build-win64-mxe/tree/v8.18.7'
    'https://github.com/libvips/build-win64-mxe/releases/tag/v8.18.7'
    'https://github.com/kleisauke/libvips-packaging/blob/v8.18.7/versions.properties'
    'LGPL shared libraries are separately replaceable by ABI-compatible user-built DLLs.'
    'Other native licensing conditions and upstream component notices remain applicable.'
) | Set-Content -Encoding utf8 (Join-Path $licenses 'libvips-dynamic-source.txt')
Write-Host "Pinned dynamic libvips payload installed: $($dlls.Count) DLLs, $expectedArchive"
