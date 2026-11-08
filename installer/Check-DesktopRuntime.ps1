# PCasa installation prerequisite: official Microsoft .NET 10 Desktop Runtime x64.
# Exit 0 = already present / successfully installed; 3010 = restart required;
# all other exit codes abort PCasa Setup before file replacement.
param(
    [switch]$ProbeOnly,
    [string]$RuntimeRoot = "",
    [switch]$NoDownload,
    [switch]$VerifyInstallerOnly
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-DesktopRuntimeRoot {
    if ($RuntimeRoot) { return $RuntimeRoot }
    # Inno Setup runs as a 32-bit setup process even on x64 Windows;
    # ProgramW6432 names the actual x64 Program Files directory.
    $base = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    Join-Path $base 'dotnet'
}

function Test-DesktopRuntime {
    $root = Get-DesktopRuntimeRoot
    # Runtime is installed only if both WindowsDesktop and base runtime are
    # present for the correct architecture and the selected major version.
    foreach ($name in @('Microsoft.WindowsDesktop.App', 'Microsoft.NETCore.App')) {
        $dir = Join-Path (Join-Path $root 'shared') $name
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) { return $false }
        $matched = @(Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^10\.\d+\.\d+$' })
        if ($matched.Count -eq 0) { return $false }
    }
    return $true
}

try {
    if ((-not $VerifyInstallerOnly) -and (Test-DesktopRuntime)) {
        Write-Host 'PCasa: .NET 10 Desktop Runtime x64 is already installed.'
        exit 0
    }
    if ($ProbeOnly -or $NoDownload) {
        Write-Host 'PCasa: .NET 10 Desktop Runtime x64 is missing.'
        exit 10
    }

    # Authoritative Microsoft release manifest. No third-party mirrors,
    # winget dependencies or unversioned download guesses.
    $manifestUri = 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'
    $manifest = Invoke-RestMethod -Uri $manifestUri -TimeoutSec 35
    $entry = @($manifest.releases |
        Where-Object { $_.windowsdesktop.version -match '^10\.\d+\.\d+$' } |
        Sort-Object { [version]$_.windowsdesktop.version } -Descending |
        Select-Object -First 1)
    if ($entry.Count -ne 1) { throw 'Microsoft .NET 10 desktop runtime release is missing from manifest.' }
    $file = @($entry[0].windowsdesktop.files |
        Where-Object { $_.rid -eq 'win-x64' -and $_.name -eq 'windowsdesktop-runtime-win-x64.exe' } |
        Select-Object -First 1)
    if ($file.Count -ne 1 -or -not $file[0].hash -or -not $file[0].url) {
        throw 'Microsoft Windows Desktop Runtime x64 installer not found.'
    }
    $downloadUri = [uri]$file[0].url
    if ($downloadUri.Scheme -ne 'https' -or
        $downloadUri.Host -notin @('builds.dotnet.microsoft.com', 'dotnetcli.azureedge.net',
            'dotnetcli.blob.core.windows.net')) {
        throw 'Microsoft runtime download host is not trusted.'
    }

    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ('pcasa-desktop-runtime-' + [guid]::NewGuid().ToString('N') + '.exe')
    try {
        try {
            Invoke-WebRequest -Uri $downloadUri -OutFile $temp -TimeoutSec 900
        } catch {
            if ($downloadUri.Host -ne 'builds.dotnet.microsoft.com') { throw }
            # Microsoft serves the same signed release also from its official
            # Azure blob origin. Handle an unavailable build CDN without
            # introducing third-party mirrors or relaxing SHA-512 verification.
            $alternate = $downloadUri.AbsoluteUri.Replace(
                'https://builds.dotnet.microsoft.com/dotnet/',
                'https://dotnetcli.blob.core.windows.net/dotnet/')
            Invoke-WebRequest -Uri $alternate -OutFile $temp -TimeoutSec 900
        }
        $expected = ([string]$file[0].hash).Trim().ToLowerInvariant()
        if ($expected -notmatch '^[a-f0-9]{128}$') {
            throw 'Microsoft release manifest SHA-512 has an invalid format.'
        }
        $actual = (Get-FileHash -LiteralPath $temp -Algorithm SHA512).Hash.ToLowerInvariant()
        if ($actual -ne $expected) { throw 'Microsoft runtime installer SHA-512 mismatch.' }
        $signature = Get-AuthenticodeSignature -LiteralPath $temp
        if ($signature.Status -ne 'Valid' -or
            $signature.SignerCertificate.Subject -notmatch '(^|,\s*)CN=Microsoft Corporation(,|$)') {
            throw 'Microsoft runtime installer Authenticode signature is not valid.'
        }

        if ($VerifyInstallerOnly) {
            Write-Host ('Verified official Microsoft .NET Desktop Runtime ' + $entry[0].windowsdesktop.version + ' installer, SHA-512 and signature.')
            exit 0
        }
        $arguments = '/install /quiet /norestart'
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $admin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
        if ($admin) {
            $process = Start-Process -FilePath $temp -ArgumentList $arguments -Wait -PassThru
        } else {
            # Even a per-user PCasa setup needs elevation ONLY for the
            # machine-wide Microsoft prerequisite, never for PCasa itself.
            $process = Start-Process -FilePath $temp -ArgumentList $arguments -Verb RunAs -Wait -PassThru
        }
        if ($process.ExitCode -notin @(0, 3010)) {
            throw "Microsoft runtime installer failed with exit code $($process.ExitCode)."
        }
        if (-not (Test-DesktopRuntime)) {
            throw 'Microsoft runtime installer completed but x64 Desktop Runtime is not available.'
        }
        if ($process.ExitCode -eq 3010) { exit 3010 }
        exit 0
    } finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
} catch {
    Write-Error ('PCasa prerequisite check failed: ' + $_.Exception.Message)
    exit 1
}
