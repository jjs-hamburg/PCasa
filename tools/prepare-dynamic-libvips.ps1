param(
    [Parameter(Mandatory=$true)][string]$PublishDir,
    [string]$EvidenceDir = 'dynamic-libvips-evidence'
)
$ErrorActionPreference = 'Stop'
# Pinned official, dynamically linked libvips Windows x64 web package.
# Never silently substitute the static NuGet build or a newer upstream build.
$approved = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../license-baseline/APPROVED-NATIVE.json') -Raw | ConvertFrom-Json
$version = [string]$approved.libvips.version
$archiveSha256 = [string]$approved.libvips.archiveSha256
$nativeSha256 = [string]$approved.libvips.mainDllSha256
$url = "https://github.com/libvips/build-win64-mxe/releases/download/v$version/vips-dev-x64-web-$version.zip"
$work = Join-Path $env:RUNNER_TEMP ("pcasa-libvips-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work, $EvidenceDir | Out-Null
try {
    $archive = Join-Path $work 'libvips.zip'
    Invoke-WebRequest -Uri $url -OutFile $archive
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $archiveSha256) { throw "Official dynamic libvips archive differs from approved $version baseline: $actual" }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $work 'unpacked')
    $main = @(Get-ChildItem -LiteralPath (Join-Path $work 'unpacked') -Filter 'libvips-42.dll' -Recurse -File)
    if ($main.Count -ne 1) { throw "Expected exactly one libvips-42.dll in dynamic distribution, got $($main.Count)" }
    $sourceDir = $main[0].Directory.FullName
    $actualNative = (Get-FileHash -LiteralPath $main[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualNative -ne $nativeSha256) { throw "Unexpected dynamic libvips binary: $actualNative" }
    $dlls = @(Get-ChildItem -LiteralPath $sourceDir -Filter '*.dll' -File)
    if ($dlls.Count -ne [int]$approved.libvips.dllCount) { throw "Dynamic library count differs from approved baseline: $($dlls.Count)" }
    $dest = (Resolve-Path -LiteralPath $PublishDir).Path
    $manifest = @(
        foreach ($dll in $dlls) {
            $out = Join-Path $dest $dll.Name
            if ((Test-Path -LiteralPath $out) -and $dll.Name -ne 'libvips-42.dll') {
                $previous = (Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash.ToLowerInvariant()
                $next = (Get-FileHash -LiteralPath $dll.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($previous -ne $next) { throw "Refusing to overwrite unrelated PCasa native DLL $($dll.Name)" }
            }
            Copy-Item -LiteralPath $dll.FullName -Destination $out -Force
            [pscustomobject]@{
                File = $dll.Name
                SHA256 = (Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash.ToLowerInvariant()
                Size = (Get-Item -LiteralPath $out).Length
            }
        }
    )
    $manifest | Sort-Object File | Export-Csv -NoTypeInformation -Encoding utf8 (Join-Path $EvidenceDir 'native-dll-inventory.csv')
    # Keep upstream archive provenance visible alongside the installed DLLs.
    @"
Official source: $url
libvips version: $version
Archive SHA256: $archiveSha256
libvips-42.dll SHA256: $nativeSha256
Dynamic DLL count: $($dlls.Count)
This records binary provenance only. Component license texts, source
availability and LGPL library replacement requirements must still be met.
"@ | Set-Content -Encoding utf8 (Join-Path $EvidenceDir 'README.txt')
    Write-Host "PASS: installed $($dlls.Count) hash-pinned dynamically linked native DLLs"
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
