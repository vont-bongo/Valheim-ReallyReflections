#!/bin/bash
# Builds the release DLL and the Thunderstore/Hexium package: dist/vont-bongo-ReallyReflections-<version>.zip with manifest.json,
# icon.png, README.md, CHANGELOG.md and ReallyReflections.dll at the root of the archive.
# Usage: ./package.sh [path to your Valheim folder with BepInEx]   (default: the csproj's default Steam path)
set -e
cd "$(dirname "$0")"
version=$(python3 -c "import json; print(json.load(open('manifest.json'))['version_number'])")
grep -q "Version = \"$version\"" src/ModInfo.cs || { echo "package: manifest.json says $version but src/ModInfo.cs has another Version"; exit 1; }
dotnet build -c Release ${1:+-p:ValheimDir="$1"} >/dev/null
mkdir -p dist
out="dist/vont-bongo-ReallyReflections-$version.zip"
python3 - "$out" <<'PY'
import sys, zipfile
with zipfile.ZipFile(sys.argv[1], "w", zipfile.ZIP_DEFLATED) as z:
    for f in ("manifest.json", "icon.png", "README.md", "CHANGELOG.md"):
        z.write(f, f)
    z.write("bin/Release/netstandard2.1/ReallyReflections.dll", "ReallyReflections.dll")
PY
echo "package: $out"
