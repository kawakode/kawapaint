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
