param(
    [Parameter(Mandatory=$true)][string]$PublishDir,
    [Parameter(Mandatory=$true)][string]$InstalledDir,
    [Parameter(Mandatory=$true)][ValidateSet('CURRENTUSER','ALLUSERS')][string]$Mode
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $PublishDir).Path
$target = (Resolve-Path -LiteralPath $InstalledDir).Path
$publishFiles = @(Get-ChildItem -LiteralPath $source -File -Recurse)
$expected = @{}
foreach ($file in $publishFiles) {
    $name = [IO.Path]::GetRelativePath($source, $file.FullName)
    $expected[$name.ToLowerInvariant()] = $true
    $installed = Join-Path $target $name
    if (-not (Test-Path -LiteralPath $installed -PathType Leaf)) {
        throw "Installer $Mode omitted payload file $name"
    }
    $actual = Get-Item -LiteralPath $installed
    if ($actual.Length -ne $file.Length) {
        throw "Installer $Mode changed size of $name"
    }
    $srcHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $dstHash = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash
    if ($srcHash -ne $dstHash) {
        throw "Installer $Mode changed payload bytes of $name"
    }
}
$extras = @(Get-ChildItem -LiteralPath $target -File -Recurse | Where-Object {
    $relative = [IO.Path]::GetRelativePath($target, $_.FullName)
    -not $expected.ContainsKey($relative.ToLowerInvariant()) -and
    $relative -notmatch '^(?i:unins[0-9]+\.(?:exe|dat|msg))$' -and
    $relative -ne 'PCasa.all-users'
} | ForEach-Object { [IO.Path]::GetRelativePath($target, $_.FullName) })
if ($extras.Count -gt 0) {
    throw "Installer $Mode produced unexpected files: $($extras -join ', ')"
}
$marker = Test-Path -LiteralPath (Join-Path $target 'PCasa.all-users')
if ($marker -ne ($Mode -eq 'ALLUSERS')) {
    throw "Installer $Mode wrong all-users marker"
}
Write-Host "PASS $Mode: $($publishFiles.Count) published files exactly match installed SHA256; no unexpected payload"
