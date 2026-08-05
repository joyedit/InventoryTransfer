#!/bin/bash
set -e

MOD_NAME="InventoryTransfer"
MODS_DIR="$HOME/.config/VintagestoryData/Mods"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

VERSION=$(grep -oP '"version"\s*:\s*"\K[^"]+' "$SCRIPT_DIR/modinfo.json")
if [ -z "$VERSION" ]; then
    echo "ERROR: could not read version from modinfo.json" >&2
    exit 1
fi
ZIP_NAME="$MOD_NAME-$VERSION.zip"

echo "=== Cleaning ==="
rm -rf "$SCRIPT_DIR/obj" "$SCRIPT_DIR/bin"

echo "=== Building ==="
dotnet build -c Debug "$SCRIPT_DIR/$MOD_NAME.csproj"

echo "=== Packaging ==="
STAGING="/tmp/${MOD_NAME}_stage"
rm -rf "$STAGING"
mkdir -p "$STAGING"

cp "$SCRIPT_DIR/bin/Debug/$MOD_NAME.dll" "$STAGING/"
cp "$SCRIPT_DIR/modinfo.json" "$STAGING/"
cp "$SCRIPT_DIR/modicon.png" "$STAGING/"
cp -r "$SCRIPT_DIR/assets" "$STAGING/"

cd "$STAGING"
# Remove any previously deployed build (versioned or the old unversioned name)
# so only one copy of the mod ever sits in the Mods folder.
rm -f "$MODS_DIR/$MOD_NAME.zip" "$MODS_DIR/$MOD_NAME"-*.zip
zip -r -q "$MODS_DIR/$ZIP_NAME" *

echo "=== Cleaning up ==="
rm -rf "$STAGING"

echo "=== Deployed to $MODS_DIR/$ZIP_NAME ==="
