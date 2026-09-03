<#
.SYNOPSIS
    Copies the Siemens Openness assemblies into lib\ so TiaOpenness.Openness can compile.

.DESCRIPTION
    Siemens ships the Openness assemblies only inside the TIA Portal installation and does not
    redistribute them - the official NuGet package resolves them from the local install too.
    Run this once on a machine that has TIA Portal. The copies in lib\ are compile-time
    references only; at run time the bridge loads the assemblies from the install directory
    through OpennessAssemblyResolver.

    Handles both layouts:
      V15.1 - V20   ...\Portal V20\PublicAPI\V20\Siemens.Engineering.dll          (one assembly)
      V21+          ...\Portal V21\PublicAPI\V21\net48\Siemens.Engineering.*.dll  (several)

    The registry scheme mirrors Siemens' own ReferenceSiemensEngineeringAssemblies.targets.

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

function New-Installation {
    param([string] $Version, [string] $Directory)

    if (-not (Test-Path $Directory)) { return $null }

    $modular = Join-Path $Directory 'Siemens.Engineering.Base.dll'
    $monolithic = Join-Path $Directory 'Siemens.Engineering.dll'

    if (Test-Path $modular) {
        return [pscustomobject]@{ Version = $Version; Directory = $Directory; Modular = $true }
    }
    if (Test-Path $monolithic) {
        return [pscustomobject]@{ Version = $Version; Directory = $Directory; Modular = $false }
    }
    return $null
}

function Get-OpennessInstallations {
    $results = @()

    # 1. Registry, both views.
    foreach ($hive in @('HKLM:\SOFTWARE\Siemens\Automation\Openness',
                        'HKLM:\SOFTWARE\WOW6432Node\Siemens\Automation\Openness')) {
        if (-not (Test-Path $hive)) { continue }

        foreach ($versionKey in Get-ChildItem $hive -ErrorAction SilentlyContinue) {
            $publicApi = Join-Path $versionKey.PSPath 'PublicAPI'
            if (-not (Test-Path $publicApi)) { continue }

            foreach ($apiKey in Get-ChildItem $publicApi -ErrorAction SilentlyContinue) {
                # V21: PublicAPI\21.0.0.0\net48, named value Siemens.Engineering.Base.
                $net48 = Join-Path $apiKey.PSPath 'net48'
                if (Test-Path $net48) {
                    $dll = (Get-ItemProperty -Path $net48 -ErrorAction SilentlyContinue).'Siemens.Engineering.Base'
                    if ($dll -and (Test-Path $dll)) {
                        $found = New-Installation $versionKey.PSChildName (Split-Path $dll -Parent)
                        if ($found) { $results += $found; continue }
                    }
                }

                # V20 and earlier: the DLL path is the key's default value.
                $dll = (Get-ItemProperty -Path $apiKey.PSPath -ErrorAction SilentlyContinue).'(default)'
                if ($dll -and (Test-Path $dll)) {
                    $found = New-Installation $versionKey.PSChildName (Split-Path $dll -Parent)
                    if ($found) { $results += $found }
                }
            }
        }
    }

    # 2. Default install layout, as a fallback for damaged registry entries.
    $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }
    foreach ($root in $roots) {
        $automation = Join-Path $root 'Siemens\Automation'
        if (-not (Test-Path $automation)) { continue }

        foreach ($portal in Get-ChildItem -Path $automation -Filter 'Portal V*' -Directory -ErrorAction SilentlyContinue) {
            $publicApi = Join-Path $portal.FullName 'PublicAPI'
            if (-not (Test-Path $publicApi)) { continue }

            foreach ($apiDir in Get-ChildItem -Path $publicApi -Directory -ErrorAction SilentlyContinue) {
                $ver = $apiDir.Name.TrimStart('V', 'v')
                if ($ver -notmatch '\.') { $ver = "$ver.0" }

                $found = (New-Installation $ver (Join-Path $apiDir.FullName 'net48'))
                if (-not $found) { $found = New-Installation $ver $apiDir.FullName }
                if ($found) { $results += $found }
            }
        }
    }

    $results |
        Group-Object Directory |
        ForEach-Object { $_.Group[0] } |
        Sort-Object -Property @{ Expression = { [version](($_.Version -split '\.')[0..1] -join '.') } } -Descending
}

# An explicit installation directory wins over any search.
if ($TiaPortalLocation) {
    $candidate = Get-ChildItem -Path (Join-Path $TiaPortalLocation 'PublicAPI') -Directory -ErrorAction SilentlyContinue |
                 ForEach-Object {
                     $v = $_.Name.TrimStart('V', 'v'); if ($v -notmatch '\.') { $v = "$v.0" }
                     $hit = New-Installation $v (Join-Path $_.FullName 'net48')
                     if (-not $hit) { $hit = New-Installation $v $_.FullName }
                     $hit
                 } | Where-Object { $_ } | Select-Object -First 1

    if (-not $candidate) {
        throw "No Openness assemblies under '$TiaPortalLocation\PublicAPI'. Point -TiaPortalLocation at the TIA Portal install directory, e.g. 'C:\Program Files\Siemens\Automation\Portal V21'."
    }
    $installations = @($candidate)
} else {
    $installations = @(Get-OpennessInstallations)
}

if ($installations.Count -eq 0) {
    throw "No TIA Portal Openness installation found. Install TIA Portal with the Openness option, then re-run this script (or pass -TiaPortalLocation)."
}

Write-Host "Found Openness installation(s):"
$installations | ForEach-Object {
    Write-Host ("  V{0,-6} {1} ({2})" -f $_.Version, $_.Directory, $(if ($_.Modular) { 'modular' } else { 'monolithic' }))
}

$selected = if ($Version) {
    $normalized = ($Version.TrimStart('V', 'v') -split '\.')[0..1] -join '.'
    $match = $installations | Where-Object { (($_.Version.TrimStart('V','v') -split '\.')[0..1] -join '.') -eq $normalized } | Select-Object -First 1
    if (-not $match) { throw "Openness version '$Version' is not installed." }
    $match
} else {
    $installations[0]
}

if (-not (Test-Path $LibDirectory)) { New-Item -ItemType Directory -Path $LibDirectory | Out-Null }
$LibDirectory = (Resolve-Path $LibDirectory).Path

# Take every Siemens.Engineering* assembly the installation ships, the way Siemens' own
# build targets do, rather than a hardcoded list that would miss V21's modular split.
$assemblies = @(Get-ChildItem -Path $selected.Directory -Filter 'Siemens.Engineering*.dll' -File)
if ($assemblies.Count -eq 0) {
    throw "No Siemens.Engineering* assemblies in $($selected.Directory)."
}

Write-Host ""
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
