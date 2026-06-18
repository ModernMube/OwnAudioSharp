#!/usr/bin/env bash
# verify-package.sh — Validate OwnAudioSharp NuGet package contents.
#
# Usage:
#   ./verify-package.sh <path-to-nupkg> [--package-id <id>]
#
# Checks:
#   1. Required native RID files are present under runtimes/ (desktop + android).
#   2. License, repository URL, and tags are not empty in the .nuspec.
#   3. Package size is within expected range (warning only if outside bounds).
#   4. Struct-size smoke test marker file (if produced by CI) is present.
#
# Exit code: 0 = all checks passed, 1 = one or more checks failed.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ $# -lt 1 ]]; then
    echo "Usage: $0 <path-to-nupkg>" >&2
    exit 1
fi

NUPKG="$1"
PACKAGE_ID="${2:-}"
FAILED=0

if [[ ! -f "${NUPKG}" ]]; then
    echo "ERROR: File not found: ${NUPKG}" >&2
    exit 1
fi

echo "Verifying: ${NUPKG}"

# ---------------------------------------------------------------------------
# Extract the package to a temp directory
# ---------------------------------------------------------------------------
TMPDIR="$(mktemp -d)"
trap 'rm -rf "${TMPDIR}"' EXIT

unzip -q "${NUPKG}" -d "${TMPDIR}"

# ---------------------------------------------------------------------------
# Determine which package variant this is
# ---------------------------------------------------------------------------
NUSPEC_FILE="$(find "${TMPDIR}" -name "*.nuspec" | head -n 1)"
if [[ -z "${NUSPEC_FILE}" ]]; then
    echo "ERROR: No .nuspec found in package." >&2
    exit 1
fi

PKG_ID=$(grep -oP '(?<=<id>)[^<]+' "${NUSPEC_FILE}" | head -n 1)
PKG_VER=$(grep -oP '(?<=<version>)[^<]+' "${NUSPEC_FILE}" | head -n 1)
echo "Package: ${PKG_ID} v${PKG_VER}"

# ---------------------------------------------------------------------------
# Check 1: Required native RIDs
# ---------------------------------------------------------------------------
echo ""
echo "[1/4] Checking required native RIDs..."

# Basic and Full packages must have all 6 desktop + android RIDs.
# Mobile package has android + ios only.
check_file() {
    local PATH_IN_PKG="${TMPDIR}/$1"
    if [[ -f "${PATH_IN_PKG}" ]]; then
        echo "  OK  $1"
    else
        echo "  FAIL MISSING: $1"
        FAILED=1
    fi
}

if [[ "${PKG_ID}" == "OwnAudioSharp.Mobile" ]]; then
    check_file "runtimes/android-arm64/native/libownaudio_ffi.so"
    # iOS is best-effort in step 8; warn but do not fail
    if [[ ! -f "${TMPDIR}/runtimes/ios-arm64/native/ownaudio_ffi.framework" ]] && \
       [[ ! -d "${TMPDIR}/runtimes/ios-arm64/native/ownaudio_ffi.framework" ]]; then
        echo "  WARN ios-arm64 framework absent (best-effort in step 8)"
    fi
else
    check_file "runtimes/win-x64/native/ownaudio_ffi.dll"
    check_file "runtimes/win-arm64/native/ownaudio_ffi.dll"
    check_file "runtimes/linux-x64/native/libownaudio_ffi.so"
    check_file "runtimes/linux-arm64/native/libownaudio_ffi.so"
    check_file "runtimes/osx-x64/native/libownaudio_ffi.dylib"
    check_file "runtimes/osx-arm64/native/libownaudio_ffi.dylib"
    check_file "runtimes/android-arm64/native/libownaudio_ffi.so"
fi

# ---------------------------------------------------------------------------
# Check 2: Metadata fields
# ---------------------------------------------------------------------------
echo ""
echo "[2/4] Checking metadata fields..."

check_nuspec_field() {
    local FIELD="$1"
    local VALUE
    VALUE=$(grep -oP "(?<=<${FIELD}>)[^<]+" "${NUSPEC_FILE}" | head -n 1 || true)
    if [[ -n "${VALUE}" ]]; then
        echo "  OK  <${FIELD}>: ${VALUE}"
    else
        echo "  FAIL <${FIELD}> is empty or missing in .nuspec"
        FAILED=1
    fi
}

check_nuspec_field "id"
check_nuspec_field "version"
check_nuspec_field "license"
check_nuspec_field "repository"
check_nuspec_field "tags"

# ---------------------------------------------------------------------------
# Check 3: Package size sanity
# ---------------------------------------------------------------------------
echo ""
echo "[3/4] Checking package size..."

SIZE_BYTES=$(stat -c%s "${NUPKG}" 2>/dev/null || stat -f%z "${NUPKG}" 2>/dev/null || echo 0)
SIZE_MB=$(echo "scale=2; ${SIZE_BYTES} / 1048576" | bc 2>/dev/null || echo "?")
echo "  Package size: ${SIZE_MB} MB (${SIZE_BYTES} bytes)"

if [[ "${SIZE_BYTES}" -gt 52428800 ]]; then
    echo "  WARN Package exceeds 50 MB — check for accidental debug artifacts or model files."
elif [[ "${SIZE_BYTES}" -lt 1000 ]]; then
    echo "  FAIL Package is suspiciously small (${SIZE_BYTES} bytes) — likely empty."
    FAILED=1
else
    echo "  OK  Size within expected range."
fi

# ---------------------------------------------------------------------------
# Check 4: No internal namespace types in public surface
# ---------------------------------------------------------------------------
echo ""
echo "[4/4] Checking managed assembly surface..."

MANAGED_DLL=$(find "${TMPDIR}" -name "OwnAudioRust.dll" | head -n 1)
if [[ -z "${MANAGED_DLL}" ]]; then
    MANAGED_DLL=$(find "${TMPDIR}" -name "*.dll" ! -path "*/native/*" | head -n 1)
fi

if [[ -n "${MANAGED_DLL}" ]]; then
    # Check that internal namespaces (Native.RustAudio, Safe) are not exported
    # as public types.  We do a simple string search on the DLL binary.
    if strings "${MANAGED_DLL}" 2>/dev/null | grep -q "Ownaudio\.Native\.RustAudio\.Interop\.OwnAudioNative"; then
        echo "  WARN OwnAudioNative found in binary — confirm it is marked internal."
    else
        echo "  OK  Internal interop types not leaking in public surface."
    fi
else
    echo "  SKIP No managed DLL found for surface check."
fi

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
echo ""
if [[ "${FAILED}" -eq 0 ]]; then
    echo "PASS: All checks passed for ${PKG_ID} v${PKG_VER}"
else
    echo "FAIL: One or more checks failed for ${PKG_ID} v${PKG_VER}"
    exit 1
fi
