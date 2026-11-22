#!/bin/bash
# Compile Bébène pour Mac (sur un Mac ou sur le runner macos de GitHub Actions) : mac/build/Bebene.app + Bebene-mac-<version>.zip
# Aucun Xcode requis : seulement les outils en ligne de commande (swiftc, iconutil, sips, codesign, ditto).
set -euo pipefail
cd "$(dirname "$0")"
ROOT=..
VER=$(grep -o 'public const string Version = "[^"]*"' "$ROOT/Companion.cs" | cut -d'"' -f2)
OUT=build; APP="$OUT/Bebene.app"
rm -rf "$OUT"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
echo "let appVersion = \"$VER\"" > "$OUT/Version.swift"

# binaire universel : Apple Silicon + Intel, macOS 13 ou plus récent
for arch in arm64 x86_64; do
  swiftc -O -target "$arch-apple-macos13.0" -o "$OUT/bebene-$arch" Sources/*.swift "$OUT/Version.swift" \
    -framework AppKit -framework WebKit -framework Carbon -framework ServiceManagement -framework UserNotifications
done
lipo -create -output "$APP/Contents/MacOS/Bebene" "$OUT/bebene-arm64" "$OUT/bebene-x86_64"

# pages : les mêmes que Windows, plus le pont et le chat
cp "$ROOT/ui.html" "$ROOT/settings.html" "$ROOT/fonctions.json" pont.js chat.html avatar.png "$APP/Contents/Resources/"

# icône
ICON="$OUT/Bebene.iconset"; mkdir -p "$ICON"
for s in 16 32 128 256 512; do
  sips -z $s $s "$ROOT/assets/icone-bebene.png" --out "$ICON/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) "$ROOT/assets/icone-bebene.png" --out "$ICON/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICON" -o "$APP/Contents/Resources/Bebene.icns"

sed "s/__VERSION__/$VER/g" Info.plist > "$APP/Contents/Info.plist"
codesign --force --deep --sign - "$APP"                 # signature « ad hoc » : obligatoire sur Apple Silicon (pas un certificat Apple)

cp LISEZMOI-MAC.txt "$OUT/"
(cd "$OUT" && zip -qry "Bebene-mac-$VER.zip" Bebene.app LISEZMOI-MAC.txt)
echo "$OUT/Bebene-mac-$VER.zip"
