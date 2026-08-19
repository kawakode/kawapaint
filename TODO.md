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

  **JP2 resume plan — not started, but scoped 2026-08-19 so this isn't a cold start next time.**
  Header: `/usr/include/openjpeg-2.5/openjpeg.h` on a system with the `openjpeg2` package (this
  dev box has 2.5.4); `pkg-config --cflags --libs libopenjp2` gives `-I.../openjpeg-2.5 -lopenjp2`
  elsewhere. New file should be `shared/KawaPaint.Engine/Codecs/Jp2Codec.cs`, same
  `IImageCodec`/`LibraryImport` pattern as `JxlCodec.cs`, registered in `CodecRegistry.cs` next to
  it.

  **This is a materially bigger lift than JXL was — two real differences, not just "same thing
  again":**
  1. **No simple buffer-in/buffer-out API.** libjxl's `JxlDecoderSetInput(dec, data, size)` has no
     openjpeg equivalent. Reading/writing memory (rather than a file path via
     `opj_stream_create_default_file_stream`) means building an `opj_stream_t` with
     `opj_stream_create` and wiring `opj_stream_set_read_function` /
     `opj_stream_set_write_function` / `opj_stream_set_skip_function` /
     `opj_stream_set_seek_function` / `opj_stream_set_user_data` to native callback function
     pointers. In .NET that means `[UnmanagedCallersOnly]` static methods (not managed delegates —
     those need `GetFunctionPointerForDelegate` and careful GC-lifetime pinning to not get
     collected mid-decode), reading/writing against a pinned buffer or `GCHandle`-tracked stream
     wrapper. Worth a small standalone spike first, same as JXL got, before wiring it into the
     codec proper — this callback-marshalling part is the part most likely to have a rough edge.
  2. **Planar, not interleaved, pixel buffers.** `opj_image_t` holds one `opj_image_comp_t` per
     channel (`image->comps[i].data`, a separate `OPJ_INT32*` per component, each with its own
     `dx`/`dy` subsampling and `prec`/`sgnd`), not a single interleaved RGBA/BGRA buffer like
     libjxl's `JxlPixelFormat`. Decode needs to gather 3-4 separate component planes into
     `Surface`'s interleaved BGRA; encode needs to split BGRA into 3-4 planar `OPJ_INT32` arrays
     (component depth 8-bit, unsigned, no subsampling needed for a straightforward RGB(A) encode —
     don't reach for chroma subsampling, it is lossy-only and not what an image editor wants
     regardless of format).

  Core call sequence once the stream is sorted, for reference: decode is
  `opj_create_decompress(OPJ_CODEC_JP2)` → `opj_setup_decoder` → `opj_read_header` → `opj_decode`
  → `opj_end_decompress` → `opj_image_destroy` + `opj_destroy_codec` + `opj_stream_destroy`.
  Encode is `opj_create_compress(OPJ_CODEC_JP2)` → `opj_setup_encoder` (takes `opj_cparameters_t`,
  a large struct — for lossless set `.irreversible = OPJ_FALSE` and use the 5-3 wavelet, which is
  openjpeg's default; don't hand-tune `tcp_rates`/`tcp_numlayers` beyond what's needed for a
  lossless-or-quality-N knob matching `EncodeOptions`) → `opj_start_compress` → `opj_encode` →
  `opj_end_compress`.

  Header-sniff signature for `MatchesHeader` (`.jp2` container, not raw J2K codestream — match
  what the encoder writes): `00 00 00 0C 6A 50 20 20 0D 0A 87 0A` (the JP2 signature box). Verify
  this by grep against the real header rather than trusting this from memory before wiring it in —
  same "verify, don't guess" bar the JXL work was held to.

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
