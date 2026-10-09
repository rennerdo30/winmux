<#
.SYNOPSIS
    Put a pinned, verified Inno Setup compiler in a folder and print the path to ISCC.exe.

.DESCRIPTION
    Used by CI to build the installer. The version is pinned and the download is checked twice —
    against the SHA-256 recorded here, and for a valid Authenticode signature from the publisher —
    before it is run. A compiler is part of what produces the shipped bytes; it gets the same
    treatment the NuGet lock files give packages (ADR 0018).

    Installs portably (/PORTABLE=1): no Start Menu entry, no uninstaller, nothing in the registry,
    so it is also safe to run on a developer machine.

    To move to a newer Inno Setup: change the version, the URL and the hash together, and say so in
    the commit. The publisher name changes only if Inno Setup's signer does.

.EXAMPLE
    $iscc = .\scripts\install-inno-setup.ps1 -Destination $env:RUNNER_TEMP\inno
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

# Inno Setup 7.1.0, released 2026-08-12 — the newest release on https://jrsoftware.org/isdl.php.
$version = '7.1.0'
$url = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe'
$sha256 = '0362A383ED217D4C4239B5933866DD96D3EB2102737DA92F80F6057A4B40DF2F'
$publisher = 'CN=Pyrsys B.V.'

$iscc = Join-Path $Destination 'ISCC.exe'
if (Test-Path -LiteralPath $iscc) { return $iscc }

$download = Join-Path ([System.IO.Path]::GetTempPath()) "innosetup-$version-$([guid]::NewGuid().ToString('N')).exe"
try {
    Invoke-WebRequest -Uri $url -OutFile $download -UseBasicParsing

    $actual = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash
    if ($actual -ne $sha256) {
        throw "Inno Setup $version did not match its pinned SHA-256 (expected $sha256, got $actual)."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $download
    if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate.Subject.StartsWith($publisher)) {
        throw "Inno Setup $version is not validly signed by $publisher ($($signature.Status), $($signature.SignerCertificate.Subject))."
    }

    $process = Start-Process -FilePath $download -Wait -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', '/PORTABLE=1', "/DIR=$Destination")
    if ($process.ExitCode -ne 0) { throw "Installing Inno Setup $version failed with exit code $($process.ExitCode)." }
}
finally {
    Remove-Item -LiteralPath $download -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path -LiteralPath $iscc)) { throw "Inno Setup $version installed, but $iscc is not there." }
return $iscc
