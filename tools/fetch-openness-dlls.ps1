<#
.SYNOPSIS
    Copies the Siemens Openness assemblies into lib\ so TiaOpenness.Openness can compile
    from source. For developers building the repo.

.DESCRIPTION
    Siemens ships the Openness assemblies only inside the TIA Portal installation and does not
    redistribute them - their own NuGet package resolves them from the local install too. Run
    this once on a machine that has TIA Portal. The copies in lib\ are compile-time references
    only; at run time the bridge loads the assemblies from the install directory through
    OpennessAssemblyResolver.

    Users of a *release package* do not need this: run enable-openness.ps1 from the unzipped
    folder instead, which compiles the adapter in place with the bundled compiler and needs no
    .NET SDK.

    Handles both layouts:
      V15.1 - V20   ...\Portal V20\PublicAPI\V20\Siemens.Engineering.dll          (one assembly)
      V21+          ...\Portal V21\PublicAPI\V21\net48\Siemens.Engineering.*.dll  (several)

.PARAMETER Version
    Openness version to take, e.g. 21.0. Defaults to the newest installed.

.PARAMETER TiaPortalLocation
    Use this TIA Portal installation directory instead of searching. Same meaning as the
    environment variable of that name honoured by the Siemens NuGet package.

.EXAMPLE
    .\tools\fetch-openness-dlls.ps1
    .\tools\fetch-openness-dlls.ps1 -Version 21.0
    .\tools\fetch-openness-dlls.ps1 -TiaPortalLocation 'C:\Program Files\Siemens\Automation\Portal V21'
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $TiaPortalLocation = $env:TiaPortalLocation,
    [string] $LibDirectory = (Join-Path $PSScriptRoot '..\lib')
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Openness.Discovery.ps1')

$all = @(Find-OpennessInstallations)
if ($all.Count -gt 0) { Write-OpennessInstallations $all; Write-Host "" }

$selected = Select-OpennessInstallation -Version $Version -TiaPortalLocation $TiaPortalLocation

if (-not (Test-Path $LibDirectory)) { New-Item -ItemType Directory -Path $LibDirectory | Out-Null }
$LibDirectory = (Resolve-Path $LibDirectory).Path

# Take every Siemens.Engineering* assembly the installation ships, the way Siemens' own build
# targets do, rather than a hardcoded list that would miss V21's modular split.
$assemblies = @(Get-ChildItem -Path $selected.Directory -Filter 'Siemens.Engineering*.dll' -File)
if ($assemblies.Count -eq 0) {
    throw "No Siemens.Engineering* assemblies in $($selected.Directory)."
}

Write-Host "Copying $($assemblies.Count) assembly/assemblies from V$($selected.Version) into $LibDirectory"
foreach ($assembly in $assemblies) {
    Copy-Item -Path $assembly.FullName -Destination $LibDirectory -Force
    Write-Host "  copied   $($assembly.Name)"
}

@(
    "version=$($selected.Version)"
    "modular=$($selected.Modular)"
    "source=$($selected.Directory)"
    "fetched=$(Get-Date -Format o)"
) | Set-Content -Path (Join-Path $LibDirectory 'OPENNESS_VERSION.txt') -Encoding utf8

Write-Host ""
Write-Host "Done. Now run:  dotnet build TiaOpenness.slnx -c Release"
Write-Host "The Openness adapter builds automatically once lib\ holds those assemblies."
