<#
.SYNOPSIS
    Stages a standalone C# compiler into the release package.

.DESCRIPTION
    The release carries a compiler because the Openness adapter has to be built on the machine
    that has TIA Portal (see enable-openness.ps1), and an engineering workstation cannot be
    assumed to have the .NET SDK or Visual Studio. Every Windows machine does have
    C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe, but that is the C# 5 compiler and
    the adapter uses later language features, so Roslyn is bundled instead.

    Microsoft.Net.Compilers.Toolset is the supported way to get a standalone Roslyn: it ships
    the same csc.exe MSBuild uses, under the MIT licence, with no install step. Only the C#
    pieces are kept - the VB compiler, the scripting host, the build tasks and the non-x64
    debugger shims come to roughly 9 MB that nothing here would load.

.PARAMETER Destination
    Folder to stage the compiler into. Created if missing, replaced if present.

.PARAMETER PackageVersion
    Microsoft.Net.Compilers.Toolset version. Pinned so a package build is reproducible.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Destination,
    [string] $PackageVersion = "4.14.0"
)

$ErrorActionPreference = "Stop"

$package = "microsoft.net.compilers.toolset"
$temp = Join-Path ([System.IO.Path]::GetTempPath()) "tia-csc-$PackageVersion"
$nupkg = Join-Path $temp "$package.$PackageVersion.nupkg"

if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
New-Item -ItemType Directory -Force -Path $temp | Out-Null

# Cache the download so repeated local packaging runs do not re-fetch 21 MB.
if (-not (Test-Path $nupkg)) {
    $url = "https://api.nuget.org/v3-flatcontainer/$package/$PackageVersion/$package.$PackageVersion.nupkg"
    Write-Host "    downloading $package $PackageVersion"
    $previous = $ProgressPreference
    $ProgressPreference = "SilentlyContinue"   # the progress bar makes this ~10x slower
    try { Invoke-WebRequest -Uri $url -OutFile $nupkg -UseBasicParsing }
    finally { $ProgressPreference = $previous }
}

$extracted = Join-Path $temp "x"
if (Test-Path $extracted) { Remove-Item $extracted -Recurse -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $extracted)

# tasks\net472 is the .NET Framework build of Roslyn - the one that runs without a .NET install.
$source = Join-Path $extracted "tasks\net472"
if (-not (Test-Path (Join-Path $source "csc.exe"))) {
    throw "csc.exe not found in $source; the package layout changed."
}

# Everything csc.exe itself loads, and nothing else.
$keep = @(
    "csc.exe", "csc.exe.config",
    "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll",
    "Microsoft.DiaSymReader.Native.amd64.dll",
    "System.Buffers.dll", "System.Collections.Immutable.dll", "System.Memory.dll",
    "System.Numerics.Vectors.dll", "System.Reflection.Metadata.dll",
    "System.Runtime.CompilerServices.Unsafe.dll", "System.Text.Encoding.CodePages.dll",
    "System.Threading.Tasks.Extensions.dll"
)

foreach ($file in $keep) {
    $path = Join-Path $source $file
    if (-not (Test-Path $path)) { throw "Expected $file in the compiler package." }
    Copy-Item $path $Destination
}

# The package's licence has to travel with the binaries it covers.
$licence = Get-ChildItem $extracted -Filter "*.txt" -File |
    Where-Object { $_.Name -match "license|licence" } | Select-Object -First 1
if ($licence) { Copy-Item $licence.FullName (Join-Path $Destination "LICENSE-roslyn.txt") }

@(
    "Microsoft.Net.Compilers.Toolset $PackageVersion (MIT), C# compiler only."
    "Bundled so enable-openness.ps1 can build the Openness adapter on a machine"
    "with TIA Portal but no .NET SDK. Nothing else in this package uses it."
) | Set-Content -Path (Join-Path $Destination "README.txt") -Encoding utf8

$size = (Get-ChildItem $Destination -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("    staged csc {0} ({1:N1} MB)" -f $PackageVersion, $size)

# Prove it runs here rather than discovering on the target machine that it does not.
$version = & (Join-Path $Destination "csc.exe") /version 2>&1
if ($LASTEXITCODE -ne 0) { throw "The staged compiler does not run: $version" }
Write-Host "    csc reports $version"
