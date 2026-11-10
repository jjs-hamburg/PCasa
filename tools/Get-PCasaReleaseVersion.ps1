$ErrorActionPreference = 'Stop'
$versionFile = Join-Path $PSScriptRoot '../PCasa.Version.props'
[xml]$xml = Get-Content -LiteralPath $versionFile -Raw
$p = $xml.Project.PropertyGroup
$product = [string]$p.InformationalVersion
$file = [string]$p.FileVersion
$semver = [string]$p.Version
if ($product -notmatch '^(\d+)\.(\d+)(?:\.(Alpha|Beta|RC)\.(\d+))?
    throw "Invalid PCasa product version in file: $product"
}
$major = [int]$Matches[1]
$minor = [int]$Matches[2]
$stage = $Matches[3]
$build = if ($stage) { [int]$Matches[4] } else { 0 }
$stageNumber = if ($stage) { @{ Alpha = 0; Beta = 1; RC = 2 }[$stage] } else { 0 }
$expectedFile = "$major.$minor.$stageNumber.$build"
$expectedSemver = if ($stage) { "$major.$minor.0-$($stage.ToLowerInvariant()).$build" } else { "$major.$minor.0" }
$expectedAssembly = "$major.$minor.0.0"
if ($file -ne $expectedFile -or $semver -ne $expectedSemver -or
    [string]$p.AssemblyVersion -ne $expectedAssembly) {
    throw "PCasa version mismatch: product=$product file=$file semver=$semver; expected file=$expectedFile semver=$expectedSemver"
}
[pscustomobject]@{
    Product = $product
    File = $file
    NuGet = $semver
    Tag = "v$product"
    InstallerPath = "dist/PCasa-Setup-$product.exe"
}
) {
    throw "Invalid PCasa product version in file: $product"
}
$major = [int]$Matches[1]
$minor = [int]$Matches[2]
$stage = $Matches[3]
$build = [int]$Matches[4]
$stageNumber = @{ Alpha = 0; Beta = 1; RC = 2 }[$stage]
$expectedFile = "$major.$minor.$stageNumber.$build"
$expectedSemver = "$major.$minor.0-$($stage.ToLowerInvariant()).$build"
if ($file -ne $expectedFile -or $semver -ne $expectedSemver) {
    throw "PCasa version mismatch: product=$product file=$file semver=$semver; expected file=$expectedFile semver=$expectedSemver"
}
[pscustomobject]@{
    Product = $product
    File = $file
    NuGet = $semver
    Tag = "v$product"
    InstallerPath = "dist/PCasa-Setup-$product.exe"
}
