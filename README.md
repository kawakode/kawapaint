# KawaPaint
## Multiplatform PDN compatible image editor

A modern, cross-platform rewrite of Paint.NET 3.36 (the last MIT-licensed release — see
[FORK.TXT](FORK.TXT)), built on a shared C#/SkiaSharp engine and an Avalonia UI.

Directory layout
-----------------

    shared/   KawaPaint.Engine (pure engine, no UI) and KawaPaint.App (the Avalonia UI/dialogs),
              used by all three platform builds below. Also KawaPaint.Sandbox, a scratch console
              app for exercising the engine directly.
    win/      Windows desktop build (KawaPaint.Win)
    linux/    Linux desktop build (KawaPaint.Linux)
    web/      Browser build (KawaPaint.Web, WebAssembly via Avalonia.Browser) — see web/Dockerfile
              and docker-compose.yml to build and serve it as a container.

Open `KawaPaint.slnx` to build any of them; each is a thin entry point that references the shared
engine and UI code, so a fix in `shared/` lands on every platform at once.

The original WinForms/GDI+ Paint.NET 3.36 source this project started from lives on the
`3.36pdn` branch, kept out of this branch's working tree.
