#!/usr/bin/env bash
# KawaPaint - builds the JPEG XL (libjxl) and JPEG 2000 (OpenJPEG) shared libraries for Android,
# one directory per ABI, so JxlCodec/Jp2Codec are available on mobile like on the desktop heads.
#
# Needs the Android NDK (ANDROID_NDK_HOME, or ANDROID_NDK_LATEST_HOME as on GitHub's Ubuntu
# runners), cmake, ninja and git. The android-natives workflow runs this and commits the result
# beside this script; run it locally only to reproduce or update that set:
#
#   android/natives/build-natives.sh [output-dir]
#
# CMake's Android platform never versions sonames, so every library is a plain lib<name>.so that
# the APK can carry flat in lib/<abi>/. libc++ is shared (libc++_shared.so from the NDK) because
# libjxl, its CMS and Highway are separate libraries that share C++ types.
set -euo pipefail

LIBJXL_VERSION="${LIBJXL_VERSION:-v0.11.1}"
OPENJPEG_VERSION="${OPENJPEG_VERSION:-v2.5.3}"
ABIS="${ABIS:-arm64-v8a x86_64}"
API_LEVEL=23   # SupportedOSPlatformVersion in KawaPaint.Android.csproj

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="$(realpath -m "${1:-$here}")"
ndk="${ANDROID_NDK_HOME:-${ANDROID_NDK_LATEST_HOME:-}}"
[[ -d "$ndk" ]] || { echo "Set ANDROID_NDK_HOME to an Android NDK." >&2; exit 1; }
toolchain="$ndk/build/cmake/android.toolchain.cmake"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

git clone --quiet --depth 1 --branch "$LIBJXL_VERSION" https://github.com/libjxl/libjxl.git "$work/libjxl"
# Only the submodules this configuration compiles; the test corpus alone is hundreds of MB.
git -C "$work/libjxl" submodule update --quiet --init --depth 1 \
    third_party/highway third_party/brotli third_party/skcms
git clone --quiet --depth 1 --branch "$OPENJPEG_VERSION" https://github.com/uclouvain/openjpeg.git "$work/openjpeg"

common=(
    -G Ninja
    -DCMAKE_TOOLCHAIN_FILE="$toolchain"
    -DANDROID_PLATFORM="android-$API_LEVEL"
    -DANDROID_STL=c++_shared
    -DCMAKE_BUILD_TYPE=Release
    -DBUILD_SHARED_LIBS=ON
    -DBUILD_TESTING=OFF
    # Android 15+ devices may use 16 KB pages; 4 KB-aligned libraries fail to load there.
    -DCMAKE_SHARED_LINKER_FLAGS="-Wl,-z,max-page-size=16384"
)

for abi in $ABIS; do
    echo "== $abi"
    cmake -S "$work/libjxl" -B "$work/build-jxl-$abi" "${common[@]}" -DANDROID_ABI="$abi" \
        -DJPEGXL_ENABLE_TOOLS=OFF -DJPEGXL_ENABLE_DOXYGEN=OFF -DJPEGXL_ENABLE_MANPAGES=OFF \
        -DJPEGXL_ENABLE_BENCHMARK=OFF -DJPEGXL_ENABLE_EXAMPLES=OFF -DJPEGXL_ENABLE_JNI=OFF \
        -DJPEGXL_ENABLE_SJPEG=OFF -DJPEGXL_ENABLE_OPENEXR=OFF -DJPEGXL_ENABLE_JPEGLI=OFF \
        -DJPEGXL_ENABLE_PLUGINS=OFF -DJPEGXL_ENABLE_SKCMS=ON -DJPEGXL_BUNDLE_LIBPNG=OFF \
        -DJPEGXL_FORCE_SYSTEM_BROTLI=OFF -DJPEGXL_FORCE_SYSTEM_HWY=OFF \
        -DHWY_ENABLE_TESTS=OFF -DHWY_ENABLE_EXAMPLES=OFF -DHWY_ENABLE_CONTRIB=OFF > /dev/null
    cmake --build "$work/build-jxl-$abi" --target jxl jxl_cms > /dev/null

    cmake -S "$work/openjpeg" -B "$work/build-opj-$abi" "${common[@]}" -DANDROID_ABI="$abi" \
        -DBUILD_CODEC=OFF -DBUILD_JPIP=OFF -DBUILD_JPWL=OFF -DBUILD_MJ2=OFF > /dev/null
    cmake --build "$work/build-opj-$abi" --target openjp2 > /dev/null

    dest="$out/$abi"
    rm -rf "$dest"
    mkdir -p "$dest"
    for name in libjxl.so libjxl_cms.so libhwy.so libbrotlicommon.so libbrotlidec.so libbrotlienc.so; do
        cp "$(find "$work/build-jxl-$abi" -name "$name" -type f | head -n 1)" "$dest/"
    done
    cp "$(find "$work/build-opj-$abi" -name libopenjp2.so -type f | head -n 1)" "$dest/"
    triple=$(case "$abi" in arm64-v8a) echo aarch64-linux-android;; x86_64) echo x86_64-linux-android;;
             armeabi-v7a) echo arm-linux-androideabi;; x86) echo i686-linux-android;; esac)
    cp "$ndk/toolchains/llvm/prebuilt/linux-x86_64/sysroot/usr/lib/$triple/libc++_shared.so" "$dest/"
    "$ndk/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-strip" --strip-unneeded "$dest"/*.so
done

mkdir -p "$out/LICENSES"
cp "$work/libjxl/LICENSE" "$out/LICENSES/libjxl-LICENSE"
cp "$work/libjxl/PATENTS" "$out/LICENSES/libjxl-PATENTS"
cp "$work/libjxl/third_party/highway/LICENSE" "$out/LICENSES/highway-LICENSE"
cp "$work/libjxl/third_party/brotli/LICENSE" "$out/LICENSES/brotli-LICENSE"
cp "$work/libjxl/third_party/skcms/LICENSE" "$out/LICENSES/skcms-LICENSE"
cp "$work/openjpeg/LICENSE" "$out/LICENSES/openjpeg-LICENSE"
cp "$ndk/toolchains/llvm/prebuilt/linux-x86_64/NOTICE" "$out/LICENSES/llvm-libcxx-NOTICE" 2>/dev/null || true
printf 'libjxl %s\nopenjpeg %s\nndk %s\n' "$LIBJXL_VERSION" "$OPENJPEG_VERSION" \
    "$(grep -m1 Pkg.Revision "$ndk/source.properties" | cut -d= -f2 | tr -d ' ')" > "$out/VERSIONS.txt"
echo "Android codec libraries written to $out"
