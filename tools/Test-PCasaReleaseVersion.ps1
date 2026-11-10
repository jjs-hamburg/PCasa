$ErrorActionPreference = 'Stop'
$getVersion = Join-Path $PSScriptRoot 'Get-PCasaReleaseVersion.ps1'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../PCasa.Version.props') -Raw
$cases = @(
    @{ Product='0.1.Alpha.9'; File='0.1.0.9'; Semver='0.1.0-alpha.9'; Assembly='0.1.0.0' }
    @{ Product='0.1.Alpha.10'; File='0.1.0.10'; Semver='0.1.0-alpha.10'; Assembly='0.1.0.0' }
    @{ Product='0.2.Beta.2'; File='0.2.1.2'; Semver='0.2.0-beta.2'; Assembly='0.2.0.0' }
    @{ Product='0.3.RC.3'; File='0.3.2.3'; Semver='0.3.0-rc.3'; Assembly='0.3.0.0' }
    @{ Product='1.0'; File='1.0.0.0'; Semver='1.0.0'; Assembly='1.0.0.0' }
)
$temp = Join-Path ([IO.Path]::GetTempPath()) ("PCasa-version-test-" + [guid]::NewGuid().ToString('N') + '.props')
try {
    foreach ($case in $cases) {
        $content = ($source -replace '<Version>[^<]*</Version>', "<Version>$($case.Semver)</Version>"
            -replace '<AssemblyVersion>[^<]*</AssemblyVersion>', "<AssemblyVersion>$($case.Assembly)</AssemblyVersion>"
            -replace '<FileVersion>[^<]*</FileVersion>', "<FileVersion>$($case.File)</FileVersion>"
            -replace '<InformationalVersion>[^<]*</InformationalVersion>', "<InformationalVersion>$($case.Product)</InformationalVersion>")
        Set-Content -LiteralPath $temp -Value $content -Encoding utf8
        $version = & $getVersion -VersionFile $temp
        if ($version.Product -ne $case.Product -or $version.File -ne $case.File -or
            $version.NuGet -ne $case.Semver -or $version.Tag -ne "v$($case.Product)" -or
            $version.InstallerPath -ne "dist/PCasa-Setup-$($case.Product).exe") {
            throw "Release parser rejected valid test version $($case.Product)"
        }
    }
    $bad = $source -replace '<InformationalVersion>[^<]*</InformationalVersion>', '<InformationalVersion>0.1.Alpha.10</InformationalVersion>'
    Set-Content -LiteralPath $temp -Value $bad -Encoding utf8
    $failedAsExpected = $false
    try { $null = & $getVersion -VersionFile $temp } catch { $failedAsExpected = $true }
    if (-not $failedAsExpected) { throw 'Version mismatch did not stop the release' }
    Write-Host "PASS: 5 release identities and mismatch rejection"
}
finally {
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
}
