#!/usr/bin/env bash
# build-macos-dmg.sh — one-shot release builder for chatxp (macOS).
#
# Publishes the Avalonia app for a given macOS arch, assembles a .app bundle,
# codesigns it (ad-hoc by default), and packs a .dmg.
#
# Usage:
#   scripts/build-macos-dmg.sh            # arm64 (Apple Silicon)
#   scripts/build-macos-dmg.sh x64        # Intel
#   scripts/build-macos-dmg.sh arm64 x64  # both
#
# Options via env:
#   SIGN_IDENTITY    ad-hoc by default. Pass a Developer ID name (e.g.
#                    "Developer ID Application: You (TEAMID)") to sign properly.
#   SKIP_SIGN=1       skip codesigning entirely.
#
set -euo pipefail

cd "$(dirname "$0")/.."

ARCHS=("${@:-arm64}")
[ ${#ARCHS[@]} -eq 0 ] && ARCHS=(arm64)

CONFIG="Release"
PACKAGING_DIR="packaging"
DIST_DIR="dist"

for ARCH in "${ARCHS[@]}"; do
    RID="osx-$ARCH"
    PUBLISH_DIR="chatxp.App/bin/$CONFIG/net10.0/$RID/publish"
    APP_NAME="chatxp.app"
    DMG_NAME="chatxp-macos-$ARCH.dmg"

    echo "==> [$ARCH] dotnet publish -r $RID (self-contained)"
    dotnet publish chatxp.App/chatxp.App.csproj -c "$CONFIG" -r "$RID" --self-contained true

    echo "==> [$ARCH] assembling $APP_NAME"
    STAGE="$DIST_DIR/chatxp-macos-$ARCH"
    rm -rf "$STAGE"
    mkdir -p "$STAGE/$APP_NAME/Contents/MacOS" "$STAGE/$APP_NAME/Contents/Resources"

    cp -R "$PUBLISH_DIR/." "$STAGE/$APP_NAME/Contents/MacOS/"
    cp "$PACKAGING_DIR/chatxp.icns" "$STAGE/$APP_NAME/Contents/Resources/chatxp.icns"
    cp "$PACKAGING_DIR/Info.plist" "$STAGE/$APP_NAME/Contents/Info.plist"

    echo "==> [$ARCH] codesign"
    if [ "${SKIP_SIGN:-0}" = "1" ]; then
        echo "    skipped (SKIP_SIGN=1)"
    else
        IDENTITY="${SIGN_IDENTITY:--}"
        codesign --force --deep --sign "$IDENTITY" "$STAGE/$APP_NAME"
        codesign -v --deep "$STAGE/$APP_NAME"
        echo "    signature verified (ad-hoc; Gatekeeper still needs manual Open on first launch)"
    fi

    echo "==> [$ARCH] creating $DMG_NAME"
    rm -f "$DIST_DIR/$DMG_NAME"
    # UDZO = compressed; -ov overwrites. (-format flag of hdiutil triggers a
    # deprecation warning on newer macOS; harmless, keep until diskutil
    # image create gains equal ergonomics.)
    hdiutil create -volname "chatxp" -srcfolder "$STAGE" -ov -format UDZO \
        "$DIST_DIR/$DMG_NAME" 2>/dev/null

    echo "==> [$ARCH] done: $DIST_DIR/$DMG_NAME"
    ls -lh "$DIST_DIR/$DMG_NAME"
done

echo
echo "All done. DMGs in $DIST_DIR/. First-launch note: ad-hoc/un-notarized apps need right-click → Open."
