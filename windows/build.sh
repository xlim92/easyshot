#!/bin/zsh
# Builds the Windows version on macOS: build/EasyShot.exe, a single file with the .NET runtime inside.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p build
# The exe icon is rendered from the same glyph as the macOS app icon.
swiftc -swift-version 6 -parse-as-library make-icon.swift Sources/Glyph.swift -o build/make-icon
build/make-icon build/EasyShot.ico
dotnet publish windows -c Release -p:ApplicationIcon="$PWD/build/EasyShot.ico" -o build/windows
cp build/windows/EasyShot.exe build/EasyShot.exe
rm -rf build/windows
echo "Built: build/EasyShot.exe"
