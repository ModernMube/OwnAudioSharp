#!/usr/bin/env bash
# pack-all.sh — Build all three OwnAudioSharp NuGet packages.
#
# Usage:
#   ./pack-all.sh [VERSION]
#
# Examples:
#   ./pack-all.sh                  # reads version from ../../version.json
#   ./pack-all.sh 1.2.3            # explicit version (overrides version.json)
#   OWNAUDIO_VERSION=1.2.3 ./pack-all.sh   # via env var
#
# Prerequisite: native artifacts must be present under ../../artifacts/{rid}/
# before calling this script.  The CI rust-build jobs populate that directory.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PACKAGING_ROOT="${SCRIPT_DIR}/.."
NUPKG_OUT="${PACKAGING_ROOT}/nupkg"

# ---------------------------------------------------------------------------
# Resolve the package version
# ---------------------------------------------------------------------------
if [[ -n "${1:-}" ]]; then
    VERSION="$1"
elif [[ -n "${OWNAUDIO_VERSION:-}" ]]; then
    VERSION="${OWNAUDIO_VERSION}"
elif [[ -f "${REPO_ROOT}/version.json" ]]; then
    VERSION=$(grep -oP '"version"\s*:\s*"\K[^"]+' "${REPO_ROOT}/version.json")
else
    echo "ERROR: Cannot determine version. Supply it as an argument, OWNAUDIO_VERSION env var, or version.json." >&2
    exit 1
fi

echo "Packaging OwnAudioSharp v${VERSION}"
echo "Artifacts root: ${REPO_ROOT}/artifacts"
echo "Output dir:     ${NUPKG_OUT}"
echo ""

mkdir -p "${NUPKG_OUT}"

# ---------------------------------------------------------------------------
# Helper: pack one project
# ---------------------------------------------------------------------------
pack_project() {
    local CSPROJ="$1"
    echo "--- Packing: $(basename "${CSPROJ}") ---"
    dotnet pack "${CSPROJ}" \
        --configuration Release \
        --no-restore \
        -p:OwnAudioVersion="${VERSION}" \
        -p:OWNAUDIO_VERSION="${VERSION}" \
        --output "${NUPKG_OUT}"
}

# ---------------------------------------------------------------------------
# Restore once for all projects
# ---------------------------------------------------------------------------
echo "Restoring dependencies..."
dotnet restore "${PACKAGING_ROOT}/OwnAudioSharp.Basic/OwnAudioSharp.Basic.csproj" \
    -p:OwnAudioVersion="${VERSION}"
dotnet restore "${PACKAGING_ROOT}/OwnAudioSharp/OwnAudioSharp.csproj" \
    -p:OwnAudioVersion="${VERSION}"

# ---------------------------------------------------------------------------
# Pack all three packages
# ---------------------------------------------------------------------------
pack_project "${PACKAGING_ROOT}/OwnAudioSharp.Basic/OwnAudioSharp.Basic.csproj"
pack_project "${PACKAGING_ROOT}/OwnAudioSharp/OwnAudioSharp.csproj"

# Mobile pack is optional — skip gracefully if Android/iOS SDK is not present
if dotnet workload list 2>/dev/null | grep -q "android\|ios"; then
    pack_project "${PACKAGING_ROOT}/OwnAudioSharp.Mobile/OwnAudioSharp.Mobile.csproj"
else
    echo "WARNING: Android/iOS workloads not installed — skipping OwnAudioSharp.Mobile pack." >&2
fi

echo ""
echo "Done. Packages written to: ${NUPKG_OUT}"
ls -lh "${NUPKG_OUT}"/*.nupkg 2>/dev/null || true
