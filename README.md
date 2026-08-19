# KawaPaint

A modern, cross-platform image editor compatible with Paint.NET 3.36 file format. KawaPaint is a clean rewrite built on a shared C#/SkiaSharp engine with Avalonia UI, running natively on Windows, Linux, and in the browser via WebAssembly.

## Quick Start

### Docker (Easiest)

Run the web version in Docker:

```bash
docker-compose up
```

Then open http://localhost:8080 in your browser.

### Windows Build

Requires .NET 10 SDK.

```bash
dotnet build KawaPaint.slnx -c Release
cd win/bin/Release/net10.0-windows
./KawaPaint.Win.exe
```

### Linux Build

Requires .NET 10 SDK.

```bash
dotnet build KawaPaint.slnx -c Release
cd linux/bin/Release/net10.0-linux-x64
./KawaPaint.Linux
```

### Web Build (Local)

Requires .NET 10 SDK with wasm-tools workload:

```bash
dotnet workload install wasm-tools
dotnet build web/KawaPaint.Web.csproj -c Release
cd web/bin/Release/net10.0-browser/publish/wwwroot
# Serve with any HTTP server, e.g.
python -m http.server 8000
```

## Project Structure

- **shared/** — Core engine (KawaPaint.Engine) and UI (KawaPaint.App), used by all platforms
- **win/** — Windows desktop application (KawaPaint.Win)
- **linux/** — Linux desktop application (KawaPaint.Linux)
- **web/** — Browser application (KawaPaint.Web, WebAssembly/Avalonia.Browser)

Open `KawaPaint.slnx` to build any platform; all share the same engine and UI code.

## Credits

**KawaPaint** is maintained by Kawa.

**Paint.NET** — Based on Paint.NET 3.36 (the last MIT-licensed release) by Rick Brewster and contributors. See [FORK.TXT](FORK.TXT) for fork rationale.

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
