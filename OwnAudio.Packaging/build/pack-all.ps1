#Requires -Version 7.0
<#
.SYNOPSIS
    Builds all three OwnAudioSharp NuGet packages.

.DESCRIPTION
    Calls dotnet pack for OwnAudioSharp.Basic, OwnAudioSharp, and OwnAudioSharp.Mobile.
    Native artifacts must already be present under ../../artifacts/{rid}/ before invoking
    this script — the CI rust-build jobs populate that directory.

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
$NupkgOut     = Join-Path $PackagingRoot "nupkg"

# ---------------------------------------------------------------------------
# Resolve the package version
# ---------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($Version)) {
    if ($env:OWNAUDIO_VERSION) {
        $Version = $env:OWNAUDIO_VERSION
    }
    elseif (Test-Path (Join-Path $RepoRoot "version.json")) {
        $json = Get-Content (Join-Path $RepoRoot "version.json") -Raw | ConvertFrom-Json
        $Version = $json.version
    }
    else {
        Write-Error "Cannot determine version. Supply -Version, set OWNAUDIO_VERSION, or create version.json."
        exit 1
    }
}

Write-Host "Packaging OwnAudioSharp v$Version"
Write-Host "Artifacts root: $RepoRoot\artifacts"
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
        "-p:OwnAudioVersion=$Version" `
        "-p:OWNAUDIO_VERSION=$Version" `
        --output $NupkgOut
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $CsProj" }
}

# ---------------------------------------------------------------------------
# Restore once for each project
# ---------------------------------------------------------------------------
Write-Host "Restoring dependencies..."
dotnet restore "$PackagingRoot\OwnAudioSharp.Basic\OwnAudioSharp.Basic.csproj" "-p:OwnAudioVersion=$Version"
dotnet restore "$PackagingRoot\OwnAudioSharp\OwnAudioSharp.csproj" "-p:OwnAudioVersion=$Version"

# ---------------------------------------------------------------------------
# Pack all three packages
# ---------------------------------------------------------------------------
Invoke-Pack "$PackagingRoot\OwnAudioSharp.Basic\OwnAudioSharp.Basic.csproj"
Invoke-Pack "$PackagingRoot\OwnAudioSharp\OwnAudioSharp.csproj"

$workloads = dotnet workload list 2>&1
if ($workloads -match "android|ios") {
    Invoke-Pack "$PackagingRoot\OwnAudioSharp.Mobile\OwnAudioSharp.Mobile.csproj"
}
else {
    Write-Warning "Android/iOS workloads not installed — skipping OwnAudioSharp.Mobile pack."
}

Write-Host ""
Write-Host "Done. Packages written to: $NupkgOut"
Get-ChildItem "$NupkgOut\*.nupkg" | Select-Object Name, Length | Format-Table
