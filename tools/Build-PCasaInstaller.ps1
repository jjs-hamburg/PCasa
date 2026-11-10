param([Parameter(Mandatory=$true)][string]$IsccPath)
$ErrorActionPreference = 'Stop'
$version = & (Join-Path $PSScriptRoot 'Get-PCasaReleaseVersion.ps1')
if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw "Inno Setup compiler missing: $IsccPath"
}
& $IsccPath "/DMyAppVersion=$($version.Product)" "/DMyFileVersion=$($version.File)" 'installer/PCasa.iss'
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with code $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $version.InstallerPath -PathType Leaf)) {
    throw "Installer missing: $($version.InstallerPath)"
}
Write-Host "PASS: built $($version.InstallerPath)"
