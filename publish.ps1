# Copyright (c) DevAM and Network Inspector Contributors
# Licensed under the MIT license.

<#
.SYNOPSIS
    Packs and optionally publishes the NetworkInspector NuGet packages.

.DESCRIPTION
    1. Resolves the package version from Directory.Build.props (or -Version).
    2. Cleans all build outputs to ensure a reproducible, artifact-free build.
    3. Packs all three publishable projects in a single dotnet pack invocation:
         NetworkInspector.Values
         NetworkInspector.Core      (includes the bundled Generators analyzer)
         NetworkInspector.Protocols
    4. Optionally pushes the resulting .nupkg and .snupkg files to nuget.org.

    NetworkInspector.Generators is marked IsPackable=false and is intentionally
    not packed -- its analyzer DLL is embedded inside NetworkInspector.Core.

.PARAMETER Version
    Overrides the version string from Directory.Build.props.
    If omitted the value of <Version> in Directory.Build.props is used.

.PARAMETER OutputDir
    Directory that receives the packed .nupkg and .snupkg files.
    Defaults to "artifacts" inside the repo root.

.PARAMETER ApiKey
    NuGet API key for nuget.org. Required when -Push is specified.
    Alternatively set the NUGET_API_KEY environment variable.

.PARAMETER Source
    NuGet feed URL. Defaults to https://api.nuget.org/v3/index.json.

.PARAMETER Push
    Push the packed packages to the NuGet feed after packing.

.PARAMETER Configuration
    Build configuration. Defaults to "Release".

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Push -ApiKey $env:NUGET_API_KEY
    .\publish.ps1 -Version 0.2.0 -OutputDir ./out -Push
#>
[CmdletBinding()]
param(
    [string] $Version       = '',
    [string] $OutputDir     = '',
    [string] $ApiKey        = $env:NUGET_API_KEY,
    [string] $Source        = 'https://api.nuget.org/v3/index.json',
    [switch] $Push,
    [string] $Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Write-Header([string] $Message) {
    Write-Host ''
    Write-Host ('=' * 72) -ForegroundColor Cyan
    Write-Host "  $Message" -ForegroundColor Cyan
    Write-Host ('=' * 72) -ForegroundColor Cyan
}

function Invoke-Dotnet([string[]] $Arguments) {
    Write-Host "> dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
}

# ---------------------------------------------------------------------------
# Resolve paths
# ---------------------------------------------------------------------------

$RepoRoot = $PSScriptRoot
$SlnFile  = Join-Path $RepoRoot 'NetworkInspector.slnx'

if (-not (Test-Path $SlnFile)) {
    throw "Solution file not found: $SlnFile"
}

# ---------------------------------------------------------------------------
# Resolve version
# ---------------------------------------------------------------------------

if (-not $Version) {
    $BuildProps = Join-Path $RepoRoot 'Directory.Build.props'
    $Xml        = [xml](Get-Content $BuildProps -Raw)
    $Version    = $Xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) {
        throw 'Could not read <Version> from Directory.Build.props. Pass -Version explicitly.'
    }
}

# ---------------------------------------------------------------------------
# Resolve output directory
# ---------------------------------------------------------------------------

if (-not $OutputDir) {
    $OutputDir = Join-Path $RepoRoot 'artifacts'
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Write-Host ''
Write-Host "Version  : $Version"       -ForegroundColor Green
Write-Host "Config   : $Configuration" -ForegroundColor Green
Write-Host "Output   : $OutputDir"     -ForegroundColor Green
Write-Host "Push     : $Push"          -ForegroundColor Green

# ---------------------------------------------------------------------------
# Step 1 -- Clean
#
# Always clean before packing to guarantee a reproducible build. Stale
# obj/GeneratedFiles from a previous build can cause duplicate-definition
# compiler errors when EmitCompilerGeneratedFiles=true is set and the build
# system considers outputs out-of-date (e.g. after a version change).
# ---------------------------------------------------------------------------

Write-Header 'Cleaning'

Invoke-Dotnet @(
    'clean', $SlnFile,
    '--configuration', $Configuration
)

# ---------------------------------------------------------------------------
# Step 2 -- Pack
#
# dotnet pack builds and packs in one pass. Projects with IsPackable=false
# (NetworkInspector.Generators) are automatically skipped.
# Using a single solution-level pack ensures the project build graph is
# evaluated once in dependency order: Values -> Generators -> Core -> Protocols.
# ---------------------------------------------------------------------------

Write-Header 'Packing'

Invoke-Dotnet @(
    'pack', $SlnFile,
    '--configuration',  $Configuration,
    '--output',         $OutputDir,
    "/p:Version=$Version"
)

# ---------------------------------------------------------------------------
# Step 3 -- Push (optional)
# ---------------------------------------------------------------------------

if ($Push) {
    Write-Header 'Pushing to NuGet'

    if (-not $ApiKey) {
        throw 'No API key supplied. Pass -ApiKey or set the NUGET_API_KEY environment variable.'
    }

    # Push .nupkg files; the matching .snupkg files are picked up automatically by nuget.org.
    foreach ($Pkg in (Get-ChildItem -Path $OutputDir -Filter '*.nupkg')) {
        Write-Host ''
        Write-Host "  $($Pkg.Name)" -ForegroundColor Yellow

        Invoke-Dotnet @(
            'nuget', 'push', $Pkg.FullName,
            '--api-key', $ApiKey,
            '--source',  $Source,
            '--skip-duplicate'
        )
    }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

Write-Header 'Done'
Write-Host ''
Write-Host "NetworkInspector $Version -- packages in:" -ForegroundColor Green
Write-Host "  $OutputDir" -ForegroundColor Green
Write-Host ''
Write-Host 'Artifacts:' -ForegroundColor Green

foreach ($Pkg in (Get-ChildItem -Path $OutputDir -Filter '*.nupkg' | Sort-Object Name)) {
    Write-Host "  $($Pkg.Name)" -ForegroundColor White
}

Write-Host ''