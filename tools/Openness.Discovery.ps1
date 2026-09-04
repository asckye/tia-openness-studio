<#
.SYNOPSIS
    Finds TIA Portal Openness installations. Dot-source this; it defines functions only.

.DESCRIPTION
    Shared by tools\fetch-openness-dlls.ps1 (developer machine, populates lib\) and
    enable-openness.ps1 (release package, compiles the adapter in place), so the two cannot
    drift apart on the one thing that is easy to get wrong.

    Two layouts exist and both are handled:

      V15.1 - V20   ...\Portal V20\PublicAPI\V20\Siemens.Engineering.dll         one assembly
      V21+          ...\Portal V21\PublicAPI\V21\net48\Siemens.Engineering.*.dll several

    The registry scheme mirrors Siemens' own ReferenceSiemensEngineeringAssemblies.targets:
    V21 moved the path into a *named* value, Siemens.Engineering.Base, under a four-part
    version and a net48 key, where earlier versions used the API key's default value.
#>

function New-OpennessInstallation {
    param([string] $Version, [string] $Directory)

    if (-not $Directory -or -not (Test-Path $Directory)) { return $null }

    if (Test-Path (Join-Path $Directory 'Siemens.Engineering.Base.dll')) {
        return [pscustomobject]@{ Version = $Version; Directory = $Directory; Modular = $true }
    }
    if (Test-Path (Join-Path $Directory 'Siemens.Engineering.dll')) {
        return [pscustomobject]@{ Version = $Version; Directory = $Directory; Modular = $false }
    }
    return $null
}

function ConvertTo-OpennessMajorMinor {
    param([string] $Version)
    $parts = $Version.TrimStart('V', 'v') -split '\.'
    if ($parts.Count -ge 2) { return "$($parts[0]).$($parts[1])" }
    return "$($parts[0]).0"
}

<#
.SYNOPSIS
    Every Openness installation on this machine, newest first.
#>
function Find-OpennessInstallations {
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
                        $found = New-OpennessInstallation $versionKey.PSChildName (Split-Path $dll -Parent)
                        if ($found) { $results += $found; continue }
                    }
                }

                # V20 and earlier: the DLL path is the key's default value.
                $dll = (Get-ItemProperty -Path $apiKey.PSPath -ErrorAction SilentlyContinue).'(default)'
                if ($dll -and (Test-Path $dll)) {
                    $found = New-OpennessInstallation $versionKey.PSChildName (Split-Path $dll -Parent)
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
            $results += Get-OpennessUnderPortal $portal.FullName
        }
    }

    $results |
        Where-Object { $_ } |
        Group-Object Directory |
        ForEach-Object { $_.Group[0] } |
        Sort-Object -Property @{ Expression = { [version](ConvertTo-OpennessMajorMinor $_.Version) } } -Descending
}

<#
.SYNOPSIS
    Openness installations under one TIA Portal directory, e.g. C:\...\Portal V21.
#>
function Get-OpennessUnderPortal {
    param([Parameter(Mandatory)] [string] $PortalDirectory)

    $publicApi = Join-Path $PortalDirectory 'PublicAPI'
    if (-not (Test-Path $publicApi)) { return @() }

    Get-ChildItem -Path $publicApi -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $version = ConvertTo-OpennessMajorMinor $_.Name
        # net48 first: on V21 both folders exist and only the inner one has the assemblies.
        $hit = New-OpennessInstallation $version (Join-Path $_.FullName 'net48')
        if (-not $hit) { $hit = New-OpennessInstallation $version $_.FullName }
        $hit
    } | Where-Object { $_ }
}

<#
.SYNOPSIS
    Picks one installation, honouring an explicit -TiaPortalLocation or -Version.
.DESCRIPTION
    Throws with a message an operator can act on rather than returning $null.
#>
function Select-OpennessInstallation {
    param(
        [string] $Version,
        [string] $TiaPortalLocation
    )

    if ($TiaPortalLocation) {
        $hit = @(Get-OpennessUnderPortal $TiaPortalLocation) | Select-Object -First 1
        if (-not $hit) {
            throw "No Openness assemblies under '$TiaPortalLocation\PublicAPI'. " +
                  "Point -TiaPortalLocation at the TIA Portal install directory, " +
                  "e.g. 'C:\Program Files\Siemens\Automation\Portal V21'."
        }
        return $hit
    }

    $all = @(Find-OpennessInstallations)
    if ($all.Count -eq 0) {
        throw "No TIA Portal Openness installation found. Install TIA Portal with the Openness " +
              "option, then re-run this script (or pass -TiaPortalLocation)."
    }

    if (-not $Version) { return $all[0] }

    $wanted = ConvertTo-OpennessMajorMinor $Version
    $hit = $all | Where-Object { (ConvertTo-OpennessMajorMinor $_.Version) -eq $wanted } | Select-Object -First 1
    if (-not $hit) {
        throw "Openness version '$Version' is not installed. Found: " +
              (($all | ForEach-Object { "V$($_.Version)" }) -join ', ')
    }
    return $hit
}

<#
.SYNOPSIS
    Prints what was found, so a wrong pick is obvious before anything is built.
#>
function Write-OpennessInstallations {
    param([object[]] $Installations)

    Write-Host "Found Openness installation(s):"
    foreach ($i in $Installations) {
        Write-Host ("  V{0,-6} {1} ({2})" -f $i.Version, $i.Directory,
            $(if ($i.Modular) { 'modular, V21+' } else { 'monolithic, V20 and earlier' }))
    }
}
