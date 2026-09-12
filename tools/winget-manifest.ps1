<#
.SYNOPSIS
    Writes the three winget manifests for a published release.

.DESCRIPTION
    winget installs WinVitals as a "portable" package: it downloads the single exe
    from the GitHub release and puts `winvitals` on the PATH. The manifests need the
    release URL and the exe's SHA-256, which this script fills in from a release
    asset you have already uploaded.

    After running it, open a pull request adding the generated folder under
    manifests/m/MarkAlexander/WinVitals/<version>/ in
    https://github.com/microsoft/winget-pkgs — or use `wingetcreate submit`.

.PARAMETER Version
    Release version without the leading v, e.g. 0.4.0.

.PARAMETER Exe
    Path to the exact WinVitals.exe that was uploaded to the release. Defaults to
    dist/WinVitals.exe.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Version,
    [string] $Exe = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist/WinVitals.exe')
)

$ErrorActionPreference = 'Stop'

$id        = 'MarkAlexander.WinVitals'
$repo      = 'mark-alexander-hub/winvitals'
$url       = "https://github.com/$repo/releases/download/v$Version/WinVitals.exe"
$sha       = (Get-FileHash $Exe -Algorithm SHA256).Hash
$outDir    = Join-Path (Split-Path $PSScriptRoot -Parent) "packaging/winget/$Version"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
"@ | Set-Content (Join-Path $outDir "$id.yaml") -Encoding UTF8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: portable
Commands:
  - winvitals
Installers:
  - Architecture: x64
    InstallerUrl: $url
    InstallerSha256: $sha
ManifestType: installer
ManifestVersion: 1.6.0
"@ | Set-Content (Join-Path $outDir "$id.installer.yaml") -Encoding UTF8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Mark Isidore Nicholas Alexander
PublisherUrl: https://github.com/mark-alexander-hub
PublisherSupportUrl: https://github.com/$repo/issues
PackageName: WinVitals
PackageUrl: https://github.com/$repo
License: MIT
LicenseUrl: https://github.com/$repo/blob/main/LICENSE
ShortDescription: Diagnose, clean up and speed up a Windows PC, in plain English.
Description: |-
  WinVitals checks a Windows PC the way a careful technician would, explains what it
  finds in plain English, and offers repairs that show their exact commands first,
  take a verified restore point, and can be undone. It never calls a driver or BIOS
  out of date without linking the vendor's own page, and never confuses "could not
  check" with "nothing found".
Moniker: winvitals
Tags:
  - diagnostics
  - cleanup
  - performance
  - repair
  - sleep
ReleaseNotesUrl: https://github.com/$repo/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@ | Set-Content (Join-Path $outDir "$id.locale.en-US.yaml") -Encoding UTF8

Write-Host "Wrote manifests to $outDir"
Write-Host "  SHA-256: $sha"
Write-Host "  URL:     $url"
Write-Host "Validate with:  winget validate --manifest `"$outDir`""
