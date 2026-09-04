<#
.SYNOPSIS
    Builds the release: one self-contained executable.

.DESCRIPTION
    The product is a single file. TiaOpenness.exe is the desktop app, and `TiaOpenness.exe mcp`
    the MCP server; the .NET runtime is inside it, so the target machine needs nothing installed.

    The three pieces that cannot be part of a .NET 10 assembly are embedded as resources and
    unpacked on first run (see EmbedToolchain.targets and ToolchainPayload):

      the bridge    a .NET Framework 4.8 process, because the Siemens assemblies are .NET
                    Framework only and modern .NET cannot load them
      the adapter   as sources, because Siemens does not permit redistributing the assemblies it
                    has to be compiled against
      a C# compiler so the machine with TIA Portal can build that adapter without a .NET SDK

    The version is read from Directory.Build.props so the file properties, the artifact name and
    the release tag cannot drift apart.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\publish.ps1
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
$stage = Join-Path $artifacts "publish"
$exe = Join-Path $stage "TiaOpenness.exe"
$final = Join-Path $artifacts "TiaOpenness-v$Version-$Runtime.exe"

Write-Host "==> Packaging TiaOpenness v$Version ($Runtime)" -ForegroundColor Cyan

if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# The bridge is built by an MSBuild task rather than a project reference, so `dotnet publish`
# never restores it. Restore the solution first or a clean checkout fails with NETSDK1004.
Write-Host "==> dotnet restore" -ForegroundColor Cyan
& dotnet restore (Join-Path $root "TiaOpenness.slnx")
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed (exit $LASTEXITCODE)." }

# Self-contained and single-file. No trimming: WPF does not support it.
Write-Host "==> dotnet publish" -ForegroundColor Cyan
& dotnet publish (Join-Path $root "src\TiaOpenness.Gui\TiaOpenness.Gui.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

if (-not (Test-Path $exe)) { throw "Expected $exe." }

# One file is the whole point, so anything else next to it means something leaked out of the
# single-file bundle and the artifact would be incomplete on its own.
$strays = @(Get-ChildItem $stage -File | Where-Object { $_.Name -ne "TiaOpenness.exe" })
if ($strays) {
    throw "The publish left files beside the executable, so it is not self-contained:`n  " +
          (($strays | ForEach-Object { $_.Name }) -join "`n  ")
}

Move-Item $exe $final -Force
Remove-Item $stage -Recurse -Force

$hash = (Get-FileHash $final -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $final -Leaf)" | Out-File -Encoding ascii "$final.sha256"

Write-Host ""
Write-Host "Executable : $final"
Write-Host ("Size       : {0:N1} MB" -f ((Get-Item $final).Length / 1MB))
Write-Host "SHA256     : $hash"
