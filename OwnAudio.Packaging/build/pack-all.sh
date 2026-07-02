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
# Prerequisite: native binaries must be present under
# OwnAudioEngine/OwnAudioRust/runtimes/{rid}/native/ (committed by the CI
# update-runtimes job, or pulled from the branch) — the real packaging projects
# embed them from there.
#
# NOTE: These are the REAL packaging projects under OwnAudio/Source/ that compile
# the OwnaudioNET public API. The old OwnAudio.Packaging/OwnAudioSharp* wrapper
# projects produced truncated, API-less packages and have been removed.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PACKAGING_ROOT="${SCRIPT_DIR}/.."
SRC_ROOT="${REPO_ROOT}/OwnAudio/Source"
NUPKG_OUT="${PACKAGING_ROOT}/nupkg"

# ---------------------------------------------------------------------------
# Resolve the package version
#
# The real projects carry their version in the csproj <Version> (source of
# truth). Only override it when an explicit version is supplied (arg or env);
# otherwise the csproj value stands. This avoids desyncing the Full package
# from its OwnAudioSharp.Midi dependency and the stale version.json footgun.
# ---------------------------------------------------------------------------
VERSION_ARGS=()
if [[ -n "${1:-}" ]]; then
    VERSION_ARGS=(-p:Version="$1")
    DISPLAY_VER="$1 (override)"
elif [[ -n "${OWNAUDIO_VERSION:-}" ]]; then
    VERSION_ARGS=(-p:Version="${OWNAUDIO_VERSION}")
    DISPLAY_VER="${OWNAUDIO_VERSION} (override)"
else
    DISPLAY_VER="(from each csproj <Version>)"
fi

echo "Packaging OwnAudioSharp v${DISPLAY_VER}"
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
        -p:GeneratePackageOnBuild=false \
        ${VERSION_ARGS[@]+"${VERSION_ARGS[@]}"} \
        --output "${NUPKG_OUT}"
}

# ---------------------------------------------------------------------------
# Restore once for all projects
# ---------------------------------------------------------------------------
echo "Restoring dependencies..."
dotnet restore "${SRC_ROOT}/OwnaudioNET.Basic.csproj"
dotnet restore "${SRC_ROOT}/OwnaudioNET.csproj"

# ---------------------------------------------------------------------------
# Pack all three packages
# ---------------------------------------------------------------------------
pack_project "${SRC_ROOT}/OwnaudioNET.Basic.csproj"
pack_project "${SRC_ROOT}/OwnaudioNET.csproj"

# Mobile pack is optional — skip gracefully if Android/iOS SDK is not present
if dotnet workload list 2>/dev/null | grep -q "android\|ios"; then
    dotnet restore "${SRC_ROOT}/OwnaudioNET.Mobile.csproj"
    pack_project "${SRC_ROOT}/OwnaudioNET.Mobile.csproj"
else
    echo "WARNING: Android/iOS workloads not installed — skipping OwnaudioNET.Mobile pack." >&2
fi

echo ""
echo "Done. Packages written to: ${NUPKG_OUT}"
ls -lh "${NUPKG_OUT}"/*.nupkg 2>/dev/null || true
