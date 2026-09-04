<#
.SYNOPSIS
    Builds the release package: one folder with the three front ends and the bridge, zipped.

.DESCRIPTION
    Each front end is published as a self-contained single-file exe so the target machine needs
    no .NET 10 install. The bridge is deliberately NOT bundled into them: it is a .NET Framework
    4.8 x64 process (the Openness assemblies cannot be loaded by modern .NET) and has to remain a
    separate exe next to the front ends, in bridge\, where BridgeClient.LocateBridge looks.

    The version is read from Directory.Build.props so the exe file properties, the zip name and
    the release tag cannot drift apart.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\publish.ps1
    powershell -ExecutionPolicy Bypass -File build\publish.ps1 -Runtime win-x64 -Configuration Release
#>
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$Version
)

$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$props = Join-Path $root "Directory.Build.props"

if (-not $Version) {
    $match = [regex]::Match((Get-Content $props -Raw), "<Version>([^<]+)</Version>")
    if (-not $match.Success) { throw "No <Version> in $props" }
    $Version = $match.Groups[1].Value.Trim()
}

$artifacts = Join-Path $root "artifacts"
$name = "TiaOpennessStudio-v$Version-$Runtime"
$stage = Join-Path $artifacts $name
$zip = Join-Path $artifacts "$name.zip"

Write-Host "==> Packaging $name" -ForegroundColor Cyan

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# Self-contained + single file: one exe per front end, runtime included, compressed.
# No trimming - WPF does not support it.
$publishArgs = @(
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=none",
    "-p:DebugSymbols=false",
    "-p:Version=$Version",
    "-o", $stage
)

# The bridge is built by an MSBuild task from BridgeDeploy.targets, not as a project reference,
# so `dotnet publish` never restores it. Restore the solution first or a clean checkout fails
# with NETSDK1004 the moment that nested build runs.
Write-Host "==> dotnet restore" -ForegroundColor Cyan
& dotnet restore (Join-Path $root "TiaOpenness.slnx")
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed (exit $LASTEXITCODE)." }

foreach ($project in "TiaOpenness.Gui", "TiaOpenness.Cli", "TiaOpenness.Mcp") {
    Write-Host "==> dotnet publish $project" -ForegroundColor Cyan
    & dotnet publish (Join-Path $root "src\$project\$project.csproj") @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project (exit $LASTEXITCODE)" }
}

if (-not (Test-Path (Join-Path $stage "bridge\TiaOpenness.Bridge.exe"))) {
    throw "The bridge was not published next to the front ends; check build\BridgeDeploy.targets."
}

# What an operator needs on the target machine, beside the binaries.
Copy-Item (Join-Path $root "README.md") $stage
Copy-Item (Join-Path $root "LICENSE") $stage
New-Item -ItemType Directory -Force -Path (Join-Path $stage "docs") | Out-Null
Copy-Item (Join-Path $root "docs\*.md") (Join-Path $stage "docs")

# ---- what makes the real backend reachable from a release --------------------
#
# TiaOpenness.Openness.dll cannot ship prebuilt: it has to be compiled against the Siemens
# assemblies, and those are not redistributable, so no build machine without TIA Portal - this
# one included - can produce it. Instead the package carries the adapter sources and a C#
# compiler, and enable-openness.ps1 compiles them in place on the engineering workstation.
# That keeps it a single command there: no .NET SDK, no Visual Studio, no source clone.

Write-Host "==> Staging the Openness adapter sources" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path (Join-Path $stage "adapter") | Out-Null
Copy-Item (Join-Path $root "src\TiaOpenness.Openness\*.cs") (Join-Path $stage "adapter")

New-Item -ItemType Directory -Force -Path (Join-Path $stage "tools") | Out-Null
Copy-Item (Join-Path $root "tools\Openness.Discovery.ps1") (Join-Path $stage "tools")
Copy-Item (Join-Path $root "tools\enable-openness.ps1") $stage

Write-Host "==> Staging the C# compiler" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "fetch-compiler.ps1") -Destination (Join-Path $stage "compiler")
if ($LASTEXITCODE -ne 0) { throw "Staging the compiler failed (exit $LASTEXITCODE)." }

Write-Host "==> Zipping" -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name.zip" | Out-File -Encoding ascii (Join-Path $artifacts "$name.zip.sha256")

Write-Host ""
Write-Host "Package : $zip"
Write-Host ("Size    : {0:N1} MB" -f ((Get-Item $zip).Length / 1MB))
Write-Host "SHA256  : $hash"
Write-Host ""
Get-ChildItem $stage -Recurse -File |
    Where-Object { $_.Extension -eq ".exe" } |
    ForEach-Object { "{0,10:N1} MB  {1}" -f ($_.Length / 1MB), $_.FullName.Substring($stage.Length + 1) }
