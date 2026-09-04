<#
.SYNOPSIS
    Enables the real TIA Portal backend in an unpacked release. Run once, on the machine that
    has TIA Portal installed.

.DESCRIPTION
    Everything else in the release works the moment it is unzipped. The one piece that cannot
    ship prebuilt is the Openness adapter: it must be compiled against the Siemens.Engineering
    assemblies, and Siemens does not permit redistributing those - even their own NuGet package
    resolves them from the local installation. So no build machine without TIA Portal, including
    the CI that produced this package, can produce that adapter.

    This script closes that gap on your machine:

      1. finds your TIA Portal installation (registry first, then the default install layout)
      2. compiles adapter\*.cs against its assemblies, using the C# compiler in compiler\
      3. writes bridge\TiaOpenness.Openness.dll, which is where the bridge looks

    Nothing else is needed - no .NET SDK, no Visual Studio, no source clone. The compiler is
    bundled precisely so this stays a single command on an engineering workstation.

    The adapter is compiled against the real assemblies rather than loaded by reflection, so a
    signature this version of TIA does not have becomes a compile error naming the file and line,
    instead of an obscure failure hours later at run time.

.PARAMETER TiaPortalLocation
    Use this TIA Portal installation instead of searching, e.g.
    'C:\Program Files\Siemens\Automation\Portal V21'. Same meaning as the environment variable
    of that name honoured by Siemens' own NuGet package.

.PARAMETER Version
    Openness version to build against, e.g. 21.0. Defaults to the newest installed.

.PARAMETER Force
    Rebuild even when bridge\TiaOpenness.Openness.dll is already present.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\enable-openness.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\enable-openness.ps1 -Version 21.0

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\enable-openness.ps1 -TiaPortalLocation 'D:\Siemens\Automation\Portal V21'
#>
[CmdletBinding()]
param(
    [string] $TiaPortalLocation = $env:TiaPortalLocation,
    [string] $Version,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

# Every failure here is something an operator can act on, so report it as a sentence rather than
# a PowerShell exception dump.
trap {
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# The package root is this script's folder when it ships at the top of the zip, and the repo
# root's parent when it is run from tools\ in a source checkout.
$here = $PSScriptRoot
$package = if (Test-Path (Join-Path $here 'bridge\TiaOpenness.Bridge.exe')) { $here }
           elseif (Test-Path (Join-Path (Split-Path $here -Parent) 'bridge\TiaOpenness.Bridge.exe')) { Split-Path $here -Parent }
           else { $here }

# In a release package this script sits at the root and the module in tools\; in a source
# checkout both live in tools\. Look in both rather than assuming one layout.
$discovery = @(
    (Join-Path $here 'Openness.Discovery.ps1')
    (Join-Path $here 'tools\Openness.Discovery.ps1')
    (Join-Path $package 'tools\Openness.Discovery.ps1')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $discovery) {
    throw "Cannot find Openness.Discovery.ps1 next to this script or under tools\. " +
          "Run this script from the folder you unzipped the release into, without moving its contents."
}
. $discovery

# ---- locate the pieces ------------------------------------------------------

$bridgeDir  = Join-Path $package 'bridge'
$adapterDir = Join-Path $package 'adapter'
$compiler   = Join-Path $package 'compiler\csc.exe'
$output     = Join-Path $bridgeDir 'TiaOpenness.Openness.dll'

foreach ($required in @(
    @{ Path = $bridgeDir;  What = "the bridge folder" },
    @{ Path = $adapterDir; What = "the adapter sources" },
    @{ Path = $compiler;   What = "the bundled C# compiler" })) {

    if (-not (Test-Path $required.Path)) {
        throw "Cannot find $($required.What) at $($required.Path). Run this script from the folder " +
              "you unzipped the release into, without moving its contents."
    }
}

if ((Test-Path $output) -and -not $Force) {
    Write-Host "The Openness adapter is already built:"
    Write-Host "  $output"
    Write-Host ""
    Write-Host "Re-run with -Force to rebuild it (do that after upgrading TIA Portal)."
    exit 0
}

# ---- pick the TIA Portal installation ---------------------------------------

$all = @(Find-OpennessInstallations)
if ($all.Count -gt 0) { Write-OpennessInstallations $all; Write-Host "" }

$selected = Select-OpennessInstallation -Version $Version -TiaPortalLocation $TiaPortalLocation
Write-Host "Building against V$($selected.Version) in $($selected.Directory)" -ForegroundColor Cyan

$siemens = @(Get-ChildItem -Path $selected.Directory -Filter 'Siemens.Engineering*.dll' -File)
if ($siemens.Count -eq 0) {
    throw "No Siemens.Engineering* assemblies in $($selected.Directory)."
}
Write-Host "  referencing $($siemens.Count) Siemens assembly/assemblies"

# ---- compile ----------------------------------------------------------------

# netstandard.dll is the facade that lets net48 code consume the netstandard2.0 contracts
# assembly. It ships with .NET Framework 4.7.2 and later, so it is beside the other framework
# assemblies on any machine that can run the bridge at all.
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$frameworkRefs = @('System.dll', 'System.Core.dll', 'netstandard.dll') |
    ForEach-Object { Join-Path $framework $_ }

foreach ($ref in $frameworkRefs) {
    if (-not (Test-Path $ref)) {
        throw "Missing $ref. The bridge needs .NET Framework 4.8; install it and re-run."
    }
}

$sources = @(Get-ChildItem -Path (Join-Path $adapterDir '*.cs') -File)
if ($sources.Count -eq 0) { throw "No .cs files in $adapterDir." }

$arguments = @(
    '/noconfig', '/nologo', '/target:library', '/platform:x64',
    '/langversion:latest', '/optimize+', '/debug-',
    "/out:$output"
) +
    ($frameworkRefs | ForEach-Object { "/r:$_" }) +
    (@('TiaOpenness.Core.dll', 'TiaOpenness.Contracts.dll', 'Newtonsoft.Json.dll') |
        ForEach-Object { "/r:$(Join-Path $bridgeDir $_)" }) +
    ($siemens | ForEach-Object { "/r:$($_.FullName)" }) +
    ($sources | ForEach-Object { $_.FullName })

Write-Host "  compiling $($sources.Count) source file(s)"
Write-Host ""

$log = & $compiler @arguments 2>&1
$failed = $LASTEXITCODE -ne 0

if ($failed) {
    $errors = @($log | Where-Object { $_ -match 'error CS' })

    Write-Host "The adapter did not compile against TIA Portal V$($selected.Version)." -ForegroundColor Red
    Write-Host ""
    $errors | Select-Object -First 25 | ForEach-Object { Write-Host "  $_" }
    if ($errors.Count -gt 25) { Write-Host "  ... and $($errors.Count - 25) more" }

    Write-Host ""
    Write-Host "Each error names a file and line in adapter\. An error about a missing Siemens" -ForegroundColor Yellow
    Write-Host "type or member means this build expects an API your TIA version does not have." -ForegroundColor Yellow
    Write-Host "Please report it with the version above and the first few errors:" -ForegroundColor Yellow
    Write-Host "  https://github.com/asckye/tia-openness-studio/issues"

    if (Test-Path $output) { Remove-Item $output -Force }
    exit 1
}

# ---- record what was built and verify ---------------------------------------

@(
    "version=$($selected.Version)"
    "modular=$($selected.Modular)"
    "source=$($selected.Directory)"
    "built=$(Get-Date -Format o)"
) | Set-Content -Path (Join-Path $bridgeDir 'OPENNESS_ADAPTER.txt') -Encoding utf8

Write-Host "Built $output" -ForegroundColor Green
Write-Host ""

$cli = Join-Path $package 'tia.exe'
if (Test-Path $cli) {
    Write-Host "Checking the environment..." -ForegroundColor Cyan
    & $cli doctor
    Write-Host ""
    Write-Host "If a check above failed, fix it and re-run 'tia doctor'. The adapter itself is built."
} else {
    Write-Host "Next: run 'tia doctor' to check the remaining preconditions."
}
