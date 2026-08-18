# KawaPaint — resume-here notes

Status snapshot: 2026-08-18, branch `master`. Full roadmap/rationale lives in Claude memory
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
  selection. Not yet committed — do that before anything else if resuming here.

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

**Tier 2, started:** 2.2 custom dock done — see `MainView`'s "Dock" panel, `DockEditorDialog`,
`Core/DockEntry.cs`. Hidden by default, `Ctrl+\`` or top-right icon summons it Floating.

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
`git ls-tree -r origin/3.36pdn --name-only | grep Effects/` lists ~30 files; pull one with
`git show origin/3.36pdn:src/Effects/BulgeEffect.cs`. (The `src/` directory in the current
working tree is unrelated leftover, not this — don't confuse the two.) Follow the existing
`IEffect`/`PerPixelEffect` pattern in
`shared/KawaPaint.Engine/Effects.cs`. Full remaining list:
- **Artistic:** Ink Sketch, Oil Painting, Pencil Sketch
- **Blurs/Distort/Noise:** Fragment, Motion, Radial, Surface, Unfocus, Zoom, Bulge, Dents, Frosted
  Glass, Pixelate, Polar Inversion, Tile Reflection, Twist, Median, Reduce Noise
- **Photo/Render/Stylize:** Glow, Red Eye Removal, Soft Portrait, Vignette, Clouds (Perlin), Julia
  and Mandelbrot fractals, Outline, Relief
- **Tools that came bundled with these in pdn:** Clone Stamp, Recolor, freeform/rounded shapes

Lowest-risk Tier 2 item — mechanical, no new architecture, each effect is independent so it can
be done incrementally alongside anything else rather than as one block.

### 2.3 — Git-backed history + forge integrations
**Blocker to solve first:** `.kwp` is currently a ZIP of PNGs (see `DocumentFile.cs`) — opaque to
git, full rewrite every save. Needs an **exploded save mode**: a directory of `manifest.json` +
`layers/N.png`, so a commit only touches the layers that actually changed. Design sketch (not yet
built): a second `DocumentFile.SaveExploded(doc, directoryPath)` / `LoadExploded` pair alongside
the existing zip-based Save/Load, sharing the same `Manifest`/`LayerInfo` shape. `AppSettings.Git`
already exists (`Enabled`, `TrackConfiguration`, `TrackProjects`, `CommitOnSave`,
`CommitOnAutosave`, `RepositoryWarnSizeMegabytes`, `RemoteProvider`, `RemoteUrl`) but nothing
reads it yet.

Scope per the user's ruling: git-compatible history **beats** arbitrary history editing — so this
stays on **snapshot mementos + truncate-only deletion** (already how `HistoryStack` works), not a
replayable command log. Don't revisit that trade without asking.

Repo git library: none chosen yet. libgit2sharp is the obvious pick (mature, cross-platform) —
not yet added as a dependency.

**Forge integrations (GitHub/GitLab/Gitea):** design as one small interface so a new provider is
additive, not a rewrite:
```csharp
interface IForgeProvider {
    string Id { get; } // "github" | "gitlab" | "gitea"
    Task Authenticate(...);
    Task<IReadOnlyList<RepoInfo>> ListRepositories();
    Task<RepoInfo> CreateRepository(string name, bool private_);
    string ResolveCloneUrl(RepoInfo repo);
}
```
Open question flagged but not resolved: device-flow OAuth vs. personal access tokens for v1, and
where to store tokens safely per-platform (keychain/secret-service/DPAPI — nothing wired yet).

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
- **JPEG XL / JPEG 2000.** Neither is impossible, both are impractical-but-accepted (user's
  explicit call). JP2 → P/Invoke `libopenjp2` (BSD-2, packaged everywhere on Linux, vcpkg on
  Windows). JXL → P/Invoke `libjxl` directly, OR a custom SkiaSharp native build with
  `SK_CODEC_DECODES_JPEGXL` (decode-only). Untested shortcut worth checking first: **Magick.NET**
  ships prebuilt natives for win/linux/osx and may cover both in one dependency — verify its JXL
  delegate is actually compiled into the shipped build before relying on it. Both are desktop-only,
  no WASM path, permanent packaging/CI-matrix tax either way. Wire through the existing
  `IImageCodec`/`CodecRegistry` — that plumbing is exactly what's needed.

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
