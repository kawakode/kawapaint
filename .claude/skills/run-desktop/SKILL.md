---
name: run-desktop
description: Build, run, and drive the KawaPaint Windows desktop app (win\KawaPaint.Win.csproj). Use when asked to start the app, screenshot it, click through its UI, or verify a GUI change actually works - not just that it compiles.
---

KawaPaint's Windows host (`win\KawaPaint.Win.csproj`) is a native Avalonia desktop app, not a
browser or Electron app - there is no DOM, no Playwright, no UI-Automation tree to query, because
Avalonia paints its own menus/dialogs onto a Skia canvas. Driving it means real pixel clicks and
real keystrokes via `user32.dll`, and verifying it means taking a screenshot and actually looking at
it (Read tool on the PNG) - "it compiles" and "the process didn't crash" are not the same as "the
feature works."

The driver is `driver.ps1` in this folder: Windows PowerShell 5.1 + inline C# P/Invoke +
`System.Drawing` for screenshots. Nothing to install - it's all already on the machine.

## Prerequisites

None beyond the .NET SDK already required to build the app. Note: in agent shells in this
environment, `dotnet` is often not on `PATH` even though it's installed - use the full path:
`C:\Program Files\dotnet\dotnet.exe`.

## Build

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build win\KawaPaint.Win.csproj
```

## Run (agent path) - full GUI click-through

```powershell
. .claude\skills\run-desktop\driver.ps1
$p = Start-KawaPaint                    # launches, waits for window, pins to (0,0,1280,900), focuses
Get-Screenshot -Path C:\...\01.png      # full-screen by default - see Gotchas for why
# Read the PNG (it's a normal file - use the Read tool), find what you need on screen, then:
Send-Click -X 31 -Y 48                  # e.g. click "File"
Start-Sleep -Milliseconds 400           # let the popup/dialog animation finish
Get-Screenshot -Path C:\...\02.png
# repeat: screenshot -> read -> click/type -> screenshot -> read ...
Stop-KawaPaint
```

There is no shortcut around the screenshot-then-look loop - coordinates have to come from an image
you actually inspected, not guessed from the AXAML source (rendered position depends on font
metrics, DPI, and window size).

### Available driver functions

| Function | What it does |
|---|---|
| `Start-KawaPaint [-ExePath] [-X] [-Y] [-Width] [-Height]` | Launch, wait for window (up to 10s), pin position/size, foreground. Returns the `Process`. |
| `Stop-KawaPaint` | Force-kill it. |
| `Get-Screenshot -Path <file> [-X] [-Y] [-Width] [-Height]` | Screenshot a region to PNG. Defaults to full 1920x1080 screen. |
| `Send-Click -X <n> -Y <n>` | Real left-click at screen coordinates. |
| `Send-CtrlClick -X <n> -Y <n>` | Ctrl+click, for multi-selecting files in native list-view dialogs. |
| `Send-Text -Text <s>` | Types a string via simulated keystrokes (handles shifted characters). |
| `Send-Enter` / `Send-Escape` | Single key press. |

## Run (agent path) - CLI batch mode, no GUI at all

If you only need to test the script/batch-apply feature (`shared\KawaPaint.Cli`), skip the driver
entirely - it's a real CLI, no window ever opens, and stdout/exit code are all you need:

```powershell
& "win\bin\Debug\net10.0\KawaPaint.Win.exe" --script foo.kpscript --in a.png b.png --out-dir out
echo $LASTEXITCODE   # 0 clean, 1 a target failed, 2 saved with some steps skipped
```

## Run (human path)

Just run `win\bin\Debug\net10.0\KawaPaint.Win.exe` - opens a normal window.

## Gotchas

- **Avalonia menus aren't native `HMENU`s.** No `GetMenu`/`GetSubMenu` API access - every menu
  click is a pixel coordinate found by screenshotting and reading, never a Win32 menu query.
- **Native Save/Open/folder-picker dialogs are separate top-level windows**, not children of the
  app's `MainWindowHandle`. A screenshot cropped to the app window's bounds misses them entirely -
  always screenshot the *full screen* (`Get-Screenshot`'s default) when a dialog might have opened.
- **Typing multiple quoted paths into a native multi-select filename field is unreliable.**
  Observed directly: `"a.png" "b.png"` typed into the field kept only the last path (1 file went
  through, not 2). `Send-CtrlClick` on the actual file icons in the dialog's own list view is what
  actually works for multi-select.
- **`Start-Process` + an immediate `Get-Process` check can race** - the window may not exist yet.
  `Start-KawaPaint` already retries for up to 10s; don't shortcut that if you roll your own launch.
- **Sleep after every click** (300-700ms) before the next screenshot - Avalonia's popup/dialog
  open/close is animated, and a screenshot mid-transition shows neither the old nor new state
  cleanly.
- **Fix the window's position/size right after launch** (`Start-KawaPaint` does this via
  `MoveWindow`) - without it, coordinates computed from one screenshot won't line up on the next
  run, since the OS may place the window differently each launch.

## Troubleshooting

- **`Start-KawaPaint` throws "window did not appear"**: build first (see Build above); check
  whether an old instance is already running (`Get-Process KawaPaint.Win`) and holding the
  file lock Avalonia needs.
- **Screenshot looks blank/black**: another window (e.g. a UAC prompt) may have stolen focus;
  screenshot full-screen and look for it.
- **Clicks land in the wrong place**: something moved the window after `Start-KawaPaint` positioned
  it (e.g. clicking a taskbar icon). Re-run `Start-KawaPaint`'s positioning lines, or just re-launch.

## Known coordinates (as of 2026-08-21, 1280x900 window, 100% DPI - re-verify by screenshot if the
## menu layout has changed since)

| Target | (X, Y) |
|---|---|
| `File` menu | (31, 48) |
| `File ▸ Record` submenu | (71, 267) |
| `File ▸ Record ▸ Record Demo` | (361, 271) |
| `File ▸ Record ▸ Record Script` / `Stop & Save Script…` | (361, 439) |
| `File ▸ Record ▸ Batch Apply Script…` | (384, 467) |
| `Image` menu | (260, 48) |
| `Image ▸ Flip Horizontal` | (315, 173) |
| `Effects` menu | (329, 48) |
| `Effects ▸ Brightness / Contrast…` | (410, 173) |

These will drift the moment the menu XAML changes - treat the table as a fast starting guess, not
ground truth, and re-derive via screenshot when a click doesn't land where expected.
