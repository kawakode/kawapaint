#!/usr/bin/env bash
# Wraps a `dotnet publish` output folder of KawaPaint.Mac into KawaPaint.app and packs it into a
# drag-to-Applications disk image. macOS only (uses codesign and hdiutil).
#
#   mac/dmg/build-dmg.sh <publish-dir> <version> <output.dmg>
#
# <version> may carry a leading "v" (a tag name); CFBundle*Version only accepts digits and dots,
# so anything after the numeric part (e.g. "-beta.1") is dropped from the plist.
#
# The bundle is ad-hoc signed only (no Apple Developer ID / notarization), so Gatekeeper asks the
# user to confirm the first launch. See the "macOS" section of README.md.
set -euo pipefail

publish_dir=${1:?publish dir}
version=${2:?version}
output=${3:?output dmg}

here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

version=${version#[vV]}
plist_version=$(printf '%s' "$version" | grep -oE '^[0-9]+(\.[0-9]+){0,2}' || true)
plist_version=${plist_version:-0.0.0}

app="$work/KawaPaint.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

# The whole publish output stays flat next to the apphost: the managed runtime and the bundled
# JXL/JP2 dylibs are resolved relative to it (AppContext.BaseDirectory / @loader_path).
ditto "$publish_dir" "$app/Contents/MacOS"
chmod +x "$app/Contents/MacOS/KawaPaint.Mac"
sed "s/__VERSION__/$plist_version/g" "$here/Info.plist" > "$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist"
printf 'APPL????' > "$app/Contents/PkgInfo"
cp "$here/KawaPaint.icns" "$app/Contents/Resources/KawaPaint.icns"

# Apple Silicon refuses to run unsigned code; an ad-hoc signature over the whole bundle keeps
# every Mach-O valid and seals the resources. Developer ID signing + notarization would replace
# "-" here once an Apple Developer account exists.
codesign --force --deep --sign - --timestamp=none "$app"
codesign --verify --deep --strict --verbose=2 "$app"

python3 -m venv "$work/venv"
"$work/venv/bin/pip" install --quiet "dmgbuild==1.6.5"

rm -f "$output"
"$work/venv/bin/dmgbuild" -s "$here/dmgbuild-settings.py" \
    -D app="$app" \
    -D background="$here/background.png" \
    -D icon="$here/KawaPaint.icns" \
    "KawaPaint" "$output"

# Sanity check: the image mounts and contains a valid app plus the Applications link.
mount_point=$(mktemp -d)
hdiutil attach -nobrowse -readonly -mountpoint "$mount_point" "$output" >/dev/null
status=0
{
    test -d "$mount_point/KawaPaint.app" &&
    test -L "$mount_point/Applications" &&
    test "$(readlink "$mount_point/Applications")" = /Applications &&
    test -f "$mount_point/.background.tiff" &&
    codesign --verify --deep --strict "$mount_point/KawaPaint.app"
} || status=$?
hdiutil detach "$mount_point" >/dev/null || hdiutil detach -force "$mount_point" >/dev/null
if [[ $status -ne 0 ]]; then
    echo "DMG verification failed." >&2
    exit "$status"
fi
echo "Built $output (KawaPaint $plist_version)"
