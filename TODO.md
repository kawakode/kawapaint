# KawaPaint — resume-here notes

Status snapshot: 2026-08-19, branch `master`. Full roadmap/rationale lives in Claude memory
(`feature-roadmap-tiers`) and the published plan:
https://claude.ai/code/artifact/b584d126-8639-4875-902d-46a1cb2917c4

## Known bugs

- **~~Crash on clicking in the History panel~~ — fixed 2026-08-19.** Root cause: a row click
  (`OnHistorySelected` → `JumpToHistory` → `HistoryStack.JumpTo` → `History.Changed`) called
  `RebuildHistoryPanel()` synchronously, which does `HistoryList.Items.Clear()` — reentering the
  *same* `ListBox`'s own `SelectionChanged` dispatch, still on the same call stack as the click.
  Avalonia's `SelectionModel` is mid-iteration at that point and throws
  `ArgumentOutOfRangeException` deep in its internals (`SelectionModel.OnSelectionRemoved` →
  `SelectedItems.GetEnumerator`). Reproduced live before fixing (temporary debug hook that
  simulated the click, confirmed the exact exception + stack) — not a guess.
  **Same bug also existed in the Layers panel** (row click → `SetActiveLayer` → `DocumentChanged`
  → `RebuildLayerPanel` → `Items.Clear()`, identical shape) — previously undiscovered, found while
  investigating this one, and fixed the same way. Fix: both `Canvas.History.Changed` and
  `Canvas.DocumentChanged` subscriptions in `MainView`'s constructor now
  `Dispatcher.UIThread.Post(...)` the rebuild instead of calling it inline, so it runs after the
  originating click's own dispatch has finished. Verified: re-ran the same repro post-fix (history
  row click on a genuinely populated list, and a layer row click) — both jump/select correctly
  with no exception, and a screenshot confirms the Layers panel visually reflects the new
  selection. Committed as `d787b35`.

## Done

**Tier 0 (foundations):** `shared/KawaPaint.App/Core/` — `SettingsService`/`AppSettings`
(typed, versioned, JSON), `PanelManager`/`WorkspaceLayout`/`PanelDescriptor` (registry-based
docking, resize, float), `CommandRegistry`/`AppCommand` (id-addressable actions, primary +
`AlternateGesture`), `DockEntry`. `shared/KawaPaint.Engine/Codecs/` — `CodecRegistry` +
PNG/JPEG/WebP/BMP/GIF/ICO with runtime availability probing. `DocumentSession` (path/dirty/edit
count).

**Tier 1 (core), all done:** autosave + crash recovery, resizable floating panels, saveable
layout presets, History panel (`HistoryStack` is an indexed list with `TileDeltaMemento` — tile
deltas not full clones, disk spill, `TruncateFrom` — see the git ruling below for why truncate-only),
clipboard (Cut/Copy/Copy Merged/Paste ×3), selection combine modes (Add/Subtract/Intersect,
live-preview while dragging), Magic Wand, Fill/Erase Selection, Canvas Size (anchor-based, distinct
from scale-Resize), recent files (MRU 10, desktop-only), per-format save options (JPEG quality /
WebP lossless), import layer from file, rulers + units (`RulerMath` in Engine is pure/testable,
`RulerBar` control, `Document.Dpi` threaded through everything incl. `.kwp`).

Deliberately skipped, not forgotten: antialiased selection edges (mask is binary; would need a
coverage-based rasterizer rewrite + graded `Selection.Clip` blending — real work, flag if wanted),
Layer Properties dialog (redundant, panel already has name/opacity/blend/visibility inline),
dedicated Zoom/Pan tool buttons (already covered by Ctrl+wheel/keys and middle/right-drag pan).

**Tier 2, started:** 2.1 effect catalogue + bundled tools done — 30 effects plus Clone Stamp/
Recolor/Rounded Rectangle/Freeform Shape, all three passes 2026-08-19 — see its own section below
for the full account, including a real infinite-loop bug found and fixed during verification. 2.2
custom dock also done — see `MainView`'s "Dock" panel, `DockEditorDialog`, `Core/DockEntry.cs`.
Hidden by default, `Ctrl+\`` or top-right icon summons it Floating.

Two real bugs found+fixed while building the dock (not just features — read if something about
panel defaults or redo-binding looks wrong later):
1. `WorkspaceLayout.For` used to seed `LastShown = DefaultPlace`, so a Hidden-default panel's
   first toggle-visible was a silent no-op forever. Fixed: Hidden now falls back to Floating.
2. Redo was two separate `AppCommand` registrations (Ctrl+Shift+Z, Ctrl+Y) — visible as a
   duplicate in the dock picker. Added `AppCommand.AlternateGesture` instead.

## Not started — pick one, each is its own multi-hour subsystem

### 2.1 — Effect catalogue
Port from paint.net 3.36's `src/Effects/` (MIT-licensed). The original source is preserved on the
`origin/3.36pdn` branch — verified it's actually there:
`git ls-tree -r origin/3.36pdn --name-only | grep Effects/` lists ~30 files. **Gotcha: don't pull
files with `git show origin/3.36pdn:path`** — some of these files are UTF-16-encoded blobs and
`git show` (unlike `git cat-file -p`) silently applies EOL conversion that corrupts the UTF-16
byte alignment, producing garbage. Use `git cat-file -p $(git rev-parse origin/3.36pdn:path)`
instead, which returns the raw blob untouched; the Read tool then auto-detects UTF-8 vs UTF-16
correctly. (The `src/` directory in the current working tree is unrelated leftover, not this —
don't confuse the two.)

**10 effects done 2026-08-19** (Distort: Bulge, Twist, Polar Inversion, Tile, Frosted Glass,
Pixelate; Stylize: Median, Outline, Relief, Vignette) — algorithms transcribed from the real pdn
source (not reinvented), wired into new `Effects > Distort` / `Effects > Stylize` submenus in
`MainView.axaml` via the existing `AdjustmentDialog`/`OnAdjust` live-preview pattern, same as
Brightness/Contrast etc. Not a literal file port: pdn's classes are built on
WinForms/IndirectUI/PropertySystem plumbing that doesn't exist here, so each effect's core
`OnRender`/`InverseTransform` math was ported onto KawaPaint's own `IEffect` shape instead.

New shared engine infrastructure, reusable for the effects still remaining below:
- `Surface.GetBilinearSampleClamped`/`GetBilinearSampleWrapped` (`Surface.cs`) — KawaPaint had no
  bilinear sampling at all before this; pdn's warp-style effects all depend on it.
- `WarpEffect` abstract base (`Effects.Distort.cs`) — mirrors pdn's `WarpEffectBase`: given a
  destination pixel's position relative to image center, subclasses return the center-relative
  source position to sample (inverse mapping), clamped or wrapped per `EdgeMode`. Bulge/Twist/
  Polar Inversion/Tile all just implement `InverseTransform`.
- `LocalHistogramEffect` abstract base (`Effects.Stylize.cs`) — mirrors pdn's
  `LocalHistogramEffect`: builds a per-channel 256-bin histogram over a circular neighborhood
  around each pixel, subclasses turn that into an output color. Median/Outline both use it.

Deliberate simplifications vs. the pdn originals (flag if any turn out to matter):
- No anti-aliased supersampling (pdn's `Utility.GetRgssOffsets`) — single-sample bilinear only,
  matching this file's existing style (`BoxBlurEffect` etc. don't AA either).
- Warp effects always center on the image; pdn's optional pan/offset property was dropped.
- `LocalHistogramEffect` recomputes each pixel's histogram from scratch
  (O(radius²)-per-pixel) rather than porting pdn's incremental row-sliding window. Fine at the
  radii a paint app's UI actually exposes (dialog defaults: Median radius 5, Outline thickness 3,
  both capped at 30 in the UI vs. pdn's 200) but would need the sliding-window version if a user
  wants very large radii to stay snappy on big canvases.
- `VignetteEffect` drops pdn's sRGB-linear round-trip (`SrgbUtility.ToLinear`/`ToSrgbClamped`) and
  multiplies directly in sRGB byte space, consistent with every other effect in this codebase
  (Sepia, Brightness/Contrast, etc. all do the same).
- `PixelateEffect` averages each full cell rather than porting pdn's 4-corner bilinear-weighted
  blend — simpler and arguably more correct for a "pixelate" effect.
- `OutlineEffect`'s alpha-channel bound-scan uses `ha` throughout; the original pdn source
  actually reads `hb` (blue histogram) for that one loop's leading-zero skip — a real upstream
  bug, invisible on typical fully-opaque images since the alpha histogram is a single spike.
  Ported correctly here rather than faithfully copying the bug.

**Verified for real, not just "it compiles":** all 10 added to `KawaPaint.Sandbox/Program.cs`'s
smoke-test `effects[]` array (runs the full apply→clip→history pipeline through real
`KawaPaint.Engine` types) — passes. A separate scratch harness applied each effect to a 64×64
test-pattern surface, confirmed every one actually changes pixels (not a silent no-op), eyeballed
the PNG output for each (bulge visibly bulges, twist/polar-inversion produce coherent swirl/
kaleidoscope patterns, median visibly removes the thin diagonal test line while preserving block
edges, outline highlights exactly the color-block boundaries, vignette darkens the corners —
all correct), and ran all 10 against deliberately degenerate sizes (1×1, 2×2, 3×7, 1×40) to catch
divide-by-zero/out-of-range crashes from `maxRadius==0`, wrap-modulo on a 1px-wide image, etc. —
no crashes. Beyond the headless checks, also built and launched the real Windows desktop app
(`win/KawaPaint.Win.csproj`) and drove it live: confirmed the `Distort`/`Stylize` submenus list
all 10 items, opened the Bulge dialog and dragged its slider — the live preview visibly bulged the
test image in real time — clicked OK and confirmed it committed (status bar showed "Bulge", title
bar picked up the unsaved-changes `*`, no crash), and opened the Vignette dialog to confirm its
sliders default to pdn's own defaults (Amount 1.00, Radius 0.50).
**Windows UI-automation gotcha for next time:** this box has no input-automation tool preinstalled
either (same as the Linux note below), but plain PowerShell + `user32.dll` P/Invoke
(`SetCursorPos`/`mouse_event` for clicks, `GetWindowRect`/`SetForegroundWindow` for the target
window, `System.Drawing.Graphics.CopyFromScreen` for screenshots) works fine for driving a real
Avalonia window and needs no extra install. **Also hit and worth remembering:** `dotnet build` on
this repo's `win/` project writes to *two* separate output dirs —
`win/bin/Debug/net10.0/KawaPaint.Win.exe` (plain build) and a stale
`win/bin/Debug/net10.0/win-x64/KawaPaint.Win.exe` left over from an earlier RID-specific
build/publish — only the former gets refreshed by a plain `dotnet build`. Launching the `win-x64`
one silently ran yesterday's binary with none of today's changes; always check both paths'
timestamps (or delete the stale one) rather than assuming the first exe `find` turns up is current.

**Remaining 17 effects done 2026-08-19 (second pass, same day)** — Blurs: Motion Blur, Radial
Blur, Zoom Blur, Surface Blur, Unfocus, Fragment; Distort: Dents; Stylize: Reduce Noise; Render:
Clouds, Julia Fractal, Mandelbrot Fractal; Photo: Glow, Red Eye Removal, Soften Portrait; Artistic:
Ink Sketch, Pencil Sketch, Oil Painting. This closes out the full pdn effect list from the original
plan — **2.1 effect catalogue is done** (30 effects total across both passes), modulo the
"Tools that came bundled with these in pdn, not effects" item below, which was never in scope as
an effect.

New files: `PerlinNoise2D.cs` (shared by Dents and Clouds — ported from pdn's own
`PerlinNoise2D.cs`), `Effects.Blur.cs`, `Effects.Noise.cs`, `Effects.Render.cs`, `Effects.Photo.cs`
(also defines internal `BlendOps` — Screen/Overlay/Darken/ColorDodge, standard two-layer blend-mode
formulas used in place of pdn's alpha-compositing-aware `UserBlendOps`, since here one side of
every blend is always effectively opaque), `Effects.Artistic.cs`. `WarpEffect` (`Effects.Distort.cs`)
gained a third `WarpEdgeMode.Reflect` for Dents (pdn's own choice for it — avoids the smeared look
Clamp gives a noise-driven ripple at the image edge).

Compositional reuse, matching how pdn itself builds these on top of each other: `GlowEffect` is
blur+brightness/contrast+Screen-blend, and `InkSketchEffect` calls `GlowEffect` directly for its
background pass, exactly like the pdn original does. `PencilSketchEffect`/`SoftenPortraitEffect`
similarly compose the existing `BoxBlurEffect`/`BrightnessContrastEffect`/`InvertEffect`/
`GrayscaleEffect` rather than duplicating blur/adjust logic.

**A real bug was found and fixed during verification, not just during review:** `WarpEffect`'s new
`ReflectCoord` helper (`value += max` / `value -= max` to bounce a coordinate back into range)
infinite-loops whenever `max <= 0` — i.e. whenever the image is exactly 1px wide or 1px tall, since
adding/subtracting zero never converges. This is invisible on any normal canvas size and only
`DentsEffect` uses Reflect mode, which is exactly why the project's boundary-size test battery
(1×1, 1×40, 40×1, ...) exists — a plain "does it look right" check on a 64×64 test image would
never have caught it. Root-caused by timing each effect against each boundary size individually
after the full batch run hung with zero output (confirmed the hang was real CPU-bound spinning, not
a slow build, via `Get-Process`'s CPU-seconds counter climbing continuously) rather than assuming
a cause. Fixed with a one-line early return (`if (max <= 0) return 0`); re-ran the full boundary
battery clean afterward.

**Other simplifications vs. pdn, same "flag if it turns out to matter" spirit as the first pass:**
- `MotionBlurEffect`/`RadialBlurEffect`/`ZoomBlurEffect` drop pdn's fixed-point rotation-matrix
  math (a perf trick from 2007-era hardware) for plain `Math.Cos`/`Math.Sin` per sample — same
  visual result, much simpler to read and verify.
- `UnfocusEffect` is a genuinely circular-kernel *unweighted* mean (reusing `LocalHistogramEffect`),
  not pdn's alpha-weighted premultiplied version — matches this codebase's existing
  non-alpha-weighted convention (`BoxBlurEffect` already averages B/G/R/A independently, not
  alpha-weighted either), and is the real reason to have Unfocus at all alongside the existing
  square/separable Gaussian Blur menu entry: a genuinely round kernel, visible at hard silhouette
  edges.
- `JuliaFractalEffect`/`MandelbrotFractalEffect` drop pdn's quality-supersampling loop (single
  sample per pixel, consistent with dropping AA everywhere else in this catalogue).
  `MandelbrotFractalEffect`'s `InvertColors` checkbox was dropped from the dialog entirely (always
  false) — `AdjustmentDialog` only has sliders, no checkbox control; flag if worth adding.
- The `BlendOps` formulas (Screen/Overlay/Darken/ColorDodge) are the standard documented two-layer
  blend-mode math, not a transcription of pdn's `UserBlendOps.Generated.cs` — that file is
  macro-generated fixed-point code whose complexity is almost entirely about correct alpha
  compositing for general layer blending, which these effects don't need (one side of each blend
  is always the fully-opaque result of a prior step). Double-checked the base/blend argument order
  against pdn's actual `ColorDodgeBlendOp.Apply(lhs,rhs)` generated code (confirmed `lhs`=base,
  `rhs`=blend) rather than assuming — this mattered: an initial guess at `SoftenPortraitEffect`'s
  Overlay argument order was backwards and got corrected once the real generated code was checked.
- `RedEyeRemoveEffect` ported `UnaryPixelOps.RedEyeRemove` faithfully, including a detail worth
  knowing if it looks weirdly aggressive/timid in practice: the saturation *slider* only controls
  how much residual redness survives removal — detection itself uses a hardcoded 100/255 threshold
  in `GetSaturation()`, unrelated to any slider. That's pdn's actual behavior, not a shortcut.

**Verified the same way as the first pass, no shortcuts taken on rigor:** all 17 added to
`KawaPaint.Sandbox/Program.cs`'s smoke test (41 effects total now, full pipeline). A second scratch
harness ran all 17 against the same 64×64 test pattern + a dedicated strongly-red test surface for
`RedEyeRemoveEffect` (the quadrant pattern has no red pixel saturated enough to trigger it) +
the same degenerate-size battery — this is what caught the ReflectCoord hang. `SurfaceBlurEffect`
showed `changed=False` on the quadrant test image; rather than accepting that, re-tested it against
a noisy-flat image instead (piecewise-constant blocks have no soft gradient for an edge-preserving
blur to act on) — confirmed real: noise variance in a flat region dropped from 24.26 to 0.41 while
the hard region boundary stayed exactly crisp (59 vs. 201, no bleed), proving the "no visible
change" on the first test was correct edge-preserving behavior, not a dead effect. All 17 outputs
individually eyeballed (Clouds renders a real cloud texture, Julia/Mandelbrot render actual
fractals, PencilSketch looks convincingly like a graphite sketch, RedEyeRemove darkened only the
saturated-red test surface). Live in the real Windows app: all 6 new submenus (Blurs/Render/
Photo/Artistic plus the extra Distort/Stylize entries) list the right items; opened Clouds,
confirmed the OK-without-touching-a-slider case is a pre-existing `AdjustmentDialog` quirk (Preview
only runs on a slider's `ValueChanged`, so committing untouched does nothing — true for every
adjustment dialog in the app, not new) rather than mistaking it for a bug in the new effect; dragged
the Power slider and confirmed a real black/white cloud pattern rendered live using the canvas's
actual current Fg/Bg colors, committed it, no crash.

**The four bundled tools are also done, 2026-08-19 (same day, third pass).** Clone Stamp, Recolor,
Rounded Rectangle, and Freeform Shape — the pdn "Tools that came bundled with these" item flagged
above as genuinely out of scope for the effect catalogue turned out to fit this codebase's existing
`ITool` architecture (`Tools.cs`) cleanly once actually looked at: unlike `IEffect`, `ITool` already
gets live pointer input (`PointerDown`/`PointerMove`/`PointerUp`) via `ToolContext`, so these were
never blocked on `AdjustmentDialog` at all — that framing in the paragraph above was about the
*effects*, not a real blocker for the tools themselves. Real source this time came from
`origin/3.36pdn`'s `src/tools/` (not `src/Effects/`) — `CloneStampTool.cs`, `RecoloringTool.cs`,
`RoundedRectangleTool.cs`, `FreeformShapeTool.cs`, `ShapeTool.cs` — fetched the same
`git cat-file -p $(git rev-parse ...)` way as the effects (all five turned out to be plain UTF-8,
no repeat of the encoding gotcha). These are real WinForms `Tool` subclasses with hundreds of lines
of cursor/undo/GDI+ plumbing per file; only the core per-pixel/per-path algorithm was ported from
each, same "not a literal file port" approach as the effects.

New engine primitives (`BrushOps.cs`: `CloneDisc`/`CloneLine`, `RecolorDisc`/`RecolorLine`;
`ShapeOps.cs`: `FillRoundedRectangle`/`DrawRoundedRectangle`, `FillPolygon`/`DrawPolygon`) plus four
new `ITool`s in `Tools.cs`, wired into the Tools panel (new icons in `Icons.cs`) with fresh
shortcuts C/N/U/D (P/E/F/K/L/R/O/G/T/M/S were already taken).

- **Clone Stamp**: Ctrl+click sets the source point (no undo step — nothing was painted), then a
  plain drag paints from that source, offset re-anchored at the start of each stroke so repeated
  strokes stay relative to the same fixed source. `ToolContext` gained a `CtrlHeld` field
  (`SurfaceView.OnPointerPressed` reads `e.KeyModifiers`) since no existing tool needed keyboard
  modifier state before this. Samples from `PreStroke` (the layer as it was before the current
  stroke began), not the live surface, so stamping over the source area mid-stroke can't feed back
  into itself as a smear — pdn's own tool has the same property structurally (it snapshots into
  `PlacedSurface`s), confirmed by reasoning about the source rather than by inspection of a
  specific line, since the actual C++/GDI+ plumbing doesn't translate directly.
- **Recolor**: brushes areas close to the *background* color over to the *foreground* color,
  adding the Bg→Fg offset onto each pixel's actual value rather than flattening to a flat color —
  so antialiasing/shading at the edge of what's being recolored carries through unscathed. Ported
  pdn's `RecoloringTool.DrawOverPoints`'s core color-adjustment math
  (`adjusted = lifted + (replacing - toReplace)`, clamped per channel) faithfully, but the
  tolerance test reuses this codebase's own `FloodFill`-style per-channel max-difference metric
  (already what the Tolerance slider means everywhere else in the app — Paint Bucket, Magic Wand)
  rather than porting pdn's separate, differently-scaled `Utility.ColorDifference`. Also ported
  pdn's `RestrictTolerance()` guard: tolerance is capped at the Fg/Bg color difference so a second
  pass over already-recolored pixels can't keep "recoloring" them and drift/oscillate.
- **Rounded Rectangle**: pdn's original doesn't expose a corner-radius control at all (hardcoded
  `radius = 10`) — matched that "just works, no new UI" spirit with a fixed formula
  (`Math.Max(8, BrushWidth * 2)`) instead of porting its GDI+ arc-path/capsule-fallback
  construction. The rasterizer here is a from-scratch raster-native replacement, not a port: a
  single clamped-distance-to-corner test (`InsideRoundedRect`) handles straight edges and all four
  rounded corners without branching, and — this is the part that isn't just a simplification, it's
  a correctness observation pdn needed a special `GetCapsule` fallback for — the same test
  naturally degrades to a capsule or circle once the radius reaches half the shorter side, with no
  special-casing needed. The outline uses a proper "rounded box" signed-distance-field for the
  stroke ring (`RoundedRectDistance`, the standard SDF formula), which is *more* capable than the
  raw inside-test alone: it's what lets `DrawRoundedRectangle` antialias its edge the same way
  `BrushOps`' round brush already does, at no extra design cost.
- **Freeform Shape**: accumulates points while dragging exactly like the existing `LassoSelectTool`
  (this codebase already had the right shape for this), but stamps a filled/outlined polygon onto
  the layer at release instead of replacing the selection. `ShapeOps.FillPolygon` is the same
  even-odd scanline algorithm already in `Selection.ReplaceWithPolygon`, just writing pixels
  instead of mask bits — reused the algorithm, not the code, since one operates on a `Surface` and
  the other on a mask array.

**Verified with the same rigor as the effects passes.** Engine primitives first, headlessly,
against a dedicated scratch harness (not folded into the visual-pattern harness the effects used,
since these needed different setups per primitive): clone-stamped a distinctive 6×6 patch to a
known offset and confirmed the destination pixels matched while pixels outside the brush radius
stayed untouched; recolored a patch and confirmed (a) the bulk of it flipped to the target color,
(b) an unrelated nearby color was left alone, and (c) a deliberately pre-shaded pixel landed
somewhere between the old and new color rather than flattening to either exactly (debug-printed the
actual channel values to confirm this by eye, not just by the boolean assertion) — two of the
initial assertions in this harness were themselves wrong (mismeasured which point fell inside a
12px brush radius, and misread which of BGR channel was dominant in the test colors), caught by
checking the debug output before accepting a FAIL as a real bug, not by assuming the first failure
must be in the new code. Rounded-rectangle and polygon fill/outline were checked both by pixel
assertion (corner pixel empty vs. edge-center pixel filled; radius 0 behaves like a sharp rectangle;
an oversized radius doesn't throw) and by eye against saved PNGs. All of the above also run against
degenerate 1×1 surfaces and radius-0/point-count-0 inputs — no crashes. Beyond the headless layer,
also **added the new primitives to `KawaPaint.Sandbox`'s permanent smoke test** (unlike the effects,
which got their own scratch harness only — these were cheap enough to fold into the permanent one
directly) alongside the existing brush/shape calls.

Live in the real Windows app, all four were driven through an actual full gesture, not just opened:
Clone Stamp — Ctrl+clicked a red square to set the source, dragged over a distant green area, and
the composited screenshot shows an unmistakable red stroke following the drag path with the brush
cursor circle correctly tracking the pointer. Recolor — set Bg to the square's red and Fg to white
via the palette swatches, dragged across the square; at the default Tolerance (32) nothing visibly
changed (the layer's actual raw color didn't fall within 32 of the exact swatch hex — a real
pixel-value gap worth knowing about, not a bug in the tool), raising Tolerance to 200 and repeating
produced a clean white diagonal stroke cutting through the square exactly along the drag path.
Rounded Rectangle — dragged out a shape small enough that the fixed corner radius exceeded half its
shorter side, and it correctly rendered as a stadium/capsule shape rather than anything jagged,
confirming the corner-radius-clamping behavior live, not just in the headless assertion. Freeform
Shape — dragged a seven-point irregular path and it rendered as a correctly closed, filled polygon
matching the traced path exactly, snapping shut back to the start point as intended.

**Small UI-automation lesson from this pass, for next time:** an editable Avalonia `ComboBox`
(the Size/Tolerance boxes) can be set reliably from PowerShell with a click to focus +
`[System.Windows.Forms.SendKeys]::SendWait("^a"); SendKeys::SendWait("<value>{ENTER}")` — no need
for anything fancier. Also, `Add-Type`'s embedded C# compiler is old enough to reject tuple-type
parameters (`(int,int)[]`) and needs `System.Drawing.Point` pulled in explicitly via
`-AssemblyName System.Drawing` if you want `Point[]` instead of two parallel `int[]` arrays for a
multi-waypoint drag helper — parallel arrays were less fuss than chasing the assembly reference.

This closes out every remaining item from the original 2.1 scope, tools included.

### 2.3 — Git-backed history + forge integrations
**Local half done 2026-08-19 (Windows machine, same session as the JP2 codec above); forge half
deliberately not started — scoped down explicitly with the user before starting, see below.**

**The blocker is solved.** `.kwp` was a ZIP of PNGs — opaque to git, full rewrite every save.
`shared/KawaPaint.Engine/DocumentFile.cs` now has `SaveExploded(doc, directoryPath)`/
`LoadExploded(directoryPath)` alongside the original zip Save/Load, writing plain
`manifest.json` + `layers/N.png` (no outer archive), sharing the same private `Manifest`/
`LayerInfo` shape as the zip path. Verified for real, not just "it compiles": a 3-layer, odd-sized
(23×17) document round-trips byte-exact through `SaveExploded`→`LoadExploded`; layer metadata
(opacity etc.) survives; and re-saving with fewer layers deletes the now-stale `layers/N.png`
files rather than leaving orphans behind for git to keep confusedly tracking.

**`AppSettings.Git` now has a real reader.** New `shared/KawaPaint.App/Core/GitService.cs`
(static, LibGit2Sharp-backed): `EnsureRepository(path)` (git-inits if missing, no-ops if already a
repo), `CommitAll(path, message)` (stages everything, commits, returns `false` — not an error —
when nothing actually changed, so a save that re-encodes to byte-identical PNGs doesn't create an
empty commit), `EnsureGitIgnore(path, patterns)`. Every method swallows its own failures and
reports back via an `out string? error` rather than throwing, same "must never interrupt the
user's actual work" rule `AutosaveService` already follows — a failed commit is a lost convenience,
not a lost save.

Two consumers wire it to the two settings that were previously dead:
- **`TrackConfiguration`** — new `shared/KawaPaint.App/Core/ConfigGitTracker.cs`, constructed
  alongside `AutosaveService` in `MainView`'s constructor. Subscribes to
  `SettingsService.Changed`; the first time `Git.Enabled && Git.TrackConfiguration`, it git-inits
  `AppPaths.Root` (the same directory `settings.json`, `recovery/`, `history-cache/`, and
  `presets/` already live in — see `AppPaths.cs`'s own comment: "turning on git tracking means
  tracking one location") and writes a `.gitignore` excluding `recovery/` (timestamped binary
  autosave snapshots — noise, not history) and `history-cache/` (pure scratch, already deleted on
  every startup). Every subsequent settings save commits.
- **`TrackProjects` / `CommitOnSave` / `CommitOnAutosave`** — a project opts in by explicitly
  linking a folder (new "Link Git Project Folder..." command in `MainView`, `file.linkGitProject`,
  no default gesture), which calls `DocumentSession.SetGitProjectDirectory` (new nullable property
  + setter on `DocumentSession`, cleared on `Reset` so New/Open don't inherit a stale link). This
  is **additive, not a replacement**: the linked folder is a mirror the exploded format gets
  written into and committed, entirely separate from whatever `.kwp` file the document's normal
  Ctrl+S still saves to unchanged. `SaveProjectAsync`'s success path and `AutosaveService.Saved`
  each call a small `CommitGitProject(autosave: bool)` helper in `MainView` that no-ops instantly
  for the overwhelming majority of documents that were never linked, and otherwise
  `SaveExploded`s into the linked folder and commits, gated on the matching `CommitOnSave`/
  `CommitOnAutosave` flag.

**Why additive-mirror instead of making the exploded folder the primary save format:** the
alternative — having `.kwp` open/save work on directories instead of files when git tracking is on
— touches the file-picker flow, the `IStorageFile _currentFile` field threaded through ~10 call
sites in `MainView.axaml.cs`, the recent-files MRU list, and the open dialog's folder-vs-file
picker choice. That's a real UX redesign with regression risk for the 95% of users who will never
turn git on, to save what amounts to one extra file-copy step for the 5% who do. Flag if the user
wants that fuller integration later — the mirror approach doesn't block it, it just doesn't
preclude keeping today's save flow exactly as-is either.

**Verified for real** (`scratchpad/gitspike`, not committed — same `ProjectReference`-to-the-real-
project pattern as the codec spikes): `GitService.EnsureRepository`/`CommitAll` produce an actual
`.git` directory and real commits (inspected via LibGit2Sharp's own `Repository.Commits`, not by
re-parsing our own output); the core "a commit only touches the layers that actually changed"
claim was checked by diffing two real commits' trees (`repo.Diff.Compare<TreeChanges>`) after
editing one pixel in one of three layers — the second commit's tree diff contains exactly
`layers/1.png` and nothing else; a same-content re-save produces no commit at all (checked as a
commit-count-unchanged assertion, not just a return value); and `ConfigGitTracker` was driven
through a real `SettingsService` (a new `FileSettingsStore.Create(root)` test-only overload was
added to `ISettingsStore.cs` so this could point at a scratch directory instead of the user's real
`%APPDATA%\KawaPaint` — the existing `TryCreate()` factory hardcoded that path with no way to
override it) — confirmed `Git.Enabled` defaults false and nothing touches the filesystem until
it's turned on, and confirmed `recovery/`/`history-cache/` end up in the tracked commit's tree
listing exactly zero times once gitignored.

**Verified the WASM/browser build still works with `LibGit2Sharp` as a dependency of the *shared*
`KawaPaint.App` project** (which `KawaPaint.Web.csproj` also references) — a real risk since
LibGit2Sharp ships native `libgit2` binaries with no `browser-wasm` RID asset. Did not assume this
was fine; ran both `dotnet build` and a full `dotnet publish -c Release` of `web/KawaPaint.Web.csproj`
before committing to the single-project design (rather than the alternative of isolating git code
into a separate desktop-only project) — both succeeded, no native-asset resolution error, same
graceful-absence pattern the JXL/JP2 P/Invoke codecs already rely on for platforms without their
native library.

**Signature note:** commits use `repo.Config.BuildSignature` (the user's own configured
`user.name`/`user.email` from git config, so `git log` looks like theirs), falling back to a fixed
`KawaPaint <kawapaint@localhost>` identity only if `BuildSignature` throws (no git identity
configured anywhere on the machine) — `BuildSignature` throws rather than degrading on its own, so
this fallback is required, not defensive-for-its-own-sake.

Scope per the user's ruling: git-compatible history **beats** arbitrary history editing — so this
stays on **snapshot mementos + truncate-only deletion** (already how `HistoryStack` works), not a
replayable command log. Don't revisit that trade without asking.

**Forge integrations (GitHub/GitLab/Gitea) — explicitly out of scope for this pass, not forgotten.**
Before starting 2.3 the user was asked how far to take it in one session: local git only, vs. local
git plus forge integration with an OAuth/token design decided now. They chose local-only — the
forge half has real unresolved design questions (device-flow OAuth vs. personal access tokens,
where to store tokens safely per-platform: keychain/secret-service/DPAPI, none wired yet) that
deserve their own scoping pass rather than being decided as a side effect of finishing the local
half. Design sketch, unchanged from before:
```csharp
interface IForgeProvider {
    string Id { get; } // "github" | "gitlab" | "gitea"
    Task Authenticate(...);
    Task<IReadOnlyList<RepoInfo>> ListRepositories();
    Task<RepoInfo> CreateRepository(string name, bool private_);
    string ResolveCloneUrl(RepoInfo repo);
}
```

### 2.4 — Native plugin API
Not started at all. Rough shape discussed: effect/codec/tool contributions loaded from a
`plugins/` directory via an isolated `AssemblyLoadContext`, declarative property UI (so a plugin's
options dialog renders in Avalonia without the plugin shipping UI code), enable/disable list
(`AppSettings.Plugins` already has `Enabled`/`SearchPaths`/`Disabled` — unused so far), per-plugin
failure reporting.

### 3.x — Spikes (time-box before committing a schedule, don't just build)
- **Paint.NET plugin compatibility.** Genuinely split: effects built on `PropertyBasedEffect` /
  IndirectUI declare their dialogs as data → portable, would work through a shim on top of 2.4.
  Effects with a hand-written WinForms `EffectConfigDialog` **cannot** show their UI outside
  Windows — best case is running with default settings, or Windows-only. Spike: load 3 widely-used
  real plugins, see which bucket they land in, before scoping anything further.
- **JPEG XL / JPEG 2000 — spiked 2026-08-19, feasibility confirmed, not yet wired.** Neither is
  impossible, both are impractical-but-accepted (user's explicit call). Both are desktop-only, no
  WASM path, permanent packaging/CI-matrix tax either way. Wire through the existing
  `IImageCodec`/`CodecRegistry` — that plumbing is exactly what's needed.

  **Option A — Magick.NET-Q16-AnyCPU 14.16.0 (verified working).** Added the nuget package in a
  throwaway console project, ran an actual encode+decode roundtrip (not just a format-list check):
  both JXL and JP2 round-tripped an 8x8 test image correctly. Ships prebuilt natives for all 8 RIDs
  (win-x64/x86/arm64, linux-x64/arm64/musl-x64, osx-x64/arm64) — covers every desktop target in one
  dependency. Cost: one native blob per platform, 19-38MB, full ImageMagick rather than scoped to
  just these two formats — acceptable if a single dependency beats two hand-written P/Invoke
  bindings.

  **Option B — direct P/Invoke, verified working for JXL 2026-08-19.** Wrote real bindings
  (`JxlEncoderCreate`/`SetBasicInfo`/`FrameSettingsCreate`/`SetFrameLossless`/`AddImageFrame`/
  `ProcessOutput`, `JxlDecoderCreate`/`SubscribeEvents`/`SetInput`/`ProcessInput`/`GetBasicInfo`/
  `ImageOutBufferSize`/`SetImageOutBuffer`, plus the `JxlBasicInfo`/`JxlPixelFormat` struct layouts
  from `/usr/include/jxl/{decode,encode,codestream_header,types}.h`) against the system
  `libjxl.so` 0.12.0 in a throwaway console project, encoded a 4x4 RGBA random-pixel image
  lossless and decoded it back — byte-for-byte pixel match, not just "it ran". No color-encoding
  call needed: per libjxl's own doc comment on `JxlEncoderAddImageFrame`, omitting
  `JxlEncoderSetColorEncoding`/`SetICCProfile` defaults to nonlinear sRGB for UINT8/16, which is
  what KawaPaint's `Surface` already assumes.

  **Size, decisive point per the user's "as optimized as possible" ask (2026-08-19):**
  hand-rolled needs `libjxl.so` (5.4M) + `libjxl_cms` (200K) + `libhwy` (60K) + brotli enc/dec/common
  (~1.1M) = **~6.7MB total, JXL only**. Magick.NET's single native blob is **20-38MB per platform**
  for JXL+JP2 riding along with 274 unused formats. Hand-rolled is ~3-5x smaller and scoped to
  exactly what's used.

  JP2 not yet spiked the same way (`libopenjp2` 2.5.4 confirmed present via pkg-config, its C API
  not yet bound) — same pattern would apply, do it before wiring either format in.

  **Decided: Option B (hand-rolled P/Invoke), not Magick.NET.**

  **JXL — actually wired in, 2026-08-19, not just spiked.** `shared/KawaPaint.Engine/Codecs/JxlCodec.cs`,
  registered in `CodecRegistry`. Bindings use `LibraryImport` (source-generated marshalling), not
  `DllImport` — no reflection-based stub, AOT-compatible, in line with the "as optimized as
  possible" ask that decided against Magick.NET in the first place. `JxlBasicInfo`'s 100-byte
  padding field had to become an `unsafe fixed byte[100]` rather than a `byte[]` with
  `MarshalAs(ByValArray)` — the source generator (`SYSLIB1051`) rejects non-blittable struct
  fields, `DllImport` would have accepted it silently. Surface is BGRA; libjxl has no BGR(A) pixel
  format (its own `types.h` says so), so encode/decode each do one channel-swap pass over the
  buffer — the only per-pixel cost this adds.

  Verified through the real path, not the throwaway spike project: a 37x29 `Surface` (odd
  non-power-of-two dims, random BGR, alpha swept 0-255 across pixels to catch a premultiply
  mistake) round-tripped through `CodecRegistry.Encode`/`.Decode` — lossless came back
  byte-for-byte identical including alpha, and `Decode` with no filename correctly header-sniffed
  the JXL container via `MatchesHeader`/`JxlSignatureCheck` rather than needing the extension.
  Lossy (`Quality = 80`) encodes smaller and decodes back to the right dimensions (not checked
  pixel-exact, lossy by definition isn't).

  Still open: JP2 P/Invoke binding (not started — see resume plan right below), and the
  still-unsolved Windows/macOS packaging story (vcpkg for `libopenjp2`; a libjxl release/build for
  the same platforms) — the P/Invoke path assumes the system already has `libjxl`/`libopenjp2`
  installed, true here via CachyOS's package but not true on a clean Windows/macOS machine.
  `JxlCodec.IsAvailable` degrades cleanly to false there (probes `JxlDecoderVersion()`, catches
  `DllNotFoundException`) so it just won't show up in file dialogs rather than crashing — but it
  needs bundled natives shipped with the app on those platforms before this is actually usable off
  this box.

  **JP2 — actually wired in, 2026-08-19 (same day, different machine — see the machine-switch note
  below), not just spiked.** `shared/KawaPaint.Engine/Codecs/Jp2Codec.cs`, registered in
  `CodecRegistry`. Same `IImageCodec`/`LibraryImport` pattern as `JxlCodec.cs`.

  The two differences flagged in the old resume plan were real and both got solved:
  1. **No buffer-in/buffer-out API** — solved with an `opj_stream_t` wired to
     `[UnmanagedCallersOnly]` read/write/skip/seek callbacks against a `GCHandle`-tracked state
     object (`Native.StreamState`). Decode reads sequentially out of a `byte[]` already buffered
     from the input `Stream` (same as `JxlCodec.Decode`, sidesteps needing the caller's stream to
     be seekable). Encode writes into an internal `MemoryStream` — required, not a style choice:
     `opj_end_compress` seeks *backward* mid-write to patch box-length headers, which only a
     random-access sink supports — then copies the finished bytes to the real output `Stream` in
     one sequential pass at the end, so the caller's stream never needs to support seeking either.
  2. **Planar pixel buffers** — solved with a per-component gather/scatter loop against `Surface`'s
     interleaved BGRA, folding in the same R/B swizzle `JxlCodec` needs (`OPJ_CLRSPC_SRGB` implies
     component order R,G,B,[A]). Decode handles 1/2/3/4-component images (gray, gray+alpha, RGB,
     RGBA) and rescales non-8-bit `prec` by shifting rather than assuming 8-bit blindly.

  **The actual hard part turned out to be `opj_cparameters_t`'s layout, not the two flagged
  risks.** It's ~18.7KB with 100+ fields, including a 32-element array of nested `opj_poc_t`
  structs (148 bytes each, itself hand-computed) sitting *before* every field this codec actually
  sets (`irreversible`, `tcp_numlayers`, `tcp_rates`, `numresolution`) — so a single wrong offset
  anywhere in the first 4.8KB would have silently misaligned every field after it. No C compiler
  was available on the Windows box this got built on (no cl.exe/gcc/clang, no vcpkg/choco/scoop —
  checked), so there was no `offsetof()` ground truth to check the hand-transliteration against, the
  way a normal port of a struct like this would get verified. Solved by cross-checking at runtime
  against the real `openjp2.dll` instead: `opj_set_default_encoder_parameters`'s doc comment
  explicitly lists its defaults ("Lossless / 1 tile / 64x64 code-block / 6 resolutions / LRCP / no
  ROI upshifted"), so calling it and reading back `prog_order`, `cblockw_init`, `cblockh_init`,
  `numresolution`, `irreversible`, `roi_compno`, etc. at their hand-computed offsets against those
  documented values is a real correctness check, not a guess — and if any offset upstream were
  wrong, at least one of these would read back garbage. All of them matched on the first run after
  a copy-paste fix (see bugs below), across the smallest fields (`tile_size_on` at offset 0) through
  the ones sitting right after the 4.8KB POC block (`numresolution`, `irreversible`) — meaning the
  offset chain is correct through the field furthest from the start that this codec touches.

  **Verification, real round trips, not just the struct-layout check:** downloaded the official
  `uclouvain/openjpeg` v2.5.4 Windows x64 release (prebuilt `openjp2.dll` + headers + the
  `opj_compress`/`opj_decompress` reference CLI tools) for a from-scratch spike project
  (`scratchpad/jp2spike`, not committed) with a `ProjectReference` to the real
  `KawaPaint.Engine.csproj`. Checked, in order: (1) our encoder's output decodes correctly with the
  *real* `opj_decompress.exe`, not just our own decoder; (2) our own encode→decode round trip is
  byte-exact lossless, including a 37×29 (odd, non-power-of-two) surface with a full BGR random
  sweep and a 0–255 alpha ramp, run through the actual `CodecRegistry.Encode`/`.Decode` — not the
  spike's own bindings — with decode header-sniffed (no filename given), matching the bar the JXL
  codec was held to; (3) our decoder correctly reads the *real* `opj_compress.exe`'s output,
  byte-exact — closes the loop the other direction, so a bug that happened to be symmetric between
  our own encode and decode paths couldn't hide; (4) lossy path produces smaller output and stays
  visually close on a worst-case random-noise image; (5) tiny images down to 1×1 round-trip
  byte-exact (see bug 2 below); (6) with `openjp2.dll` removed from the output directory,
  `Jp2Codec.IsAvailable` returns `false` without throwing and the format silently drops out of
  `CodecRegistry.Encoders`/`.Decoders` — the graceful-degradation path actually works, not just in
  theory.

  **Two real bugs found and fixed during verification (both would have shipped silently broken
  without the round-trip tests above — the struct-layout check alone would not have caught
  either):**
  1. The write callback wrote into the output `MemoryStream` without first syncing its `Position`
     to the codec's own tracked position. `opj_end_compress` seeks backward to patch box-length
     headers, and `MemoryStream.Write` only advances *its own* internal position — never told about
     the seek, so post-seek writes landed at the wrong offset and silently corrupted the box
     headers. Symptom: both the real `opj_decompress.exe` and our own decoder failed identically
     ("Expected a SOC marker") on our encoder's output. Fixed by setting
     `output.Position = state.Position` before every write, matching what the read callback already
     did.
  2. openjpeg's default `numresolution = 6` requires `2^(numresolutions-1) <= min(width, height)`,
     i.e. the short side must be at least 32px — fails outright ("Number of resolutions is too high
     in comparison to the size of tiles") on anything smaller, which includes ordinary icon-sized
     images. Fixed with a `ClampResolutions(width, height)` helper (`shared/KawaPaint.Engine/Codecs/
     Jp2Codec.cs`) that picks the largest valid resolution count, always applied rather than only
     for small images. Verified against 16×16, 4×4, 3×7, and 1×1.

  Quality mapping: unlike JPEG's IJG 1-100 scale or JXL's `JxlEncoderDistanceFromQuality`, JP2 has
  no standard "quality" concept — `EncodeOptions.Quality` maps onto a compression ratio
  (`tcp_rates[0] = 101 - quality`, `cp_disto_alloc = 1`), a documented judgment call, not a
  perceptual calibration. Revisit if real images show it's poorly scaled in practice.

  Still open, same as JXL: bundled natives for Windows/macOS (this box now has real Windows x64
  `openjp2.dll` + dependent MSVC runtime DLLs sitting in the spike's scratchpad from the
  verification above — not yet copied anywhere the app itself would find them at runtime). Until
  then `IsAvailable` correctly degrades to false off dev boxes with the library preinstalled.

  **Mid-project machine switch, worth knowing if something here looks inconsistent:** the JXL work
  and this file's original resume plan were written on a Linux (CachyOS) box; this JP2 work was
  done in the very next session, same day, on a Windows box instead — different filesystem, no
  system package manager for native libs, and initially no C compiler of any kind (see above). The
  dotnet SDK *is* installed on this Windows box (10.0.400) but is not on `PATH` in a fresh shell —
  invoke it via its full path, `C:\Program Files\dotnet\dotnet.exe`, or add that directory to
  `PATH` for the session, or things like `dotnet build` will fail with a plain "not recognized"
  error that has nothing to do with the project itself.

### 4.x — Deferred, gated on other decisions
Branching/non-linear history and git-as-literal-undo-timeline are gated on revisiting the
snapshot-vs-command-log ruling above — don't build without an explicit go-ahead, the user
prioritized git-compat truncate-only over this.

## Open decisions (assumed defaults below; flag if the user should be asked explicitly)
- Is the browser/WASM build first-class? Assumed: demo target, features gracefully absent there.
- Snapshot vs. replayable command log for history? Assumed: snapshots (settled per the ruling
  above, but the command-log alternative is what unlocks Tier 4 — noted here as the fork point).
- Git scope: backup/versioning of projects + config, or the literal undo timeline? Assumed:
  backup/versioning only.
- Native plugin API before Paint.NET compat, or the reverse? Assumed: native API first (2.4),
  Paint.NET compat as an adapter on top of it later.

## Working notes for whoever resumes

- **No input-automation tool in this sandbox** (no xdotool/wl-copy/ydotool/xclip). Verification
  pattern used throughout: (1) headless console harness project under
  `/tmp/.../scratchpad/codectest` with a `ProjectReference` to the real `KawaPaint.Engine`/
  `KawaPaint.App` csproj — exercises actual production types, not reimplementations; (2) for
  anything requiring a real render, a *temporary* debug hook spliced into `MainWindow.axaml.cs` or
  `MainView.axaml.cs` (auto-open a dialog, force-select a tool, inject fake data) + `spectacle -b
  -n -a -o out.png` to screenshot, then revert the hook before committing. Never ship a debug hook.
- Build: `dotnet build KawaPaint.slnx`. Run desktop: `dotnet run --project linux/KawaPaint.Linux.csproj`
  (or `dotnet linux/bin/Debug/net10.0/KawaPaint.Linux.dll` after a build, faster for repeat runs).
- Settings/state live at `~/.config/KawaPaint/` on Linux — delete it to reset to defaults when
  testing first-run behavior (several bugs above only showed up on a truly fresh install).
- **On Windows** (this box, as of the 2026-08-19 JP2 session): `dotnet` is installed (10.0.400) but
  not on `PATH` in a fresh shell — use the full path `C:\Program Files\dotnet\dotnet.exe`, or
  `$env:PATH += ';C:\Program Files\dotnet'` for the session. Desktop project is
  `win/KawaPaint.Win.csproj`, not the Linux one. No C compiler (cl.exe/gcc/clang) and no
  vcpkg/choco/scoop were present — checked directly, don't assume any of them exist without
  checking again. Network access to github.com worked fine for pulling reference native libraries
  when a spike genuinely needed real ground truth to verify against (see the JP2 entry above).
