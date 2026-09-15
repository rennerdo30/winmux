<#
.SYNOPSIS
    Write THIRD-PARTY-NOTICES.txt for the assemblies a package actually ships.

.DESCRIPTION
    MIT, BSD and Apache all require the copyright and permission notice to travel with the
    binaries. WinMux ships around forty third-party DLLs -- Avalonia, SkiaSharp, HarfBuzz,
    BouncyCastle, SSH.NET, FluentFTP, Porta.Pty, Tomlyn, Terminal.Emulation, Google's ANGLE --
    and for a long time shipped none of their licences.

    The list is built from the publish output rather than hand-maintained, because a hand-written
    list is wrong the first time someone adds a package and does not remember this file exists.
    Licence text comes from the NuGet cache where the package carries one; where it does not, the
    entry records the assembly's own copyright string and its SPDX expression from the .nuspec, so
    the notice is still attributable.

.PARAMETER Destination
    The published folder to scan and write into.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packagesRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget\packages' }

# Ours, not theirs. Everything else in the folder came from a package.
$ourPrefixes = @('WinMux', 'wmux')

$assemblies = Get-ChildItem -LiteralPath $Destination -Filter *.dll -Recurse |
    Where-Object { $name = $_.BaseName; -not ($ourPrefixes | Where-Object { $name -like "$_*" }) } |
    Sort-Object BaseName -Unique

# Assemblies whose file name does not resemble the package that ships them. The prefix search
# below cannot find these, and a missing entry is a missing notice, so they are named.
$packageAliases = @{
    'Renci.SshNet'  = 'ssh.net'
    'conpty'        = 'microsoft.windows.console.conpty'
    'av_libglesv2'  = 'avalonia.angle.windows.natives'
    'libSkiaSharp'  = 'skiasharp.nativeassets.win32'
    'libHarfBuzzSharp' = 'harfbuzzsharp.nativeassets.win32'
}

function Find-PackageDirectory {
    param([string] $AssemblyName)

    if ($packageAliases.ContainsKey($AssemblyName)) {
        $aliased = Join-Path $packagesRoot $packageAliases[$AssemblyName]
        if (Test-Path -LiteralPath $aliased) { return $aliased }
    }

    # Package id usually matches the assembly name; where it does not (libSkiaSharp lives in
    # SkiaSharp, av_libglesv2 in Avalonia.Angle.Windows.Natives) fall back to a prefix search.
    $direct = Join-Path $packagesRoot $AssemblyName.ToLowerInvariant()
    if (Test-Path -LiteralPath $direct) { return $direct }

    $trimmed = $AssemblyName -replace '^lib', '' -replace '^av_', ''
    return Get-ChildItem -LiteralPath $packagesRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "$trimmed*" -or $trimmed -like "$($_.Name)*" } |
        Select-Object -First 1 -ExpandProperty FullName
}

function Get-LicenceText {
    param([string] $PackageDirectory)
    if (-not $PackageDirectory) { return $null }

    $file = Get-ChildItem -LiteralPath $PackageDirectory -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE)(\.(txt|md))?$' } |
        Select-Object -First 1
    if (-not $file) { return $null }

    return (Get-Content -LiteralPath $file.FullName -Raw).Trim()
}

function Get-SpdxExpression {
    param([string] $PackageDirectory)
    if (-not $PackageDirectory) { return $null }

    $nuspec = Get-ChildItem -LiteralPath $PackageDirectory -Recurse -Filter *.nuspec -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $nuspec) { return $null }

    $xml = [xml](Get-Content -LiteralPath $nuspec.FullName -Raw)
    $licence = $xml.package.metadata.license
    if ($licence -is [string]) { return $licence }
    if ($licence -and $licence.'#text') { return $licence.'#text' }
    return $null
}

function Get-CanonicalLicence {
    param([string] $Spdx, [string] $Copyright)

    $holder = if ($Copyright) { $Copyright } else { "the copyright holders" }

    switch -Regex ($Spdx) {
        '^MIT$' { return @"
$holder

Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
"@ }
        '^Apache-2\.0$' { return @"
$holder

Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file
except in compliance with the License. You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software distributed under the
License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND,
either express or implied. See the License for the specific language governing permissions
and limitations under the License.
"@ }
        '^BSD-2-Clause$' { return @"
$holder

Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of
   conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of
   conditions and the following disclaimer in the documentation and/or other materials
   provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
"@ }
        '^BSD-3-Clause$' { return @"
$holder

Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of
   conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of
   conditions and the following disclaimer in the documentation and/or other materials
   provided with the distribution.
3. Neither the name of the copyright holder nor the names of its contributors may be used to
   endorse or promote products derived from this software without specific prior written
   permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
"@ }
        default { return $null }
    }
}

$sections = New-Object System.Collections.Generic.List[string]
$seen = New-Object System.Collections.Generic.HashSet[string]
$withoutText = New-Object System.Collections.Generic.List[string]

foreach ($assembly in $assemblies) {
    $directory = Find-PackageDirectory $assembly.BaseName
    $key = if ($directory) { Split-Path -Leaf $directory } else { $assembly.BaseName }
    if (-not $seen.Add($key)) { continue }

    $copyright = (Get-Item -LiteralPath $assembly.FullName).VersionInfo.LegalCopyright
    $spdx = Get-SpdxExpression $directory
    $text = Get-LicenceText $directory

    $header = "-" * 78
    $entry = "$header`n$key`n$header`n"
    # A nuspec may carry <license type="file">LICENSE</license>, where the value is a filename
    # rather than an SPDX id. Printing "License: LICENSE" says nothing; the text below says it all.
    if ($spdx -and $spdx -notmatch '^(LICENSE|LICENCE|COPYING|NOTICE)') { $entry += "License: $spdx`n" }
    if ($copyright) { $entry += "$copyright`n" }
    $entry += "`n"

    if (-not $text -and $spdx) {
        # Most packages declare a licence as an SPDX expression and bundle no file at all, which is
        # why the first version of this left two thirds of the list as bare copyright lines. The
        # obligation is to reproduce the licence, so reproduce it: the canonical text of the common
        # permissive licences, with this component's own copyright line above it.
        $text = Get-CanonicalLicence -Spdx $spdx -Copyright $copyright
    }

    if ($text) {
        $entry += "$text`n"
    }
    else {
        $entry += "(This package declares no licence file and no SPDX expression. The copyright above`nis taken from the shipped assembly; see the project's own site for terms.)`n"
        $withoutText.Add($key) | Out-Null
    }

    $sections.Add($entry) | Out-Null
}

$preamble = @"
THIRD-PARTY NOTICES
===================

WinMux itself is MIT licensed; see LICENSE. This file covers the third-party components
distributed alongside it, and is generated from the contents of the package rather than
maintained by hand.

$($sections.Count) component(s).

"@

$output = Join-Path $Destination 'THIRD-PARTY-NOTICES.txt'
($preamble + ($sections -join "`n")) | Set-Content -LiteralPath $output -Encoding utf8

Write-Host "Third-party notices: $($sections.Count) component(s) -> $output"
if ($withoutText.Count -gt 0) {
    Write-Host "  no licence text found for: $($withoutText -join ', ')" -ForegroundColor Yellow
}
