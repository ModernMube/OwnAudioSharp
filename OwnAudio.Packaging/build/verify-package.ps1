#Requires -Version 7.0
<#
.SYNOPSIS
    Validates an OwnAudioSharp NuGet package's contents.

.DESCRIPTION
    Extracts the .nupkg and checks:
      1. Required native RID files are present under runtimes/.
      2. License, repository URL, and tags are populated in the .nuspec.
      3. Package size is within the expected range.
      4. Internal interop types are not leaking as public surface.

.PARAMETER NupkgPath
    Path to the .nupkg file to validate.

.EXAMPLE
    .\verify-package.ps1 -NupkgPath .\nupkg\OwnAudioSharp.Basic.1.0.0.nupkg
#>
param(
    [Parameter(Mandatory)]
    [string] $NupkgPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path $NupkgPath)) {
    Write-Error "File not found: $NupkgPath"
    exit 1
}

Write-Host "Verifying: $NupkgPath"

$TempDir = New-TemporaryFile | ForEach-Object { Remove-Item $_; New-Item -ItemType Directory -Path "$($_.FullName)_nupkg" }
try {
    # A .nupkg is just a zip file
    Expand-Archive -Path $NupkgPath -DestinationPath $TempDir.FullName -Force

    $NuspecFile = Get-ChildItem $TempDir.FullName -Filter "*.nuspec" -Recurse | Select-Object -First 1
    if (-not $NuspecFile) {
        Write-Error "No .nuspec found in package."
        exit 1
    }

    [xml]$Nuspec = Get-Content $NuspecFile.FullName
    $ns = @{ n = "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd" }
    $PkgId  = Select-Xml -Xml $Nuspec -XPath "//n:id"      -Namespace $ns | Select-Object -ExpandProperty Node | Select-Object -ExpandProperty InnerText
    $PkgVer = Select-Xml -Xml $Nuspec -XPath "//n:version"  -Namespace $ns | Select-Object -ExpandProperty Node | Select-Object -ExpandProperty InnerText
    Write-Host "Package: $PkgId v$PkgVer"

    $Failed = $false

    # -----------------------------------------------------------------------
    # Check 1: Required native RIDs
    # -----------------------------------------------------------------------
    Write-Host ""
    Write-Host "[1/4] Checking required native RIDs..."

    function Check-File {
        param([string] $RelPath)
        $full = Join-Path $TempDir.FullName ($RelPath -replace '/', '\')
        if (Test-Path $full) {
            Write-Host "  OK  $RelPath"
        }
        else {
            Write-Host "  FAIL MISSING: $RelPath"
            $script:Failed = $true
        }
    }

    if ($PkgId -eq "OwnAudioSharp.Mobile") {
        Check-File "runtimes/android-arm64/native/libownaudio_ffi.so"
        $iosFw = Join-Path $TempDir.FullName "runtimes\ios-arm64\native\ownaudio_ffi.framework"
        if (-not (Test-Path $iosFw)) {
            Write-Host "  WARN ios-arm64 framework absent (best-effort in step 8)"
        }
    }
    else {
        Check-File "runtimes/win-x64/native/ownaudio_ffi.dll"
        Check-File "runtimes/win-arm64/native/ownaudio_ffi.dll"
        Check-File "runtimes/linux-x64/native/libownaudio_ffi.so"
        Check-File "runtimes/linux-arm64/native/libownaudio_ffi.so"
        Check-File "runtimes/osx-x64/native/libownaudio_ffi.dylib"
        Check-File "runtimes/osx-arm64/native/libownaudio_ffi.dylib"
        Check-File "runtimes/android-arm64/native/libownaudio_ffi.so"
    }

    # -----------------------------------------------------------------------
    # Check 2: Metadata fields
    # -----------------------------------------------------------------------
    Write-Host ""
    Write-Host "[2/4] Checking metadata fields..."

    function Check-NuspecField {
        param([string] $Field, [string] $Xpath)
        $val = Select-Xml -Xml $Nuspec -XPath $Xpath -Namespace $ns |
               Select-Object -ExpandProperty Node -ErrorAction SilentlyContinue |
               Select-Object -ExpandProperty InnerText -ErrorAction SilentlyContinue
        if ($val) {
            Write-Host "  OK  <$Field>: $val"
        }
        else {
            Write-Host "  FAIL <$Field> is empty or missing in .nuspec"
            $script:Failed = $true
        }
    }

    Check-NuspecField "id"         "//n:id"
    Check-NuspecField "version"    "//n:version"
    Check-NuspecField "license"    "//n:license"
    Check-NuspecField "repository" "//n:repository/@url"
    Check-NuspecField "tags"       "//n:tags"

    # -----------------------------------------------------------------------
    # Check 3: Package size
    # -----------------------------------------------------------------------
    Write-Host ""
    Write-Host "[3/4] Checking package size..."

    $SizeBytes = (Get-Item $NupkgPath).Length
    $SizeMB    = [math]::Round($SizeBytes / 1MB, 2)
    Write-Host "  Package size: $SizeMB MB ($SizeBytes bytes)"

    if ($SizeBytes -gt 52428800) {
        Write-Host "  WARN Package exceeds 50 MB — check for accidental debug artifacts or model files."
    }
    elseif ($SizeBytes -lt 1000) {
        Write-Host "  FAIL Package is suspiciously small — likely empty."
        $Failed = $true
    }
    else {
        Write-Host "  OK  Size within expected range."
    }

    # -----------------------------------------------------------------------
    # Check 4: Managed assembly surface (internal types not leaking)
    # -----------------------------------------------------------------------
    Write-Host ""
    Write-Host "[4/4] Checking managed assembly surface..."

    $ManagedDll = Get-ChildItem $TempDir.FullName -Filter "OwnAudioRust.dll" -Recurse |
                  Where-Object { $_.FullName -notmatch "native" } |
                  Select-Object -First 1

    if ($ManagedDll) {
        $Content = [System.IO.File]::ReadAllBytes($ManagedDll.FullName)
        $Text    = [System.Text.Encoding]::ASCII.GetString($Content)
        if ($Text -like "*Ownaudio.Native.RustAudio.Interop.OwnAudioNative*") {
            Write-Host "  WARN OwnAudioNative string found in binary — confirm it is marked internal."
        }
        else {
            Write-Host "  OK  Internal interop types appear correctly hidden."
        }
    }
    else {
        Write-Host "  SKIP No managed DLL found for surface check."
    }

    # -----------------------------------------------------------------------
    # Summary
    # -----------------------------------------------------------------------
    Write-Host ""
    if (-not $Failed) {
        Write-Host "PASS: All checks passed for $PkgId v$PkgVer" -ForegroundColor Green
    }
    else {
        Write-Host "FAIL: One or more checks failed for $PkgId v$PkgVer" -ForegroundColor Red
        exit 1
    }
}
finally {
    Remove-Item -Recurse -Force $TempDir.FullName -ErrorAction SilentlyContinue
}
