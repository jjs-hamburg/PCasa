param(
  [Parameter(Mandatory=$true)][string]$PublishDir,
  [Parameter(Mandatory=$true)][string]$OutputDir
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $PublishDir).Path
$files=Join-Path $root 'exiftool/exiftool_files'
$zip=Join-Path $files 'Licenses_Strawberry_Perl.zip'
if (-not (Test-Path -LiteralPath $zip)) { throw 'Missing original Perl rights archive' }
Add-Type -AssemblyName System.IO.Compression
$archive=[System.IO.Compression.ZipFile]::OpenRead($zip)
try {
  $entries=@($archive.Entries | Where-Object { $_.Length -gt 0 } | ForEach-Object { $_.FullName })
  foreach($required in @('perl/Artistic','perl/Copying','gcc-toolchain/gcc/COPYING.RUNTIME','gcc-toolchain/mingw-w64/COPYING.MinGW-w64-runtime.txt','libxz/COPYING','gcc-toolchain/winpthreads/COPYING')) {
    if ($required -notin $entries) { throw "Original archive lacks license evidence: $required" }
  }
} finally {$archive.Dispose()}
$native=@(Get-ChildItem (Join-Path $root 'exiftool') -File -Recurse | Where-Object { $_.Extension -in @('.dll','.exe') })
$report=@(
foreach($file in $native) {
  $rel=[IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
  $name=$file.Name
  $license=''
  $family=''
  if ($name -eq 'exiftool.exe') {
    $family='ExifTool launcher'; $license='exiftool/exiftool_files/LICENSE; Licenses/Native/exiftool-13.59/EXIFTOOL-LICENSE.txt'
  } elseif ($name -in @('perl.exe','perl532.dll')) {
    $family='Strawberry Perl runtime'; $license='Licenses_Strawberry_Perl.zip: perl/Artistic; perl/Copying'
  } elseif ($name -match '^libgcc|^libstdc\+\+') {
    $family='GCC runtime'; $license='Licenses_Strawberry_Perl.zip: gcc-toolchain/gcc/COPYING.RUNTIME; gcc-toolchain/gcc/COPYING3'
  } elseif ($name -match '^libwinpthread') {
    $family='MinGW winpthreads'; $license='Licenses_Strawberry_Perl.zip: gcc-toolchain/winpthreads/COPYING; gcc-toolchain/mingw-w64/COPYING.MinGW-w64-runtime.txt'
  } elseif ($name -match '^liblzma') {
    $family='libxz'; $license='Licenses_Strawberry_Perl.zip: libxz/COPYING; libxz/COPYING.LGPLv2.1'
  } elseif ($name -match '\.xs\.dll$') {
    $family='Perl XS module'
    # Modules may be either Perl-core or additional CPAN distributions.
    # Do not assert that the Perl runtime license covers all module copyrights.
    $module=$rel -replace '^exiftool/exiftool_files/lib/auto/','' -replace '\.xs\.dll$',''
    $moduleParts=$module.Split('/')
    if ($moduleParts.Count -gt 1) { $moduleParts=$moduleParts[0..($moduleParts.Count-2)] }
    $moduleName=$moduleParts -join '/'
    $pm=Join-Path $files ('lib/'+$moduleName+'.pm')
    $license='Licenses_Strawberry_Perl.zip: perl/Artistic; perl/Copying'
    if (Test-Path -LiteralPath $pm) { $license += '; bundled module source '+([IO.Path]::GetRelativePath($root,$pm).Replace('\','/')) }
  } else {
    throw "Unmapped redistributed ExifTool executable: $rel"
  }
  [pscustomobject]@{Path=$rel;Family=$family;LicenseEvidence=$license;SHA256=(Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()}
})
if ($native.Count -ne 38) { throw "Expected 38 exact-version ExifTool native executables; got $($native.Count)" }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$report | Sort-Object Path | Export-Csv -NoTypeInformation -Encoding utf8 (Join-Path $OutputDir 'exiftool-perl-native-license-map.csv')
Write-Host "Inventoried $($native.Count) installed ExifTool native binaries; retained exact Perl/CPAN sources and original license archive."
