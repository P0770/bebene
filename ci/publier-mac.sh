#!/bin/bash
# Lancé par GitHub (.github/workflows/bebene.yml, job mac) sur main, après la publication Windows.
# Ajoute (ou remplace) Bebene-mac-<version>.zip dans la Release vX.Y de la même version.
set -euo pipefail
ZIP="$1"
VER=$(grep -o 'public const string Version = "[^"]*"' Companion.cs | cut -d'"' -f2)
if ! gh release view "v$VER" >/dev/null 2>&1; then echo "Release v$VER absente : rien à ajouter."; exit 0; fi
gh release upload "v$VER" "$ZIP" --clobber
echo "Ajouté à la Release v$VER : $(basename "$ZIP")"
