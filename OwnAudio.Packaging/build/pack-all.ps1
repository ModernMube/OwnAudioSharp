#Requires -Version 7.0
<#
.SYNOPSIS
    Builds all three OwnAudioSharp NuGet packages.

.DESCRIPTION
    Packs the real OwnAudio/Source/OwnaudioNET*.csproj projects (which compile the
    OwnaudioNET public API). Native binaries must already be present under
    OwnAudioEngine/OwnAudioRust/runtimes/{rid}/native/ before invoking this script —
    the CI update-runtimes job commits them there. The old OwnAudio.Packaging wrapper
    projects produced truncated, API-less packages and have been removed.

.PARAMETER Version
    Override the package version.  If omitted, reads from ../../version.json or
    the OWNAUDIO_VERSION environment variable.

.EXAMPLE
    .\pack-all.ps1
    .\pack-all.ps1 -Version 1.2.3
#>
param(
    [string] $Version = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ScriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot     = Resolve-Path (Join-Path $ScriptDir "../..")
$PackagingRoot = Resolve-Path (Join-Path $ScriptDir "..")
$SrcRoot      = Resolve-Path (Join-Path $RepoRoot "OwnAudio/Source")
$NupkgOut     = Join-Path $PackagingRoot "nupkg"

# ---------------------------------------------------------------------------
# Resolve the package version
# ---------------------------------------------------------------------------
# The real projects carry their version in the csproj <Version> (source of truth).
# Only override it when an explicit version is supplied (-Version or OWNAUDIO_VERSION);
# otherwise the csproj value stands. This avoids desyncing the Full package from its
# OwnAudioSharp.Midi dependency and the stale version.json footgun.
$VersionArgs = @()
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $VersionArgs = @("-p:Version=$Version")
    $DisplayVer  = "$Version (override)"
}
elseif ($env:OWNAUDIO_VERSION) {
    $VersionArgs = @("-p:Version=$($env:OWNAUDIO_VERSION)")
    $DisplayVer  = "$($env:OWNAUDIO_VERSION) (override)"
}
else {
    $DisplayVer = "(from each csproj <Version>)"
}

Write-Host "Packaging OwnAudioSharp v$DisplayVer"
Write-Host "Output dir:     $NupkgOut"
Write-Host ""

New-Item -ItemType Directory -Force -Path $NupkgOut | Out-Null

# ---------------------------------------------------------------------------
# Helper: pack one project
# ---------------------------------------------------------------------------
function Invoke-Pack {
    param([string] $CsProj)
    Write-Host "--- Packing: $(Split-Path -Leaf $CsProj) ---"
    dotnet pack $CsProj `
        --configuration Release `
        --no-restore `
        "-p:GeneratePackageOnBuild=false" `
        @VersionArgs `
        --output $NupkgOut
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $CsProj" }
}

# ---------------------------------------------------------------------------
# Restore once for each project
# ---------------------------------------------------------------------------
Write-Host "Restoring dependencies..."
dotnet restore "$SrcRoot\OwnaudioNET.Basic.csproj"
dotnet restore "$SrcRoot\OwnaudioNET.csproj"

# ---------------------------------------------------------------------------
# Pack all three packages
# ---------------------------------------------------------------------------
Invoke-Pack "$SrcRoot\OwnaudioNET.Basic.csproj"
Invoke-Pack "$SrcRoot\OwnaudioNET.csproj"

$workloads = dotnet workload list 2>&1
if ($workloads -match "android|ios") {
    dotnet restore "$SrcRoot\OwnaudioNET.Mobile.csproj"
    Invoke-Pack "$SrcRoot\OwnaudioNET.Mobile.csproj"
}
else {
    Write-Warning "Android/iOS workloads not installed — skipping OwnaudioNET.Mobile pack."
}

Write-Host ""
Write-Host "Done. Packages written to: $NupkgOut"
Get-ChildItem "$NupkgOut\*.nupkg" | Select-Object Name, Length | Format-Table
