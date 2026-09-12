<#
.SYNOPSIS
    Builds WinVitals as a single self-contained executable in dist/.

.DESCRIPTION
    Produces one .exe that runs on a machine with no .NET runtime installed.
    Requires the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0

.PARAMETER Runtime
    Target runtime identifier. Defaults to win-x64. Use win-arm64 for ARM devices.

.PARAMETER Configuration
    Build configuration. Defaults to Release.
#>
[CmdletBinding()]
param(
    [string] $Runtime = 'win-x64',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root    = $PSScriptRoot
$project = Join-Path $root 'src/WinVitals.App/WinVitals.App.csproj'
$dist    = Join-Path $root 'dist'

# dotnet is not on PATH in a shell opened before the SDK was installed.
$dotnet = 'dotnet'
if (-not (Get-Command $dotnet -ErrorAction SilentlyContinue)) {
    $fallback = Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'
    if (Test-Path $fallback) {
        $dotnet = $fallback
    } else {
        throw "The .NET SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0"
    }
}

Write-Host "Building WinVitals ($Configuration, $Runtime)..." -ForegroundColor Cyan

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null

& $dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    --output $dist

if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

# The publish folder also contains the debug symbols, which nobody shipping a
# diagnostic tool wants to hand out alongside it.
Get-ChildItem $dist -Filter '*.pdb' | Remove-Item -Force

$exe = Join-Path $dist 'WinVitals.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe but it was not produced." }

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Built $exe ($size MB)" -ForegroundColor Green
Write-Host "Run it with:  $exe --redact"
