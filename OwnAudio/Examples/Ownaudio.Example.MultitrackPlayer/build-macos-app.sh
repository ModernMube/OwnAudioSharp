#!/bin/bash

# MultitrackPlayer macOS .app Bundle Builder
# This script builds a proper macOS application bundle

set -e  # Exit on error

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${GREEN}=== MultitrackPlayer macOS App Builder ===${NC}"
echo ""

# Configuration
PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_FILE="$PROJECT_DIR/Ownaudio.Example.MultitrackPlayer.csproj"
RELEASE_DIR="$PROJECT_DIR/bin/Release/net9.0/osx-arm64"
APP_NAME="MultitrackPlayer.app"
APP_PATH="$RELEASE_DIR/$APP_NAME"

# Step 1: Clean previous build
echo -e "${YELLOW}[1/7] Cleaning previous build...${NC}"
if [ -d "$APP_PATH" ]; then
    rm -rf "$APP_PATH"
    echo "  ✓ Removed previous app bundle"
fi

# Step 2: Publish the application
echo -e "${YELLOW}[2/7] Publishing for macOS (osx-arm64)...${NC}"
dotnet publish "$PROJECT_FILE" \
    -c Release \
    -r osx-arm64 \
    --self-contained false \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false \
    -v quiet

if [ $? -eq 0 ]; then
    echo "  ✓ Publish successful"
else
    echo -e "${RED}  ✗ Publish failed${NC}"
    exit 1
fi

# Step 3: Create .app bundle structure
echo -e "${YELLOW}[3/7] Creating .app bundle structure...${NC}"
mkdir -p "$APP_PATH/Contents/MacOS"
mkdir -p "$APP_PATH/Contents/Resources"
echo "  ✓ Created bundle directories"

# Step 4: Copy published files to bundle
echo -e "${YELLOW}[4/7] Copying files to bundle...${NC}"
cp -r "$RELEASE_DIR/publish/"* "$APP_PATH/Contents/MacOS/"
echo "  ✓ Files copied"

# Step 5: Create Info.plist
echo -e "${YELLOW}[5/7] Creating Info.plist...${NC}"
cat > "$APP_PATH/Contents/Info.plist" << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>Ownaudio.Example.MultitrackPlayer</string>
    <key>CFBundleName</key>
    <string>Multitrack Player</string>
    <key>CFBundleDisplayName</key>
    <string>Multitrack Player</string>
    <key>CFBundleIdentifier</key>
    <string>com.ownaudio.multitrackplayer</string>
    <key>CFBundleVersion</key>
    <string>1.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.15</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSMicrophoneUsageDescription</key>
    <string>This app needs microphone access for audio recording and multitrack playback.</string>
    <key>NSSupportsAutomaticGraphicsSwitching</key>
    <true/>
</dict>
</plist>
EOF
echo "  ✓ Info.plist created"

# Step 6: Set executable permissions
echo -e "${YELLOW}[6/7] Setting executable permissions...${NC}"
chmod +x "$APP_PATH/Contents/MacOS/Ownaudio.Example.MultitrackPlayer"
echo "  ✓ Permissions set"

# Step 7: Code sign libraries
echo -e "${YELLOW}[7/7] Code signing native libraries...${NC}"
DYLIB_COUNT=0
for dylib in $(find "$APP_PATH" -name "*.dylib" 2>/dev/null); do
    codesign --force --sign - "$dylib" 2>/dev/null || true
    ((DYLIB_COUNT++))
done
echo "  ✓ Signed $DYLIB_COUNT .dylib files"

# Remove extended attributes
xattr -cr "$APP_PATH" 2>/dev/null || true

# Success message
echo ""
echo -e "${GREEN}=== Build Complete! ===${NC}"
echo ""
echo "App bundle location:"
echo "  $APP_PATH"
echo ""
echo "To run the app:"
echo "  1. Double-click in Finder, or"
echo "  2. Run: open \"$APP_PATH\""
echo ""
echo -e "${YELLOW}Note: First launch may show security warning. Allow in System Settings if needed.${NC}"
echo ""

# Optional: Ask to open
read -p "Open the app now? (y/n) " -n 1 -r
echo
if [[ $REPLY =~ ^[Yy]$ ]]; then
    open "$APP_PATH"
fi
