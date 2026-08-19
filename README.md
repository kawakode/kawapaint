<p align="center">
  <img src="kawapaint-main.png" alt="KawaPaint" width="320" />
</p>

<p align="center">
  <a href="https://github.com/kawakode/kawapaint/releases/latest"><img src="https://img.shields.io/github/v/release/kawakode/kawapaint?label=release" alt="Latest Release"></a>
  <a href="https://github.com/kawakode/kawapaint/releases/latest"><img src="https://img.shields.io/github/release-date/kawakode/kawapaint" alt="Release Date"></a>
  <a href="https://github.com/kawakode/kawapaint/releases"><img src="https://img.shields.io/github/downloads/kawakode/kawapaint/total" alt="Downloads"></a>
</p>

A modern, cross-platform image editor compatible with Paint.NET 3.36 file format. KawaPaint is a clean rewrite built on a shared C#/SkiaSharp engine with Avalonia UI, running natively on Windows, Linux, and in the browser via WebAssembly.

## Project Structure

- **shared/** — Core engine (KawaPaint.Engine) and UI (KawaPaint.App), used by all platforms
- **win/** — Windows desktop application (KawaPaint.Win)
- **linux/** — Linux desktop application (KawaPaint.Linux)
- **web/** — Browser application (KawaPaint.Web, WebAssembly/Avalonia.Browser)

Open `KawaPaint.slnx` to build any platform; all share the same engine and UI code.

## Credits

**KawaPaint** is maintained by Kawa.

**Paint.NET** — Based on Paint.NET 3.36 by Rick Brewster and contributors. See [FORK.TXT](FORK.TXT) for fork rationale.

**Libraries**

- [SkiaSharp](https://github.com/mono/SkiaSharp) — 2D graphics rendering
- [Avalonia](https://github.com/AvaloniaUI/Avalonia) — Cross-platform UI framework
- [Avalonia.Controls.ColorPicker](https://github.com/AvaloniaUI/Avalonia) — Color picker control
- [Avalonia.Themes.Fluent](https://github.com/AvaloniaUI/Avalonia) — Fluent design theme
- [Avalonia.Fonts.Inter](https://github.com/AvaloniaUI/Avalonia) — Inter font package
- [Avalonia.Browser](https://github.com/AvaloniaUI/Avalonia) — WebAssembly browser support

**Icons**

- [Lucide](https://lucide.dev) — Toolbox, panel, and menu icons ([shared/KawaPaint.App/Icons.cs](shared/KawaPaint.App/Icons.cs)) are adapted from the Lucide icon set, ISC License (with portions under the MIT-licensed Feather Icons this project forked from). See [Lucide's license](https://github.com/lucide-icons/lucide/blob/main/LICENSE).

## License

KawaPaint is licensed under the MIT License, consistent with Paint.NET 3.36. See LICENSE file for details.
