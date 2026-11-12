#!/usr/bin/env bash
# Packs a self-contained `dotnet publish` output folder of KawaPaint.Linux into a single-file
# AppImage: download, make executable, run. No installation and no .NET runtime needed.
#
#   linux/appimage/build-appimage.sh <publish-dir> <version> <output.AppImage>
#
# <version> may carry a leading "v" (a tag name). Needs curl and desktop-file-utils.
#
# The desktop entry inside declares the file types KawaPaint opens, so tools that integrate
# AppImages into the desktop (AppImageLauncher, Gear Lever) offer it under "Open with".
# JPEG XL and JPEG 2000 still come from the system's libjxl / libopenjp2, as in the zip build.
set -euo pipefail

publish_dir=${1:?publish dir}
version=${2:?version}
output=${3:?output AppImage}

here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

version=${version#[vV]}

command -v desktop-file-validate >/dev/null || {
    echo "desktop-file-validate is needed by appimagetool (package desktop-file-utils)." >&2; exit 1; }
test -f "$publish_dir/KawaPaint.Linux" || { echo "No KawaPaint.Linux in $publish_dir" >&2; exit 1; }

app="$work/KawaPaint.AppDir"
mkdir -p "$app/usr/lib/kawapaint" \
         "$app/usr/share/applications" \
         "$app/usr/share/icons/hicolor/256x256/apps" \
         "$app/usr/share/mime/packages"

cp -a "$publish_dir/." "$app/usr/lib/kawapaint/"
chmod +x "$app/usr/lib/kawapaint/KawaPaint.Linux"

install -m 755 "$here/AppRun" "$app/AppRun"
# The AppImage format wants the desktop entry and the icon at the root; the copies under
# usr/share are what desktop integration tools install.
install -m 644 "$here/kawapaint.desktop" "$app/kawapaint.desktop"
install -m 644 "$here/kawapaint.desktop" "$app/usr/share/applications/kawapaint.desktop"
install -m 644 "$here/kawapaint.png" "$app/kawapaint.png"
install -m 644 "$here/kawapaint.png" "$app/usr/share/icons/hicolor/256x256/apps/kawapaint.png"
ln -s kawapaint.png "$app/.DirIcon"
install -m 644 "$here/kawapaint.xml" "$app/usr/share/mime/packages/kawapaint.xml"

# Pinned to a release: this tool's output is what users download and run.
tool_version=1.9.0
tool="${APPIMAGETOOL:-}"
if [[ -z "$tool" ]]; then
    tool="$work/appimagetool"
    curl -fsSL --retry 3 -o "$tool" \
        "https://github.com/AppImage/appimagetool/releases/download/$tool_version/appimagetool-x86_64.AppImage"
    chmod +x "$tool"
fi

rm -f "$output"
# --appimage-extract-and-run: build machines and containers usually have no FUSE.
ARCH=x86_64 VERSION="$version" "$tool" --appimage-extract-and-run --no-appstream "$app" "$output"
chmod +x "$output"

# Sanity check: the image unpacks without FUSE and holds a launchable app.
check="$work/check"
mkdir "$check"
(cd "$check" && "$(realpath "$output")" --appimage-extract >/dev/null)
test -x "$check/squashfs-root/AppRun"
test -x "$check/squashfs-root/usr/lib/kawapaint/KawaPaint.Linux"
test -f "$check/squashfs-root/kawapaint.desktop"

echo "Built $output (version $version)"
