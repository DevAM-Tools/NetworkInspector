# Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

<#
.SYNOPSIS
    Packs and optionally publishes the NetworkInspector NuGet packages.

.DESCRIPTION
    1. Resolves the package version from Directory.Build.props (or -Version).
    2. Cleans all build outputs to ensure a reproducible, artifact-free build.
    3. Empties (or creates) the output directory so no stale artefacts can
       be pushed by accident.
    4. Packs every project in the explicit release allowlist:
         NetworkInspector.Values
         NetworkInspector.Core      (includes the bundled Generators analyzer)
         NetworkInspector.Protocols
         NetworkInspector.FrameBuilder
         NetworkInspector.Sources
         NetworkInspector.Exporters
         NetworkInspector.CLI       (published as the 'ni' dotnet tool)
    5. Optionally pushes the resulting .nupkg and .snupkg files to nuget.org.

    NetworkInspector.Generators is marked IsPackable=false and is intentionally
    not packed -- its analyzer DLL is embedded inside NetworkInspector.Core.
    NetworkInspector.Playground and NetworkInspector.Profiling are developer-only
    tools; they are not part of the public release surface.

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

# Clear the output directory so no stale artefacts from a previous run can
# slip into the push. Create it if it does not exist yet.
if (Test-Path $OutputDir) {
    Get-ChildItem -Path $OutputDir -File | Remove-Item -Force
} else {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

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
# Each project in the release allowlist is packed individually so that only
# the intended packages land in the output directory. Solution-level pack is
# intentionally avoided here: it would include developer-only executables
# (Playground, Profiling) and could pull in stale artefacts from previous
# version runs.
# NetworkInspector.Core is packed last so that the Generators DLL (built
# during the Values/Core graph walk) is already present when the
# analyzers/dotnet/cs path is resolved.
# ---------------------------------------------------------------------------

$ProjectsToPack = @(
    'NetworkInspector.Values/NetworkInspector.Values.csproj',
    'NetworkInspector.Core/NetworkInspector.Core.csproj',
    'NetworkInspector.Protocols/NetworkInspector.Protocols.csproj',
    'NetworkInspector.FrameBuilder/NetworkInspector.FrameBuilder.csproj',
    'NetworkInspector.Sources/NetworkInspector.Sources.csproj',
    'NetworkInspector.Exporters/NetworkInspector.Exporters.csproj',
    'NetworkInspector.CLI/NetworkInspector.CLI.csproj'
)

Write-Header 'Packing'

foreach ($Project in $ProjectsToPack) {
    $ProjectPath = Join-Path $RepoRoot $Project
    Invoke-Dotnet @(
        'pack', $ProjectPath,
        '--configuration',  $Configuration,
        '--output',         $OutputDir,
        "/p:Version=$Version"
    )
}

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