<p align="center">
  <a href="https://paint.kawa.zip/"><img src="kawapaint-main.png" alt="KawaPaint" width="320" /></a>
</p>

<p align="center">
  <a href="https://github.com/kawakode/kawapaint/releases/latest"><img src="https://img.shields.io/github/v/release/kawakode/kawapaint?label=release" alt="Latest Release"></a>
  <a href="https://github.com/kawakode/kawapaint/releases/latest"><img src="https://img.shields.io/github/release-date/kawakode/kawapaint" alt="Release Date"></a>
  <a href="https://github.com/kawakode/kawapaint/releases"><img src="https://img.shields.io/github/downloads/kawakode/kawapaint/total" alt="Downloads"></a>
</p>

A modern, cross-platform image editor compatible with Paint.NET 3.36 file format. KawaPaint is a clean rewrite built on a shared C#/SkiaSharp engine with Avalonia UI, running on Windows, Linux, Apple Silicon macOS, Android, and in the browser via WebAssembly.

## Project Structure

- **shared/** - Core engine (KawaPaint.Engine) and UI (KawaPaint.App), used by all platforms
- **win/** - Windows desktop application (KawaPaint.Win)
- **linux/** - Linux desktop application (KawaPaint.Linux)
- **mac/** - Apple Silicon macOS desktop application (KawaPaint.Mac), with bundled JXL/JP2 codecs
- **android/** - Android application (KawaPaint.Android), with touch/pen input and a compact workspace
- **web/** - Browser application (KawaPaint.Web, WebAssembly/Avalonia.Browser)

Open `KawaPaint.slnx` to build any platform; all share the same engine and UI code.

### Android on Apple Silicon

Install the Android workload, JDK 21, command-line SDK, API 36 platform and an ARM64 system image.
The project recognizes the standard `~/Library/Android/sdk` plus Homebrew's `openjdk@21` path. Then:

```text
dotnet build android/KawaPaint.Android.csproj -c Debug
adb install --no-incremental android/bin/Debug/net10.0-android/com.kawapaint.app-Signed.apk
```

Debug APKs embed their assemblies and can be installed directly. Desktop plugins and Git-backed
history are disabled on Android; native JXL/JP2 are reported unavailable when no Android ABI pack
is present.

## Docker

The web image is served at `/` by default:

```text
docker compose up
```

Set `BASE_PATH` when publishing it below a URL prefix. For example, this serves the app at
`http://localhost:8080/app/`:

```text
BASE_PATH=/app docker compose up
```

The reverse proxy should preserve that prefix when forwarding requests to the container.

## Batch export

Named export presets are managed from **File > Export > Manage Presets**. Presets can resize or
pad, choose codec settings, run a `.kpscript`, apply filename patterns, and emit caption/alt-text
sidecars. The desktop executables also expose the same engine from the command line:

```text
kawapaint --preset "Art Square" --in image.kwp --out-dir exported
kawapaint --script cleanup.kpscript --in-dir photos --pattern *.png --out-dir cleaned
```

Preset CLI runs read the normal KawaPaint `settings.json`; use `--settings <path>` to select a
different settings file.

## Publish artwork

Choose **File > Publish Artwork** to publish an export-preset rendering directly to Tumblr,
DeviantArt, or a managed Facebook Page. The dialog supports Tumblr drafts/queueing, DeviantArt
mature-content and gallery metadata, and Facebook Page captions and alt text.

Each service requires an API application. Register this desktop callback URL exactly:
`http://127.0.0.1:43817/callback/`, then enter the application's client ID and secret in the
publishing dialog. Secrets and OAuth tokens are stored in the operating system's credential vault,
not in `settings.json` or image/project files. Facebook publishing targets Pages rather than
personal profiles and may require Meta permission review. A new DeviantArt public/native client uses
PKCE and therefore does not need a client secret; legacy confidential clients remain supported.

Instagram is export-only: the default presets include **Instagram Square**, **Instagram Portrait
4x5**, and **Instagram Landscape**. ArtStation publishing is not included because ArtStation does
not offer an official public publishing API.

## Tablet input and 3D reference layers

Pen pressure can control brush size, opacity, or both; inverted/eraser tips are recognized
automatically. Touch input pans and pinch-zooms instead of painting. Configure this under
**Preferences > Drawing**.

Choose **File > Import 3D Reference** to load an OBJ, glTF or GLB model, pose its yaw, pitch and roll
in the live preview, and render it into a normal antialiased raster layer. The resulting pixels
support the usual layer tools and undo/redo; the source model is not stored as a live scene.

### Mail merge from CSV

Use the **Dynamic Text / CSV Zone** tool to place a non-destructive text area on a template. Its
text may reference CSV columns—for example `Binder — {StudentName}` or `{FirstName} {LastName}`.
Click a zone again to edit or delete it, then choose **File > Export > Mail Merge from CSV**. Select
the CSV, an export preset and an output folder; KawaPaint creates one image per row while leaving
the template unchanged. Comma-, semicolon- and tab-delimited files are accepted.

## Credits

**KawaPaint** is maintained by Kawa.

**Paint.NET** - Based on Paint.NET 3.36 by Rick Brewster and contributors. See [FORK.TXT](FORK.TXT) for fork rationale.

**Libraries**

- [SkiaSharp](https://github.com/mono/SkiaSharp) - 2D graphics rendering
- [Avalonia](https://github.com/AvaloniaUI/Avalonia) - Cross-platform UI framework
- [Avalonia.Controls.ColorPicker](https://github.com/AvaloniaUI/Avalonia) - Color picker control
- [Avalonia.Themes.Fluent](https://github.com/AvaloniaUI/Avalonia) - Fluent design theme
- [Avalonia.Fonts.Inter](https://github.com/AvaloniaUI/Avalonia) - Inter font package
- [Avalonia.Browser](https://github.com/AvaloniaUI/Avalonia) - WebAssembly browser support

**Icons**

- [Lucide](https://lucide.dev) - Toolbox, panel, and menu icons ([shared/KawaPaint.App/Icons.cs](shared/KawaPaint.App/Icons.cs)) are adapted from the Lucide icon set, ISC License (with portions under the MIT-licensed Feather Icons this project forked from). See [Lucide's license](https://github.com/lucide-icons/lucide/blob/main/LICENSE).

## License

KawaPaint is licensed under the MIT License, consistent with Paint.NET 3.36. See LICENSE file for details.
