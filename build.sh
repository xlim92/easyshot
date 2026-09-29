#!/bin/zsh
set -euo pipefail
cd "$(dirname "$0")"
APP=build/EasyShot.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
swiftc -O -swift-version 6 -target arm64-apple-macos14 Sources/*.swift -o "$APP/Contents/MacOS/EasyShot"
cp Info.plist "$APP/Contents/"
swift make-icon.swift build/AppIcon.iconset
iconutil -c icns build/AppIcon.iconset -o "$APP/Contents/Resources/AppIcon.icns"
codesign --force -s - "$APP"
echo "Собрано: $APP"
