#!/bin/zsh
set -euo pipefail
cd "$(dirname "$0")"
APP=build/EasyShot.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
swiftc -O -swift-version 6 -target arm64-apple-macos14 Sources/*.swift -o "$APP/Contents/MacOS/EasyShot"
cp Info.plist "$APP/Contents/"
swiftc -swift-version 6 -parse-as-library make-icon.swift Sources/Glyph.swift -o build/make-icon
build/make-icon build/AppIcon.iconset
iconutil -c icns build/AppIcon.iconset -o "$APP/Contents/Resources/AppIcon.icns"
codesign --force -s - "$APP"

# Disk image for distribution: the app plus a link to Applications for drag-and-drop install.
rm -rf build/dmg
mkdir -p build/dmg
ditto "$APP" build/dmg/EasyShot.app
ln -s /Applications build/dmg/Applications
hdiutil create -volname EasyShot -srcfolder build/dmg -ov -format UDZO build/EasyShot.dmg >/dev/null
rm -rf build/dmg
echo "Built: $APP, build/EasyShot.dmg"
