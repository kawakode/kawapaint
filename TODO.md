# KawaPaint - resume-here notes

Status snapshot: 2026-08-21, branch `master` (updated post-2.4, post-JXL/JP2-Windows-packaging,
post-3.x-classic-PDN-plugin-bridge, post-3.x-BitmapEffect-tier-spike-proven-impossible,
post-bughunt-sweep, post-second-bughunt-pass-B1..B7-all-fixed, post-UI-gaps-pass,
post-demo-recorder).
Full roadmap/rationale lives in Claude memory
(`feature-roadmap-tiers`) and the published plan:
https://claude.ai/code/artifact/b584d126-8639-4875-902d-46a1cb2917c4

## Demo recorder - added 2026-08-21

Doom-style session recording: capture the user's *inputs* and replay them against the same
starting document, rather than storing frames or a video. New `Demo` menu (Record / Play / Pause /
Stop / Playback Speed 0.5×–8×) and a `DemoStatusText` readout in the status bar.

Four new files under `shared/KawaPaint.App/Core/Demo/` - `DemoEvent` (opcodes), `DemoFile`
(the `.kpdemo` container), `DemoRecorder`, `DemoPlayer` - plus `MainView.Demo.cs`, which is the
only part that knows what an editor action *is*. Everything else is taps into existing code.

**What made this cheap:** `SurfaceView`'s pointer handlers already work in image coordinates, and
`CommandRegistry` already made actions ID-addressable. `SurfaceView.OnPointerPressed` was split
into `BeginStroke`/`ExtendStroke`/`EndStroke` so a replay drives the identical code path, with
`StrokeBegan`/`StrokeExtended`/`StrokeEnded` events raised *only* from the pointer handlers so a
replay can't re-record itself. `CommandRegistry.DispatchScope` is a single hook wrapping every
command dispatch (menu, shortcut, dock button) - it logs the id and suppresses the duplicate note
the command's own handler would make.

**Recording taps live in the handlers, not just the registry.** Menu items and toolbar buttons in
this codebase call `OnXxx` directly and never touch `CommandRegistry`, so tapping only the registry
silently missed the Undo *button* while catching Ctrl+Z. ~50 `RecordAction`/`RecordSkipped` calls
now sit in the handlers themselves; the registry's suppression scope stops the double-count.

**Coverage:** strokes, tool switches, fg/bg colour, size/hardness/tolerance/AA/fill-shapes/global-
fill/combine-mode, zoom+pan, every registry command, layer ops (add/delete/duplicate/merge/
reorder/select/visibility/opacity/blend), image ops (flip/rotate/crop/flatten), parameterless
effects, history jump/clear, rulers. Recorded-but-not-replayed (`Skipped`, surfaced in the status
bar during playback): anything whose result is dialog input or external state - adjustments,
Curves, Resize, Canvas Size, Import Layer, plugin effects, the Text tool's string, clipboard
pastes, and all file open/save. Extending to those needs a parameter-source indirection through
each dialog call site; the `Skipped` opcode is the hook for it.

### Verification (both kinds, and the second kind found bugs the first missed)

Headless harness (`scratchpad/demotest`, not committed, `ProjectReference` to the real
`KawaPaint.App`): format round trip, corrupt-file rejection, size, and replay fidelity run through
the **real** `PencilTool`/`PaintbrushTool`/`EraserTool`.

Live: launched the real `win/bin/Debug/net10.0/KawaPaint.Win.exe`, drove it with the PowerShell +
`user32.dll` pattern (menus, colour swatches, four spiral drags, the toolbar Undo button), saved a
`.kpdemo` through the real file picker, replayed it, and pixel-diffed the 800×600 canvas region of
the two screenshots. **Result: 0 of 480,000 pixels differ**, and still 0 when the replay is
harassed with mouse sweeps, a click and a drag across the canvas, and 0 at 8× speed.

Three real bugs came out of that live diff, none visible from reading the code:

1. **Dedupe of repeated pointer samples was unsound.** The recorder skipped a move that landed on
   the already-stored point. `PencilTool.PointerMove` alpha-blends a disc even for a zero-length
   segment, so the dropped duplicate lost a dab and the replay came out lighter - showing up as
   specks clustered at stroke starts, where a real mouse repeats positions most. Now every sample
   is kept; the harness has a regression test asserting the difference is non-zero.
2. **Coordinate precision was too coarse.** 1/16 px let a coordinate near a rounding boundary flip
   a whole pencil pixel (18 differing over a 300-sample drag). Measured 1/16 → 1/4096: 1/4096 is
   byte-exact for all three tools and is also where `DemoEvent.X`'s float storage tops out. Costs
   ~5 bytes/sample.
3. **Live pointer input leaked into a replay's stroke.** `_drawing` is set by the demo's
   `BeginStroke`, so a real mouse move over the canvas got handed to the same in-flight gesture and
   drew a line out to the pointer. `SurfaceView.SuppressPointerInput` now gates all five pointer
   entry points while playback owns the canvas.

### Size

~5 bytes per pointer sample (fixed-point delta + zigzag varint + varint time delta, gzipped, with
an inline string dictionary for repeated tool tags / action ids). Idle time is free - the recorder
only emits on change. Measured: **a 44s four-stroke session with its full starting image embedded
is 2,223 bytes**; 13s of continuous drawing is 3.4 KB; an unbroken 5m42s of it is 92 KB. The
starting document rides along as `.kwp` bytes (~1 KB for the gradient sample image), and is skipped
entirely when the canvas was one uniform colour, which is what `File ▸ New` leaves behind.

### Relation to the snapshot-vs-command-log fork below

A `.kpdemo` *is* a command log, but a replay-from-start one - it does not touch the undo stack's
snapshot design, and it is not evidence for reopening that ruling. It does show the input taps a
Tier 4 command log would need, if that ever gets revisited.

## UI gaps closed 2026-08-20

Three user-reported gaps, all runtime-verified against the real Windows build (launched
`win/bin/Debug/net10.0/KawaPaint.Win.exe`, drove it with the PowerShell + `user32.dll` pattern
described in the working notes, screenshotted each result).

1. **No paintbrush.** The toolbox had a Pencil but nothing soft-edged - Paint.NET's Paintbrush had
   no counterpart here at all. Added `PaintbrushTool` (`Tools.cs`, key `B`, tag `"Brush"`) on top of
   a new `SoftBrushStroke` in `BrushOps.cs`. It is deliberately *not* the pencil with a blurrier
   disc: a soft dab's semi-transparent rim would blend over the previous dab's, so a slow drag would
   darken along the stroke. Instead the whole stroke max-accumulates into one canvas-sized byte
   coverage mask, and each flush re-composites the dirty region from `ToolContext.PreStroke`, which
   makes overlap idempotent and caps the stroke at the brush color's own alpha. Hardness is a new
   `ToolContext.BrushHardness` / `SurfaceView.BrushHardness` (0..1) fed by a `Hardness:` combo in the
   toolbar's new `BrushGroup` (percent; the toolbar talks percent, the engine 0..1). The AA checkbox
   is left disabled for it - the brush is antialiased by construction. Verified: a hardness-15,
   size-42 stroke renders a smooth falloff with no banding or overlap darkening.

2. **No settings menu.** `AppSettings` had readers since tier 0 but no UI, so every default was
   frozen short of hand-editing `settings.json`. New `SettingsDialog.cs` (Preferences, four tabs:
   Autosave / History / Git / Plugins) reached from a new top-level `Settings` menu, which also
   collects the two dialogs that were previously only reachable from elsewhere (Customize Dock,
   Manage Plugins) plus Reset Layout. Only fields something actually reads are exposed -
   `HistorySettings.ShowThumbnails` is deliberately omitted because nothing consumes it yet. Edits
   stage on the controls and write in one `SettingsService.Update` on OK, so Cancel really cancels
   (Save raises `Changed`, which reschedules autosave and can trigger a config commit). Verified by
   changing the autosave interval to 20, confirming `settings.json` on disk, and setting it back.

3. **Unreadable icons in the dropdown menus.** Root cause: `App.axaml` had
   `RequestedThemeVariant="Default"`, which follows the OS - and this box runs
   `AppsUseLightTheme=1`. Every panel in the app paints its own hardcoded dark background, but the
   parts Fluent themes itself (menu dropdowns, combo popups, tooltips) came out near-white, and
   `Icons.Create` strokes every glyph `#DCDCDC`, so menu icons all but vanished. Pinned the variant
   to `Dark` and flipped the `MenuItem MenuItem PART_HeaderPresenter` override from `#1A1A1A` to
   `#F0F0F0` to match (that override exists because Fluent's `MenuFlyoutItemForeground` resolves
   inconsistently once only some items in a popup carry an Icon - still true, still needed).
   Also added the missing `"Plugin"` key to `Icons.cs`: `RebuildPluginToolButtons` asks for it by
   name for every `ToolRegistry` tool button, and `Icons.Create` was falling through to its literal
   `"?"` placeholder.

## Known bugs

- **~~Crash on clicking in the History panel~~ - fixed 2026-08-19.** Root cause: a row click
  (`OnHistorySelected` → `JumpToHistory` → `HistoryStack.JumpTo` → `History.Changed`) called
  `RebuildHistoryPanel()` synchronously, which does `HistoryList.Items.Clear()` - reentering the
  *same* `ListBox`'s own `SelectionChanged` dispatch, still on the same call stack as the click.
  Avalonia's `SelectionModel` is mid-iteration at that point and throws
  `ArgumentOutOfRangeException` deep in its internals (`SelectionModel.OnSelectionRemoved` →
  `SelectedItems.GetEnumerator`). Reproduced live before fixing (temporary debug hook that
  simulated the click, confirmed the exact exception + stack) - not a guess.
  **Same bug also existed in the Layers panel** (row click → `SetActiveLayer` → `DocumentChanged`
  → `RebuildLayerPanel` → `Items.Clear()`, identical shape) - previously undiscovered, found while
  investigating this one, and fixed the same way. Fix: both `Canvas.History.Changed` and
  `Canvas.DocumentChanged` subscriptions in `MainView`'s constructor now
  `Dispatcher.UIThread.Post(...)` the rebuild instead of calling it inline, so it runs after the
  originating click's own dispatch has finished. Verified: re-ran the same repro post-fix (history
  row click on a genuinely populated list, and a layer row click) - both jump/select correctly
  with no exception, and a screenshot confirms the Layers panel visually reflects the new
  selection. Committed as `d787b35`.

### Audit 2026-08-20 - open findings

Full-tree read-through (engine, `SurfaceView`, `HistoryStack`, `MainView`, dialogs, codecs).
**Evidence bar: none of these are runtime-verified.** Every one was traced through the code and
the control flow checked by hand, but nothing below was reproduced live the way the History-panel
crash above was. Treat each as "confirmed by reading, not by running" until it has a repro -
especially before writing a fix that assumes the failure shape. Line numbers are as of this date.

**Audit complete as of 2026-08-20.** All 16 findings resolved one way or another: #1-#5 (High) and
#7-#15 (Medium/Low) fixed and verified; #6 deliberately skipped (user's call - see its entry); #16
retracted after failing to reproduce (see its entry - this is the one place "confirmed by reading,
not by running" turned out to be wrong on the reading side). See each entry for what changed, how it
was verified, and any known gaps left on purpose.

**High - data loss / wrong results**

1. **~~Move tool leaves a phantom offset when dragged back to the start~~ - fixed 2026-08-20.**
   `shared/KawaPaint.App/Tools.cs` - `if (dx == 0 && dy == 0) return;` fired *before* `ShiftInto`,
   so returning the pointer to the origin skipped the restore and the surface kept the last
   non-zero shift; pointer-up then committed a move the user had visibly undone. Fix: the zero-move
   guard now only applies before the first push (skips creating an undo step for a click that never
   moves); once `_pushed` is true, every `PointerMove` re-shifts from `PreStroke`, including at
   (0,0), so dragging back to the start actually restores it. Verified: `dotnet build` on
   `KawaPaint.App` succeeds. Not runtime-verified - no manual drag repro was run against the built
   app in this session.

2. **~~`DocumentFile.Save` destroys the old file before knowing the new one writes~~ - fixed
   2026-08-20.** `File.Create` used to truncate the destination, *then* encode; a failure mid-encode
   left a truncated `.kwp` - including via autosave's `WriteToOriginalFile`, unattended.
   `SaveExploded` was worse: it deleted every existing `layers/*.png` up front, before writing any
   replacement. Fix: `Save(Document,string)` now encodes to a temp file beside the destination and
   `File.Move(overwrite: true)`s it in only on success. `SaveExploded` now encodes every layer to a
   `.tmp` file first; only once *all* of them succeed does it move each onto its real name, delete
   stale layer files from a since-shrunk layer count, and write the manifest last (via the same
   temp+move) - so `LoadExploded`'s trusted pointer never describes a layer set that isn't fully on
   disk. A `finally` sweeps any leftover temp files regardless of where the failure happened.
   Verified: `dotnet build` on `KawaPaint.Engine` succeeds; the existing Sandbox smoke test's
   Save/Load round trip still passes; and a throwaway verification project confirmed all four
   target scenarios - normal `SaveExploded`/`LoadExploded` round trip, stale-file cleanup after a
   layer-count shrink, and (for both `Save` and `SaveExploded`) that disposing a layer's `Surface`
   mid-save to force an `ObjectDisposedException` leaves every on-disk byte and every manifest
   identical to before the attempt, with no leftover `.tmp` files.

3. **~~History never re-trims after undo/redo, so the memory budget silently stops applying~~ -
   fixed 2026-08-20.** `Trim()` used to be called only from `Push`, but `StepBackward`/
   `StepForward` call `Restore()` on spilled mementos - one un-spill per undo, with nothing
   re-spilling them. Undoing back through a long spilled history pulled the whole thing into RAM,
   since `MemoryBudgetBytes` was only ever consulted again on the next new edit. Fix: `Undo()`,
   `Redo()`, and `JumpTo()` now each call `Trim()` once after moving the caret (before firing
   `Changed`) - `JumpTo` only once at the end of its whole walk, not per step, so a long jump stays
   O(n) rather than O(n²). `Trim()` was already written to be a no-op-safe, idempotent pass keyed
   off the *current* `_position`, so re-running it after every caret move was the whole fix - no
   changes to its internals. Verified with a throwaway project: pushed 40 single-tile edits (16 KB
   each) against a 12-tile-worth budget and a real spill directory, confirmed steady-state resident
   bytes settle at the budget, then undid all the way back to position 0 while tracking
   `HistoryStack.ResidentBytes` after every step. With the fix, resident bytes never exceeded the
   budget through the entire walk. **Confirmed the test was meaningful, not vacuous**: temporarily
   disabled the three `Trim()` calls and reran the identical scenario - resident bytes peaked at
   655 KB against a 196 KB budget (3.3×over), the exact failure mode described above, before
   restoring the fix and reconfirming the pass.

4. **~~Structural mementos pin whole surfaces the budget can't see~~ - fixed 2026-08-20.**
   `DelegateMemento` used to report `ApproximateBytes => 0` unconditionally and had no `Dispose`
   override - but Delete Layer captures a live `Layer`, Duplicate a clone, Add Layer a freshly
   allocated (still full-size, even if blank) one, and Merge Down a full `Surface.Clone()` plus the
   removed layer. On a 4000×3000 doc each such step could hold tens of MB invisible to
   `ResidentBytes`, and dropping the step never freed it. Fix: `DelegateMemento` gained two optional
   constructor params, `Func<long>? approximateBytes` and `Action? dispose`, both forwarded
   unchanged through every `Undo()`-produced mirror (they're live queries against current document
   state, not per-instance captured values, so they don't need "swapping" between directions - see
   the doc comment on the constructor for why). The four `MainView.axaml.cs` layer-lifecycle call
   sites (Add/Delete/Duplicate/Merge Down) now pass both: `approximateBytes` reports real bytes only
   while `doc.IndexOf(layer) < 0` (i.e. only while the layer is actually detached and this memento
   is its only owner - while the Document owns it, it's the Document's problem, not history's), and
   `dispose` applies the identical check before freeing anything, since a step can be torn down
   (e.g. `DiscardFrom` invalidating a redo branch) while its object happens to currently be the
   live, document-owned side - disposing then would be a use-after-dispose bug reachable from the
   live canvas. Merge Down's `belowBefore` snapshot is unconditionally owned by the step for its
   whole lifetime (needed for undo in both directions), unlike the removed layer, whose
   attached/detached state still flips per the same `IndexOf` check. Verified with a throwaway
   project covering: (1) the byte report correctly flips between 0 and full bytes as a memento is
   toggled back and forth through `Undo()`; (2) disposing a step whose layer is currently
   *reattached* leaves the document's layer fully intact and readable - the specific double-free
   shape being guarded against; (3) disposing a step whose layer is currently *detached* actually
   frees it; (4) pushing ten detached-layer steps against a tight `MemoryBudgetBytes` (sized above
   `HistoryStack`'s own resident-window floor, so the assertion is achievable by the algorithm) now
   gets trimmed down to budget, with several of the oldest layers' surfaces genuinely freed -
   confirmed via a deliberately-caused crash on access (`Surface`'s indexer doesn't call
   `ThrowIfDisposed`, so a disposed surface's pixel access dereferences a null pointer and surfaces
   as `NullReferenceException`, not `ObjectDisposedException` - a pre-existing, out-of-scope quirk
   noted for whoever next touches `Surface`, not fixed here); and (5) a contrast run using the old
   3-arg `DelegateMemento` shape (no byte/dispose params) on the identical scenario, confirming
   `ResidentBytes` stays exactly zero and nothing is ever reclaimed - proof the `MainView.axaml.cs`
   call-site changes were load-bearing, not cosmetic.

5. **~~Opacity changed by keyboard leaves no undo step and doesn't mark the doc dirty~~ - fixed
   2026-08-20.** `_opacityBefore` used to be set only from a `PointerPressed` handler, and the only
   commit trigger was `PointerReleased` - so an arrow-key nudge on the focused `OpacitySlider` ran
   `OnOpacityChanged`, applied the change, and pushed no history; since `MarkDirty` hangs off
   `History.Changed`, the document kept looking saved. Fix: `OnOpacityChanged` now captures
   `_opacityBefore` lazily (`??=`) on the first change of a gesture regardless of what drove it, so
   both a mouse drag and a keyboard nudge get a correct undo baseline. Commit now fires on any
   gesture-end signal - the existing `PointerReleased` for a drag, plus new `KeyUp` and `LostFocus`
   handlers for keyboard, since arrow keys never raise pointer events at all. `KeyUp` is wired with
   `RoutingStrategies.Bubble, handledEventsToo: true` rather than relying on `OpacitySlider`'s own
   `LostFocus`, because Avalonia's `Slider` handles arrow-key navigation on an internal template
   part and there's no `AutomationId` on it to confirm which element actually holds focus - bubble
   + handledEventsToo catches the key regardless. `CommitOpacityChange()` is idempotent (no-ops once
   `_opacityBefore` is null), so having three trigger paths is safe, not redundant-risky. Verified:
   `dotnet build` on `KawaPaint.App` succeeds. **Not runtime-verified** - same caveat as bug #1: this
   is UI event-wiring logic, not pure engine code, and the app has no `AutomationId`s anywhere in its
   XAML, so a reliable GUI-automation repro would need either fragile blind-Tab-counting or adding
   automation IDs app-wide first, both out of scope for this fix. A manual check (open a document,
   Tab to the Opacity slider, press an arrow key, confirm the title bar gets a `*` and the History
   panel's step count goes up) would close this out properly.

**Medium - visible misbehaviour**

6. **Semi-transparent strokes blotch where discs overlap - scoped, deliberately not fixed
   2026-08-20.** `BrushOps.DrawLine` (`BrushOps.cs:78-92`) stamps discs every `radius*0.5` px and
   each `BlendOver`s the previous, so with alpha < 255 the stroke darkens along its length and
   piles up at polygon vertices and ellipse seams (`ShapeOps.cs:54`, `:196`). Turns out to be two
   differently-sized problems, not one: (a) shape outlines (ellipse/polygon/rounded-rectangle/line)
   pile up at vertices because each edge is a separate `BrushOps.DrawLine` call blending
   independently - fixable by having each shape build one coverage buffer internally and blend
   once, contained to `ShapeOps.cs`/`BrushOps.cs`, low risk; (b) freehand pencil-stroke darkening
   spans many `PointerMove`-triggered calls across a whole drag gesture, which needs a persistent
   per-gesture coverage buffer threaded through `ToolContext` and every tool that blends via
   `BrushOps.DrawLine` (Pencil, Line, Recolor, Clone Stamp, all shape tools - 10+ call sites) - a
   real architecture change with meaningfully more regression surface and, like everything else in
   this codebase, no automated UI test coverage to catch a subtle stroke-rendering regression.
   Asked the user to pick a scope (shapes-only / full fix / skip); **they chose skip**, consistent
   with this project's own precedent of not committing to large uncertain changes without a
   dedicated pass (see the BitmapEffect spike in this file). Revisit as its own task, not bundled
   into a bugfix sweep.

7. **~~Subtract/Intersect against an empty selection is a no-op instead of the documented
   semantics~~ - fixed 2026-08-20.** `IsSelected` treats `!IsActive` as "everything selected", but
   `Combine` used to operate on the raw (physically zeroed, since an inactive selection's mask is
   never materialized) mask directly - so Intersect-dragging with nothing selected yielded empty
   instead of the shape, and Subtract-dragging yielded no change instead of "everything minus the
   shape," both silently ignoring the drag entirely. Fix: `Subtract` and `Intersect` now call
   `SelectAll()` first when `!IsActive`, materializing the "everything selected" mask `IsSelected`
   already promises before combining - a two-line change, `SelectAll()` already existed. **`Add` was
   deliberately left alone**: reading its inactive base as "everything" would make Add-mode useless
   for starting a fresh selection from nothing (union with everything is still everything), so it
   keeps treating the base as the physically-empty mask it actually is, producing exactly the shape
   - this is the existing, useful behavior, not a bug. Verified with a throwaway project: 6 cases
   (Add/Subtract/Intersect × inactive-base and already-active-base) all confirmed correct, including
   3 regression checks proving the already-active-base path is byte-for-byte unchanged. Confirmed
   the test was meaningful: temporarily disabled the two `SelectAll()` calls and reran - the
   Subtract-from-inactive case failed exactly as described (`result is active` false) - before
   restoring the fix and reconfirming all 6 pass.
   
   **Pre-existing wrinkle noticed while fixing this, not fixed here** (out of scope, doesn't get
   worse from this fix): `Combine`'s trailing `IsActive = (any mask byte nonzero)` conflates "no
   selection was ever made" with "a selection was explicitly narrowed to literally nothing" - both
   end up as an all-zero mask with `IsActive=false`, which `IsSelected` then reads as "everything
   editable," the opposite of what subtracting a selection down to nothing should mean. Reachable
   today via a plain active selection fully covered by a Subtract shape, no inactive base needed -
   an existing property of `Combine`, not something this fix introduces.

8. **~~Live-preview dialogs run the full effect synchronously per slider tick~~ - fixed
   2026-08-20.** Each `ValueChanged`/`IsCheckedChanged`/`SelectionChanged`/`ColorChanged` did a
   full-surface `CopyFrom`, a full `Apply`, and a full recomposite inline on the UI thread - dragging
   a slider queued one of those per intermediate value, "seconds of work" on a large image exactly
   as described. Fix: both `AdjustmentDialog` and `PluginEffectDialog` gained a `SchedulePreview()`
   debounce (a 60ms `DispatcherTimer`, restarted on every change) that all four control types now
   call instead of `Preview()` directly; the numeric slider readout still updates immediately
   (cheap, no perf issue) so the UI doesn't feel laggy even though the pixel preview trails by up to
   60ms. `Commit()` now flushes - stops the pending timer and calls `Preview()` synchronously once
   more - before pushing history, so clicking OK immediately after a drag can never commit a stale
   preview from before the debounce fired; without that flush this would have been a real
   regression, not just an optimization. **Deliberately scoped down from the full suggestion**: this
   fixes the number of full recomputations a drag queues (the stated complaint), not the cost of any
   single recomputation - a genuinely expensive effect on a huge image can still cause one visible
   hitch when the debounce fires, rather than continuous accumulating freezes throughout the drag. A
   downscaled or viewport-bounded preview (the other half of the original suggestion) would smooth
   that out too, but most of this engine's effects (blur/warp/radial kernels) read from anywhere in
   the whole image, not just a local neighborhood, so bounding them to a viewport rect would need
   `IEffect.Apply` to accept partial-surface bounds - the same order of architectural change as bug
   #6's skipped option, not attempted here. Verified: `dotnet build` on `KawaPaint.App` succeeds.
   Not runtime-verified - same UI-event-wiring caveat as bugs #1/#5.

9. **~~Autosave blocks the UI thread~~ - fixed 2026-08-20.** `Tick` used to do the whole `.kwp`
   write (zip + N PNG encodes) inline on the `DispatcherTimer` callback, freezing the UI thread for
   as long as a big document took to write, on a timer the user didn't trigger. Fix: `Document`
   gained a `Clone()` method (deep copy - every layer cloned via the existing `Layer.Clone()`, but
   with exact names restored, since `Layer.Clone()`'s " copy" suffix is right for a user-facing
   Duplicate Layer and wrong for a snapshot whose names get written straight into a saved file's
   manifest - caught by the verification test below, not by inspection). `Tick` now does only the
   layer clone (a plain memcpy, fast) synchronously - that's what actually keeps the snapshot
   torn-free against further painting, not an optimization to skip - then backgrounds the slow part
   (path/dir resolution, the actual encode, pruning) via `Task.Run`, resuming on the UI thread
   (Avalonia's dispatcher `SynchronizationContext` handles that for free) to call `MarkAutosaved()`
   and raise `Saved`. Added a `_saving` re-entrancy guard: unlike the old fully-synchronous version,
   where a single UI thread structurally couldn't fire the timer again mid-save, a backgrounded save
   can now genuinely still be running when the next tick lands, so something has to stop two
   encodes from racing on the same recovery folder. **Known accepted gap, not fixed**: `Dispose()`
   only stops the timer; it doesn't cancel an in-flight background save, so closing the app
   mid-autosave can let one extra write complete after the service is nominally disposed. Harmless
   (no corruption, at worst one stray "Autosaved" status message or recovery snapshot during
   shutdown) but real - a full fix would need a `CancellationToken` threaded through `Task.Run` and
   `DocumentFile.Save` (which doesn't currently accept one), judged disproportionate to a rare,
   benign shutdown race. Verified: `dotnet build` on `KawaPaint.App` succeeds, and a throwaway
   project directly exercised `Document.Clone()` - confirmed deep-copy independence in both
   directions (post-clone edits to the original don't appear in the clone and vice versa) plus every
   property (Dpi, order, opacity, blend mode, visibility) carries over, and specifically caught the
   layer-name bug above before it could have silently corrupted every autosave's manifest. The
   `DispatcherTimer`/background-task interaction itself is not runtime-verified - no Avalonia
   dispatcher loop available to drive it outside the real app (no `Avalonia.Headless` package in
   this repo, and standing one up was judged disproportionate to this one timer callback).

10. **~~Lost pointer capture leaves the view stuck mid-stroke~~ - fixed 2026-08-20.**
    `OnPointerReleased` was the only place `_drawing` cleared; Alt-Tab or a capture steal during a
    drag left it true with `_preStroke` still held, so the next press disposed and silently replaced
    `_preStroke`, dropping the in-progress stroke's history with no undo step recorded. Fix: the
    release-handling logic was extracted into a shared `FinishGesture()` (tool finalize + undo
    commit, or just clearing the pan flag) called from both the existing `OnPointerReleased` and a
    new `OnPointerCaptureLost` override - so an involuntary capture loss now wraps up the gesture the
    same way a normal release does, instead of leaving it stuck. Verified: `dotnet build` on
    `KawaPaint.App` succeeds, and the `PointerCaptureLostEventArgs` override signature compiling
    confirms it's a real, correctly-matched Avalonia override rather than a typo'd method that would
    silently never be called. Not runtime-verified - forcing a genuine OS-level capture-loss event
    (actually Alt-Tabbing mid-drag) needs a live app and wasn't attempted.

11. **~~Horizontal scroll zooms out~~ - fixed 2026-08-20.** `e.Delta.Y > 0 ? 1.2 : 1/1.2` treated
    `Delta.Y == 0` (a pure horizontal wheel/trackpad gesture) as zoom-out, since it read "not
    positive" as "negative." Fix: a zero vertical delta now pans horizontally instead (reusing the
    same `_origin` the mouse-drag pan already uses) rather than zooming at all. **The exact pan
    direction/speed (`e.Delta.X * 60`) is a judgment call, not verified against real trackpad
    output** - Avalonia's wheel-delta sign convention varies by platform and natural-scrolling
    settings, and confirming it feels right needs a real trackpad gesture on the built app, which
    wasn't done. The bug itself (incorrect zoom-out) is fixed regardless of whether the pan direction
    ends up feeling backwards; if it does, flipping the sign is a one-character fix. Verified:
    `dotnet build` on `KawaPaint.App` succeeds.

**Low**

12. **~~`Selection.CopyFrom` and `Clip` don't validate dimensions~~ - fixed 2026-08-20.** Neither
    checked that the other selection/surface matched its own `Width`/`Height` before indexing into
    it, unlike `Combine`, which already did - `Clip` in particular walked `Height`/`Width` rows of a
    surface it never size-checked, an unmanaged out-of-bounds write if they ever diverged (a latent
    trap, not a live bug - `Adopt()` keeps everything in sync today). Fix: both now throw
    `ArgumentException` on a mismatch, matching `Combine`'s existing check exactly. Verified:
    `dotnet build` on `KawaPaint.Engine` succeeds; confirmed by inspection that every existing call
    site (`Tools.cs` select/lasso tools' `CopyFrom(_base)`, every dialog's `Selection.Clip(layer.
    Surface, _snapshot)`, `SurfaceView.cs`'s stroke clipping, the Sandbox smoke test) passes
    same-sized objects already, so the new checks are inert for all current callers and only fire if
    a future caller actually gets it wrong - no separate runtime repro needed for a pure
    additive-guard change like this.

13. **~~Static registry events are never unsubscribed~~ - fixed 2026-08-20.** `MainView` subscribed
    to the *static* `EffectRegistry.Changed`/`ToolRegistry.Changed` events via anonymous lambdas -
    harmless with one window (today's only real usage), but the moment a second `MainView` were ever
    created, the first one (and everything it closes over) would stay reachable for the process's
    whole lifetime, not just while it's on screen. Fix: replaced the lambdas with a named
    `OnPluginRegistryChanged` handler and unsubscribed both in a new `Unloaded` handler. Verified:
    `dotnet build` on `KawaPaint.App` succeeds, confirming `Unloaded` is a real Avalonia `UserControl`
    lifecycle event and the handler signature matches. Not runtime-verified - `MainView` is never
    actually unloaded/recreated in this app's current lifecycle (single window, process exit reclaims
    everything), so there's no live repro available for "does it actually fire on unload" without a
    second-window scenario this app doesn't have yet.

14. **~~`ReflectCoord` uses unbounded `while` loops~~ - fixed 2026-08-20.** Replaced the step-by-step
    `while (value < 0) value += max;` / `while (value > max) value -= max;` pair with a closed-form
    period-`2*max` triangle wave (one modulo), so a coordinate arbitrarily far out of range resolves
    in O(1) instead of O(distance) - bounded for today's actual warp parameter ranges, but nothing
    enforced that staying true at every call site forever. Verified via reflection against the real
    `private static` method (not a reimplementation): exact equivalence with the original loop across
    5 different `max` values × the full `[-5·max, 5·max]` range at 0.5-step resolution (zero
    mismatches), the `max<=0` edge case, and - the actual point of the fix - a coordinate around 1e9
    resolved in under a millisecond, where the old loop would have needed on the order of 1e8
    iterations.

15. **~~`DropOldest` decrements `_position` unconditionally~~ - fixed 2026-08-20.** Both call sites
    guarded it, but the `MaxSteps` loop's guard (`_position > 0`) meant the step cap silently stopped
    being enforceable once the user undid all the way back to position 0 with more steps stored than
    the (possibly since-lowered) cap - reachable via an entirely ordinary sequence: push more than
    `MaxSteps` while it's unlimited, undo everything, then lower `MaxSteps` live via settings. Traced
    carefully before fixing: every memento type here (`TileDeltaMemento`, `LayerSurfaceMemento`,
    `DocumentSwapMemento`, `DelegateMemento` post-bug-#4-fix) stores a self-contained *absolute*
    snapshot for its own region - never a diff relative to a neighboring step - so dropping the
    front index is provably safe regardless of whether it's currently the undoable or the
    redoable side; the only real consequence is that specific edit becoming permanently unreachable,
    the same bounded-history trade-off either direction. Fix: `DropOldest` now only decrements
    `_position` `if (_position > 0)` (instead of unconditionally, which would have gone negative once
    called from the redoable side), and `Trim`'s `MaxSteps` loop dropped the `&& _position > 0` guard
    entirely, since `DropOldest` is now safe to call regardless. Verified with a throwaway project
    replicating the exact realistic scenario (10 steps pushed unlimited, undone to position 0,
    `MaxSteps` lowered to 2, then `Redo()`): confirmed `Count` actually reaches 2 (crossing position 0
    mid-drop, dropping 7 steps that were never applied), `Position` never goes negative, and -
    critically - redoing the two surviving steps afterward produces the *exact* predicted pixel
    colors, confirming the "absolute snapshot" safety reasoning empirically rather than just
    analytically. Confirmed the test was meaningful: reran against the original `&& _position > 0`
    guard and reproduced the exact failure (`Count` stuck at 9, only 1 of the needed 8 drops
    happening) before restoring the fix and reconfirming the pass.

16. **`Surface.Resized` linear-filters unpremultiplied alpha - retracted 2026-08-20, could not
    reproduce.** Originally written from reading the code alone (`WrapSKBitmap` tags the bitmap
    `SKAlphaType.Unpremul`, and naive unpremultiplied bilinear filtering is a well-known source of
    dark halos in other imaging libraries) - this session's own stated evidence bar was "confirmed by
    reading, not by running," and running it this time overturned it. Built a throwaway project that
    resized a hard red/transparent edge across 4 different scale ratios (non-integer downscale,
    non-integer upscale, an aggressive downscale, and an odd-sized source) and, separately, tested
    transparent pixels carrying leftover non-zero "garbage" RGB (the more realistic real-world case
    than freshly-zeroed memory) across 2 more ratios. In every single semi-transparent edge pixel
    produced, across all 6 scenarios, the implied fully-opaque color (`R * 255 / A`) came back exactly
    255.0 - the precise signature of correct premultiplied-alpha-aware filtering, with zero halo in
    any case, including zero bleed-through of the garbage green RGB. SkiaSharp's `SKCanvas.DrawImage`
    /`SKImage.FromBitmap` pipeline evidently premultiplies internally for compositing regardless of
    the source bitmap's tagged alpha type. No code change made - there was nothing to fix. This
    doesn't rule out every conceivable Skia version/backend combination misbehaving, but for the
    sampling options this codebase actually uses (`SKFilterMode.Linear, SKMipmapMode.Linear`) on the
    platform this session ran on, the claim as originally written is false. Left the retraction here
    rather than deleting the entry, since erasing it would look like it was silently missed rather
    than actively investigated and disproven.

### Bughunt 2026-08-20 (second pass) - all 7 found and fixed

Deliberately aimed at what the first audit's scope list ("engine, `SurfaceView`, `HistoryStack`,
`MainView`, dialogs, codecs") does **not** name: the plugin system and the Paint.NET classic-tier
bridge (`dfbd2b3`, the newest and least-reviewed code in the tree), plus a re-read of the areas the
first audit's own fixes touched, looking for call sites those fixes missed. Finding B2 is exactly
that - the same defect the first audit's #4 fixed, at two call sites #4 never visited.

**Evidence bar - every finding is runtime-verified with a reverted-fix contrast run, with no
residual gaps.** All seven were reproduced live before fixing and re-run after, and in each case the
fix was temporarily undone to confirm the test actually catches the bug rather than passing
vacuously. B2 was additionally verified by **clicking through the real GUI** (see its entry). Full
solution builds clean, and the Sandbox smoke tests pass (`effects=41`, plugin smoke OK, **and PDN
plugin smoke OK against real paint.net**).

**Two standing assumptions in this file were wrong and got corrected by testing them:**
- "No dispatcher loop available headlessly" (recorded under #9, #13, and B6's first draft) - false.
  `DispatcherTimer` constructs *and* starts with no Avalonia app running. Only the **render
  interface** needs more, and `Avalonia.Headless` + `Avalonia.Skia` supply that in a throwaway
  project. Between them, B6 and B7 were both fully verified. Future UI-adjacent findings should
  probe what actually fails before being filed as untestable.
- B1's severity - see its entry.

**One finding did not survive being run: B1's severity is retracted** (its mechanism stands, its
impact does not). Reading the code said "unbounded leak"; measuring real paint.net 5.1.12 said
"bounded reclamation via finalizers". Same lesson as #16, and the reason that entry was left in
place rather than deleted. **Note the asymmetry worth remembering: B3, sitting right next to it in
the same subsystem and found the same way, was confirmed exactly as written.** "Traced by reading"
is not uniformly unreliable - it's unreliable specifically about *consequences* that depend on
runtime behaviour of code this repo doesn't own.

**paint.net 5.1.12 was installed on this box partway through** (`C:\Program Files\Paint.NET`;
`PdnInstallLocator.Locate(null)` auto-detects it - the well-known probe's lowercase `paint.net`
resolves fine, Windows paths being case-insensitive). Everything that previously had to run against
a fake install was re-run against the real one. **That re-run overturned B1's severity** - see its
entry; the mechanism was real but the consequence was not what reading the code suggested. It also
means **the repo's own `PdnPluginSmokeTest` now actually runs here** rather than skipping:
`$env:KAWAPAINT_PDN_TEST_INSTALL_DIR = "C:\Program Files\Paint.NET"` before the Sandbox, and it
passes.

**Three throwaway verification projects were written for this** (in the session scratchpad, not
committed):
- `realverify/` - drives the real installed paint.net. Measures B3 directly by enumerating
  `AssemblyLoadContext.All` for `"KawaPaint.PdnBridge"` contexts (no instrumentation needed), and
  carries the `Probe` that established, by reflection on the real loaded types, that
  `Surface`/`MemoryBlock`/`RenderArgs` are all finalizable - the fact that forced B1's retraction.
- `pdnverify/` - a **fake paint.net-shaped assembly** (`PaintDotNet.Fake.dll`: `Surface`/
  `MemoryBlock`/`RenderArgs`/`Effect`/`PropertyBasedEffect`/`PropertyCollection` types matching
  exactly what `PdnReflectionSchema` looks up by full name) plus a fake third-party plugin DLL
  deriving from it. This drives the **real, unmodified** `PdnEffectDiscovery.LoadFrom` →
  `PluginEffectDescriptor.Build(...)` → `PdnClassicEffectAdapter.Apply(...)` path end to end. Still
  worth keeping even now that a real install exists: it is the only way to *instrument* the pdn side,
  which is what proves disposal actually happens - the real types can't be made to report that. It
  logs Surface/RenderArgs construction and disposal to a
  **file** rather than a static counter, deliberately: the verification host must not reference
  the fake assembly, or it would land in the default `AssemblyLoadContext` and
  `PdnAssembliesLoadContext.TryResolve` (which tries `Default` first, by design - see its header)
  would serve the bridge a different type identity than `LoadAll()` loaded, and discovery would
  silently find nothing. Same trap applies to the plugin DLL: stage **only** the plugin's own
  `.dll`, with no `.deps.json` and no copy of the pdn assembly beside it, or
  `AssemblyDependencyResolver` resolves a second private copy and breaks type identity the same way.
- `engineverify/` - pure-engine checks for B2/B4/B5 against the real shipped types.
- `uiverify/` - references `KawaPaint.App` and adds `Avalonia.Headless` + `Avalonia.Skia`
  (scratchpad-only; **not** added to the repo). Covers B2's magnitude measurement, B6's
  resurrection check, and B7's `SurfaceView`/`CloneStampTool` behaviour. This is the project that
  disproved the long-standing "UI-adjacent code can't be tested here" assumption - worth recreating
  rather than re-deriving that conclusion next time.

**Originally filed High - "leaks that grow without bound during ordinary use". After measurement:
B2 and B3 hold up as High (114 MB unaccounted and a full assembly-set copy per reload, both
measured). B1's severity does not - retracted to Low, see its entry.**

B1. **~~The PDN classic-tier bridge never disposes the real paint.net objects it builds, once per
    effect apply~~ - fixed 2026-08-20.** `PdnClassicEffectAdapter.Apply` calls
    `PdnSurfaceBridge.Wrap` twice - for the destination surface and for a clone of the source -
    and each `Wrap` (`PdnSurfaceBridge.cs:17-30`) constructs a real `PaintDotNet.Surface` plus a
    real `PaintDotNet.RenderArgs` by reflection. Both types are `IDisposable`: the Surface owns a
    native `MemoryBlock`, and `RenderArgs` lazily creates a GDI+ `Bitmap` **and** `Graphics`
    aliasing it (verified against the actual source on `origin/3.36pdn`: `src/Core/RenderArgs.cs` -
    `public sealed class RenderArgs : IDisposable`, with `Bitmap`/`Graphics` properties built on
    demand from `surface.CreateAliasedBitmap()`). Nothing in
    `shared/KawaPaint.Engine/Plugins/Pdn/` calls `Dispose` on anything - grepped the whole
    directory for `Dispose`/`IDisposable`, zero hits. So every `Apply()` strands two full-canvas
    unmanaged buffers, and any plugin that touches `args.Graphics`/`args.Bitmap` also strands two
    GDI+ handles. `Apply()` is not once-per-effect: `PluginEffectDialog.Preview` rebuilds and
    re-applies on every debounce tick (`PluginEffectDialog.cs:181-189`), so one slider drag is
    ~16 applies/sec. On a 4000×3000 canvas that's ~96 MB of unmanaged memory per tick, invisible
    to the GC except for whatever memory pressure pdn's own `MemoryBlock` reports, and GDI handles
    cap out at 10,000/process.

    **⚠ SEVERITY RETRACTED 2026-08-20, after paint.net 5.1.12 was installed on this box and the
    claim was measured instead of reasoned about. The paragraph above is the finding as originally
    written; the "~96 MB per tick" and "grows without bound" parts of it are FALSE for pdn 5.1.12.**
    Reflecting on the real loaded types shows all three are finalizable as well as `IDisposable`:
    `PaintDotNet.Surface` (Finalize inherited from `RefTrackedObject`), `PaintDotNet.MemoryBlock`
    (declares its own), and `PaintDotNet.RenderArgs` (from its `Disposable` base). The CLR therefore
    reclaims the native memory with no explicit `Dispose` at all, and it keeps up easily: with the
    fix **reverted**, 40 applies on a 2048×2048 canvas - 32 MB of pdn surfaces per apply, 1,280 MB
    allocated in total - peaked at **40 MB** of unmanaged growth, i.e. roughly one apply's worth
    outstanding at a time, sampled every iteration with no forced collection. A separate 60-apply run
    showed the same. So the real behaviour is bounded reclamation via finalizers, not an unbounded
    leak. The GDI-handle half of the claim is subject to the same correction (`RenderArgs` is
    finalizable too) and additionally was never exercised: GaussianBlur doesn't touch
    `args.Graphics`, so no `Bitmap`/`Graphics` is ever created on that path and no case was
    constructed that does. **Correct severity: Low - resource hygiene, not a leak.** This is the
    second time in two passes (see #16) that a read-only inference about consequence has been
    overturned by actually running it; the mechanism was right and the impact was not.

    **The fix is kept anyway, on its merits rather than the retracted severity:** these objects are
    `IDisposable` and were not being disposed, deterministic release beats finalizer-dependent
    release under burst load, and - the real argument - finalizability is an *implementation detail
    of a third-party library this bridge only ever reaches by reflection*, not a contract it can
    lean on. It costs nothing and removes that hidden dependency.

    **Fix:** `Wrap` now returns a `PdnRenderTarget` (new type in
    `PdnSurfaceBridge.cs`) owning both objects, and `Apply` takes both with `using`. Dispose order
    is RenderArgs-then-Surface because the former's Bitmap/Graphics alias the latter's memory, and
    the Surface is freed separately and unconditionally because **RenderArgs explicitly does not own
    it** - checked against the real source rather than assumed (`origin/3.36pdn:src/Core/RenderArgs.cs`
    says so in its own ctor docs, and its `Dispose` frees only the Bitmap and Graphics), so this is
    not a double free. `Wrap` also got a try/catch so a throw between allocating the native surface
    and handing over ownership doesn't leak the very thing this fixes. Uses `as IDisposable` rather
    than a hard cast, degrading to "don't free" if a future paint.net ever stops implementing it.
    **Verified - mechanism, on the instrumented fake harness** (`pdnverify/`): 24 applies built 48
    pdn Surfaces and 48 RenderArgs and disposed **48 and 48**, with **zero** double-dispose events,
    and a single-shot check confirmed pixels were genuinely changed through the bridge (so the
    applies were real work, not a skipped path). **Confirmed meaningful:** reverted to the
    un-`using`'d version and reran - 48 built, **0 disposed** - then restored and reconfirmed. This
    is what proves disposal now happens; it says nothing about how much that matters, which is what
    the retraction above corrects.

    **Verified - no regression, against the real paint.net 5.1.12 install:** the repo's own
    `PdnPluginSmokeTest` now runs for real here (`KAWAPAINT_PDN_TEST_INSTALL_DIR=C:\Program Files\Paint.NET`)
    and passes - a real `GaussianBlurEffect` discovered, registered, driven via its real
    `PropertyCollection`, and rendering a correct blur. That is the check that matters most for this
    change: had the added disposal been wrong (double-free, or freeing something still aliased), it
    would surface as an `ObjectDisposedException` or corrupt output there. A separate `realverify/`
    run also drove 60 consecutive applies of the real blur and confirmed the last one still renders
    correctly - repeated disposal doesn't poison later applies.

B2. **~~The audit's #4 fix missed two layer-lifecycle call sites with the identical shape~~ - fixed
    2026-08-20.** #4
    correctly gave `approximateBytes`/`dispose` to Add/Delete/Duplicate/Merge Down
    (`MainView.axaml.cs:2835-2905`), but **Paste Into New Layer** (`:2705-2707`) and **Import
    Layer** (`:2736-2738`) build the same "undo detaches a whole `Layer`, redo reattaches it"
    `DelegateMemento` with the plain 3-arg constructor - no byte report, no dispose. So both hold
    a full-size layer that `ResidentBytes` reads as 0 and that dropping the step never frees,
    which is precisely the bug #4 exists to fix. Worse than the four that were fixed, in practice:
    a paste is one of the most common ways a large layer enters a document at all. **Fix:** both
    call sites now pass the same `doc.IndexOf(layer) < 0` `approximateBytes`/`dispose` pair as
    `OnAddLayer`, whose reasoning transfers unchanged since both are "undo removes the layer I just
    added." **Verified** via `engineverify/`, which builds the identical memento shape against a
    real `Document`/`Layer` and confirms: 0 bytes reported while the document owns the layer,
    the full 262,144 bytes once undo detaches it, disposing a step whose layer is *attached* leaves
    it intact (the double-free shape #4 guards against), and disposing one whose layer is *detached*
    genuinely frees it. Includes the contrast that proves the two arguments are load-bearing: the
    old 3-arg shape reports 0 bytes in **both** directions.

    **Magnitude measured 2026-08-20 (`uiverify/`), after B1's severity had to be retracted for
    exactly the sin of never measuring it - this one holds up.** Realistic scenario: 10 pastes into
    a 2000×1500 document, then undo all 10, so history is the sole owner of ten full-size layers.
    The plain 3-arg shape those two call sites used reports **0 MB** while **~126 MB is genuinely
    resident** (measured as private bytes minus managed heap, after forced GC + finalizers). The
    fixed shape reports **114 MB**, exactly the ten layers. Both shapes run side by side in one
    process, so no source revert was needed - the contrast *is* which constructor the call site picks.

    **Why B1's retraction does not apply here, tested rather than asserted:** those layers are
    reachable from `HistoryStack._steps`, so they are not garbage and no finalizer can reclaim them
    - the measurement runs a full `GC.Collect` + `WaitForPendingFinalizers` cycle *before* sampling
    and the memory stays. B1's pdn surfaces were unreachable and finalizable, which is precisely why
    that one was bounded and this one is not. **So B2's High rating stands, on evidence.**

    **Gap closed 2026-08-20 - verified end to end in the real GUI, by clicking.** Built and launched
    the real Windows app, put an 800×600 image on the Windows clipboard (sized to fit the default
    800×600 canvas so `ChoosePastePlacementAsync` doesn't interpose its dialog), then drove it with
    user32 P/Invoke per the technique in the 2.1 notes below: **Edit ▸ Paste Into New Layer**, then
    the top-bar **Undo** button. The History panel renders `HistoryStack.ResidentBytes` as
    "N steps · X MB", which makes the fix directly readable on screen:

    - after the paste, `1 step · 0 MB` - **correct**, the step is applied so the *document* owns the
      layer and `approximateBytes` is supposed to report 0;
    - after undo, `1 step · 1.8 MB` - the layer is detached and history is now its sole owner.
      800 × 600 × 4 = 1,920,000 bytes = 1.83 MB, exactly.

    **Confirmed the test was meaningful:** reverted *only* the Paste Into New Layer call site back to
    the 3-arg constructor, rebuilt, relaunched and repeated the identical click sequence - same
    layers, same greyed-out redoable step, but the panel read **`1 step · 0 MB`** after the undo.
    Restored and reconfirmed. Screenshots retained in the scratchpad. **Watch out when repeating
    this:** `SendKeys` with the `Ctrl+Shift+V` accelerator did nothing (Avalonia didn't have
    keyboard focus) - clicking the menu worked first time and is the better route anyway, and the
    app must be `ShowWindow(SW_MAXIMIZE)` + `SetForegroundWindow`'d first or `CopyFromScreen`
    captures whatever is occluding it rather than the app.

B3. **~~Every plugin reload permanently loads a second copy of the entire paint.net assembly set~~ -
    fixed 2026-08-20.** `PdnEffectDiscovery.LoadFrom` used to build a fresh `new PdnAssembliesLoadContext(...)` per call
    (`PdnEffectDiscovery.cs:37`), and that context is `isCollectible: false` **by design**
    (`PdnAssembliesLoadContext.cs:28`, and the file header argues for it) - the design assumed one
    construction per process. But `AppPdnPluginHost.Reload` calls straight back into `LoadFrom`,
    and the Plugin Manager dialog reaches `Reload` from three separate places: the "Reload Plugins"
    button (`PluginManagerDialog.cs:67`), "Browse…", and "Auto-detect" (both via
    `SetPdnInstallOverride`, `:114`). Each click strands the previous context and every
    `PaintDotNet.*.dll` in it - tens of MB, unreclaimable for the process lifetime. Note the
    per-plugin `PdnPluginLoadContext` *is* collectible and does come free after
    `EffectRegistry.Clear()` drops the descriptors rooting it; it's only the shared one that
    accumulates. **Fix:** a static `_bridgeCache` keyed by install directory (ordinal-ignore-case)
    holds the context + schema and reuses them across reloads - keyed rather than a single field so
    that genuinely repointing at a different install still builds a new context instead of silently
    serving types from the old one, and populated only on **full** success so a half-built bridge
    (assemblies loaded, schema lookup failed) is never served to the next reload as if usable.
    Makes reload much faster as a side effect.

    **Verified live against the REAL paint.net 5.1.12 install** (`realverify/`), not just the fake
    harness - and this one needed no instrumentation at all, because `AssemblyLoadContext.All` can
    simply be enumerated for contexts named `"KawaPaint.PdnBridge"`, which is precisely the thing
    that used to accumulate. Loading paint.net's own `PaintDotNet.Effects.Legacy.dll` as a
    stand-in third-party plugin discovered **32 real effects**; the first discovery created exactly
    one bridge context, and 5 further reloads created **none**. **Confirmed the test was meaningful:**
    disabled the cache lookup and reran against the same real install - the context count went to
    **6** (1 + 5), the exact described failure - then restored and reconfirmed. The fake harness
    (`pdnverify/`) shows the same 1-vs-6 result via module-initializer counting. Unlike B1, this
    finding's severity survived contact with the real install unchanged: every reload really did
    strand a full, unreclaimable copy of paint.net's 25-assembly set.

**Medium - visible misbehaviour**

B4. **~~A degenerate lasso in Replace mode produces an "active but empty" selection, after which
    every edit silently does nothing~~ - fixed 2026-08-20.** `Selection.ReplaceWithPolygon`
    used to end with an unconditional `IsActive = true`, even when the scanline fill set no pixels at all
    (a sliver whose per-row `left > right` after rounding writes nothing).
    `ReplaceWithRectangle` does not have this problem - it computes
    `IsActive = right > left && bottom > top` (`:138`) - so this is an inconsistency between
    sibling methods, not a deliberate convention. Normally `Combine`'s trailing
    "`IsActive` = any mask byte nonzero" recompute (`:121-122`) would clean it up, but the
    `Replace` case returns early through `CopyFrom(shape)` (`:95-97`) and never reaches it - and
    Replace is the default mode. The result: `IsActive` true over an all-zero mask, so
    `Selection.Clip` restores *every* pixel from the pre-stroke snapshot
    (`SurfaceView.ClipToSelection` → `Selection.cs:219`) and every subsequent brush stroke is
    undone as it's drawn, while `DrawMarchingAnts` renders nothing, leaving no visual cue as to
    why. Reachable from a quick 3+ point flick of the lasso spanning under a pixel - the
    `_points.Count < 3` guard in `LassoSelectTool.PointerUp` (`Tools.cs:342`) doesn't catch it.
    This is a *different* defect from the "narrowed to literally nothing" wrinkle noted under #7:
    that one is about `Combine` conflating empty with inactive; this one is about `Replace`
    bypassing that recompute entirely and asserting active over an empty mask.

    **Fix:** both `ReplaceWithPolygon` and `ReplaceWithEllipse` now track whether the rasterizer
    actually wrote a pixel and set `IsActive` from that, matching what `ReplaceWithRectangle`
    already did. `ReplaceWithEllipse` was included even though it's much harder to trigger there
    (its `rx < 0.5 || ry < 0.5` guard and the tool's own zero-size check catch most cases) - leaving
    one of two sibling methods with the broken convention is how this bug survived in the first
    place. **Verified live** via `engineverify/`, including the user-visible consequence rather than
    just the flag: a sub-pixel 3-point lasso and a wholly off-canvas polygon both leave the
    selection inactive; a subsequent brush stroke through the real `Clip` path **survives**
    (alpha=255). Regression checks confirm a real triangle lasso (617 px) and a real ellipse
    (1291 px) still select exactly as before. **Confirmed the test was meaningful:** restored the
    unconditional `IsActive = true` and reran - all four degenerate cases flipped to active, and the
    brush stroke came back **alpha=0**, i.e. silently reverted, precisely the described failure -
    then restored the fix and reconfirmed.

**Low / latent**

B5. **~~`HistoryStack.TruncateFrom` can truncate at the wrong index, because the walk it performs
    first can renumber the list under it~~ - fixed 2026-08-20.** `TruncateFrom(index)` used to call
    `JumpTo(index)` and then `DiscardFrom(index)` with the *same* `index`. But `JumpTo` ends in
    `Trim()` (`:504`), and `Trim` can call `DropOldest()` (`:616`), which removes from the **front**
    of `_steps` - shifting every surviving index down. `DiscardFrom` then cuts at a stale index:
    either destroying steps the user meant to keep, or (if the list shrank past `index`) silently
    doing nothing. The trigger is real rather than theoretical: jumping backward un-spills each
    step it crosses, and post-#4 a `DelegateMemento` for Add Layer flips its byte report from 0 to
    full-surface the moment undo detaches the layer, so a backward jump genuinely can push
    `ResidentBytes` over budget and into the drop loop. **Latent today**, and that's the only
    reason this is filed Low: `TruncateHistoryFrom` (`SurfaceView.cs:210`) has no caller anywhere -
    grepped `shared/` across both `.cs` and `.axaml` - so the History panel doesn't expose step
    deletion yet. It was a live trap for whoever wires that button up. **Fix:** `HistoryStack` gained
    a monotonic `_dropCount` (incremented in `DropOldest`), and `TruncateFrom` rebases `index` by the
    delta across its `JumpTo` call. Identity tracking was considered and rejected: `StepBackward`/
    `StepForward` *replace* each step with its own inverse, and the walk crosses the target index, so
    the object at that slot is not the one the caller pointed at. A negative rebased index means the
    targeted step was itself dropped - everything still in the list is then "after" it, so it clamps
    to 0 and truncates the lot, which is the correct reading of "drop this step and everything after."
    **Verified live** via `engineverify/`, using audit #15's own reachable setup (10 steps pushed
    unlimited, `MaxSteps` lowered to 3 afterwards, then truncate): truncating at index 8 leaves
    `Count=1` with the surviving step correctly identified as `step7` and `Position` still valid;
    truncating at index 2 - a step the trim had already dropped - correctly clears the list.
    **Confirmed the test was meaningful:** neutralised the rebase and reran - `Count=3` (nothing
    truncated at all) and `Count=2` for the second case, the exact described failure - then restored
    and reconfirmed.

B6. **~~`AutosaveService.Dispose()` leaves the service subscribed to settings changes, so a disposed
    autosaver resurrects its own timer~~ - fixed 2026-08-20.** The constructor subscribed with an anonymous lambda
    (`AutosaveService.cs:34`) and `Dispose()` only stops the timer (`:128-132`). Any later
    `SettingsService.Save()` therefore calls `Reschedule()` on the dead service, which builds and
    starts a *new* `DispatcherTimer` - and it keeps autosaving forever. Structurally identical to
    audit finding #13 (static registry events), including the mitigating circumstance: `_autosave`
    is currently never disposed at all (`MainView.axaml.cs:176`; no `Dispose` call anywhere), and
    `SettingsService.Instance` is a process-lifetime singleton, so nothing reaches the bad state
    today. **Fix:** the same named-handler + unsubscribe shape #13 used, plus a `_disposed` flag that
    makes `Reschedule()` refuse to re-arm regardless of who calls it - belt and braces, since the
    resurrection path runs through a public method. `Dispose`'s doc comment now also records the
    pre-existing accepted gap unchanged from #9 (an in-flight background save is still not
    cancelled).

    **Runtime-verified 2026-08-20 (`uiverify/`) - and the standing "we can't test this headlessly"
    assumption turned out to be wrong.** #9, #13 and the first draft of this entry all recorded "no
    dispatcher loop available outside the real app" as the reason to stop. Probing it instead of
    assuming: `Dispatcher.UIThread`, `new DispatcherTimer()` **and** `DispatcherTimer.Start()` all
    work fine with no Avalonia application running at all. Nothing needs to *tick* to test this -
    the question is only whether a timer gets armed, which is observable directly. Confirmed: a live
    service arms a timer; `Dispose()` clears it; a subsequent `settings.Save()` leaves it **null**;
    an explicit `Reschedule()` on the disposed service also leaves it null; and a still-live service
    continues to reschedule normally (interval correctly follows a settings change to 7 minutes -
    proving the `_disposed` guard didn't just break the feature). **Confirmed the test was
    meaningful:** restored the lambda subscription and removed the guard, then reran - the disposed
    service **re-armed its timer** on the settings change *and* on the direct call, exactly the
    described resurrection - before restoring and reconfirming. **Worth remembering for next time:
    "needs a dispatcher" is not the same as "needs a running app."** Only the render interface
    genuinely requires more (see B7).

B7. **~~Clone Stamp's source point survives into documents where it means nothing~~ - fixed
    2026-08-20.** `CloneStampTool._source` lives on the tool instance, which is a single
    long-lived object reused across every document, and nothing clears it on document open, crop,
    resize or rotate. Painting afterwards computes an offset against coordinates that no longer
    refer to anything. Not a memory-safety issue - `BrushOps.CloneDisc` bounds-checks its sample
    against `src.Width`/`Height` before reading (`BrushOps.cs:132`), so it degrades to painting
    nothing rather than reading out of bounds - but the tool silently does nothing with no
    indication that the source needs re-setting. **Fix:** `SurfaceView` now carries a
    `_documentVersion` bumped in `Adopt` (so it changes on document open *and* on every
    crop/resize/rotate/flatten, since all of those route through it), surfaced on `ToolContext` as
    `DocumentVersion`; `CloneStampTool` records the version alongside its source and drops the source
    when it no longer matches. Deliberately conservative in one respect: undoing a crop also bumps
    the version, so the source clears even though the user is back at "the same" canvas - correct
    rather than merely cautious, because `DocumentSwapMemento` really does swap in a different
    `Document` instance.

    **Runtime-verified 2026-08-20 (`uiverify/`).** `SurfaceView.Adopt` builds a `WriteableBitmap`,
    which is the one thing here that genuinely needs a platform render interface - supplied by
    adding `Avalonia.Headless` + `Avalonia.Skia` (`UseHeadless(UseHeadlessDrawing: false)`) as a
    **scratchpad-only** dependency; nothing was added to the repo. Confirmed against a real
    `SurfaceView`: `DocumentVersion` changes on `SetDocument` (1→2) and on `ReplaceDocument` (2→3,
    the path crop/resize/rotate/flatten all route through). The tool half needs no UI at all - a
    `ToolContext` is plain data - so `CloneStampTool` was driven directly: a source set in the
    current document still paints; a source carried over from a previous document is **refused**;
    and re-setting the source in the new document restores normal cloning, which guards against the
    fix silently bricking the tool after any crop. **Confirmed the test was meaningful:** removed the
    staleness check and reran - the stale source was used instead of dropped - then restored and
    reconfirmed.

## Optimizations

From the same 2026-08-20 read-through, ordered by expected felt impact. Same evidence bar as the
bug list - these are reasoned from the code, not profiled. **Profile before committing to any of
the big ones**; the ordering below is a hypothesis about where the time goes, not a measurement.

1. **Dirty-rect compositing.** Every brush move calls `Composite()` → `RenderComposite`
   (`SurfaceView.cs:141`) → full `Document.RenderTo` over every layer → `RefreshBitmap` (`:148`)
   copying the whole composite. On a 4000×3000 doc with 5 layers that's 60M blends plus a 48 MB
   memcpy *per mouse-move event*. Track the changed rect from the tool and composite/upload only
   that. Single biggest win in the app.

2. **`Blending.Composite` fast paths.** `Blending.cs` runs three `BlendChannel` switch dispatches
   plus double-precision math per pixel per layer. Add: Normal + opacity 255 → integer `BlendOver`;
   first visible layer over a just-cleared dest → straight copy; hoist the mode switch out of the
   pixel loop into per-mode specialized loops.

3. **Checkerboard as a tile brush.** `DrawCheckerboard` (`SurfaceView.cs:326`) emits one
   `FillRectangle` per 8px screen cell - ~22,000 draw ops per frame at 1600×900, repainting on
   every pointer move because the brush cursor calls `InvalidateVisual()`. One `ImageBrush` with
   `TileMode.Tile` over a 16×16 bitmap replaces all of it.

4. **Cache marching-ants boundaries.** `DrawMarchingAnts` (`SurfaceView.cs:291`) recomputes the
   boundary set and emits one rect per boundary pixel, 8× a second indefinitely. Compute the
   boundary once per selection change into a `StreamGeometry`; the animation only needs the phase.

5. **Cache layer thumbnails.** `MakeThumbnail` (`MainView.axaml.cs:2340`) does a full-surface Skia
   resample per layer, and `RebuildLayerPanel` (`:2265`) runs on *every* `DocumentChanged` -
   including clicking a layer row, undo, redo and opacity changes. Key a cached thumbnail off a
   per-layer version counter bumped only on pixel writes.

6. **`RebuildHistoryPanel` is O(steps × tiles) per edit.** `MainView.axaml.cs:2383` calls
   `history.ResidentBytes`, which walks every step's every tile, and recreates all N
   `ListBoxItem`s. Maintain the resident total incrementally in `HistoryStack` (the `Trim` comment
   already recognises the cost, it just doesn't cache across calls), and append/mark rows instead
   of clearing.

7. **Shape tools full-copy the surface per mouse move.** `ShapeToolBase` (`Tools.cs:167`) -
   `CopyFrom(c.PreStroke)` is a whole-surface memcpy to discard the previous preview; restore only
   the previous shape's bounding rect. `FreeformShapeTool` and `LassoSelectTool` are worse: they
   re-rasterize a monotonically growing point list every move, so a long drag is O(n²).

8. **`SurfaceOps.ShiftInto` is per-pixel with a bounds test** (`SurfaceOps.cs:45`) and sits on the
   Move tool's per-mouse-move path - clip the row span once, then one `NativeMemory.Copy` per row.
   Same file, `Rotate90` (`:29`) uses the bounds-checked indexer with a cache-hostile
   stride-jumping write; a 32×32 tiled transpose is typically 3-5×.

9. **LUT base class for per-pixel effects.** `PerPixelEffect.Apply` (`Effects.cs:25`) makes a
   virtual call per pixel. Invert, Grayscale, BrightnessContrast, Curves and Posterize are all
   expressible as a 256-entry byte table - a `LutEffect` base collapses them to a table lookup with
   no dispatch. (Sepia is cross-channel; leave it.)

10. **Trig out of the radial-blur inner loop.** `Effects.Blur.cs:97` calls `Math.Cos`/`Math.Sin`
    per sample per pixel - at default quality ~1B transcendentals on a 12 MP image. The samples are
    evenly spaced angles, so rotate a vector by a precomputed fixed delta instead. Relatedly,
    `BilinearAt` (`Surface.cs:187`) does four `double` lerps per channel; 16.16 fixed-point is a
    broad win across every warp and blur.

11. **`Surface.Clear` is a scalar per-pixel loop** (`Surface.cs:69`). `NativeMemory.Fill` for the
    transparent case - which is what `Document.RenderTo` calls every composite - or a
    `Span<uint>.Fill` otherwise.

12. **FloodFill recomputes row pointers inside the inner loop.** `FloodFill.cs:42,48` call
    `GetRowPointer(y±1)` per pixel, twice - hoist them. `visited` as a bitset instead of
    `bool[w*h]` also cuts an 8 MP fill from 8 MB to 1 MB and helps cache.

13. **`Selection.GetBounds` and `Clip` scan the full mask** (`Selection.cs:188`, `:208`). Cache
    bounds (invalidated on mutation) and let `Clip` restore row runs within them rather than
    branching per pixel over the whole image.

14. **Parallelize the PNG encodes in `DocumentFile.Save`** (`DocumentFile.cs:58`, currently
    serial). Encode into memory buffers in parallel, write sequentially. Pairs naturally with
    moving autosave off the UI thread (bug #9).

15. **Drop the clipboard PNG round-trip.** `FromClipboardBitmap` (`MainView.axaml.cs:2526`) encodes
    an Avalonia bitmap to PNG then decodes it through `CodecRegistry`. The comment justifies this by
    header-sniffing "any format Skia can read" - but the preceding
    `bitmap.Save(...PngBitmapEncoderOptions)` has already normalized it to PNG, so the sniff always
    says PNG. Use `CopyPixels`
    straight into a `Surface`; same for `ToClipboardBitmap` in the other direction. **Fix the
    comment too - its stated rationale is wrong, not just redundant.**

**Checked and found correct** (don't re-audit these without new evidence): the
`DocumentSwapMemento` ownership dance around crop/rotate/flatten - the interleaved
stroke→crop→stroke→discard path was traced looking for a double-free and the ordering holds.

**Not a finding but worth recording:** there is no test project in `KawaPaint.slnx`, so none of the
above has a regression net. The engine half - `Selection.Combine`, `HistoryStack.Trim`,
`ColorBgra.BlendOver`, `FloodFill` - is pure and cheap to cover, and several of these bugs are
exactly the shape a unit test pins down.

## Done

**Tier 0 (foundations):** `shared/KawaPaint.App/Core/` - `SettingsService`/`AppSettings`
(typed, versioned, JSON), `PanelManager`/`WorkspaceLayout`/`PanelDescriptor` (registry-based
docking, resize, float), `CommandRegistry`/`AppCommand` (id-addressable actions, primary +
`AlternateGesture`), `DockEntry`. `shared/KawaPaint.Engine/Codecs/` - `CodecRegistry` +
PNG/JPEG/WebP/BMP/GIF/ICO with runtime availability probing. `DocumentSession` (path/dirty/edit
count).

**Tier 1 (core), all done:** autosave + crash recovery, resizable floating panels, saveable
layout presets, History panel (`HistoryStack` is an indexed list with `TileDeltaMemento` - tile
deltas not full clones, disk spill, `TruncateFrom` - see the git ruling below for why truncate-only),
clipboard (Cut/Copy/Copy Merged/Paste ×3), selection combine modes (Add/Subtract/Intersect,
live-preview while dragging), Magic Wand, Fill/Erase Selection, Canvas Size (anchor-based, distinct
from scale-Resize), recent files (MRU 10, desktop-only), per-format save options (JPEG quality /
WebP lossless), import layer from file, rulers + units (`RulerMath` in Engine is pure/testable,
`RulerBar` control, `Document.Dpi` threaded through everything incl. `.kwp`).

Deliberately skipped, not forgotten: antialiased selection edges (mask is binary; would need a
coverage-based rasterizer rewrite + graded `Selection.Clip` blending - real work, flag if wanted),
Layer Properties dialog (redundant, panel already has name/opacity/blend/visibility inline),
dedicated Zoom/Pan tool buttons (already covered by Ctrl+wheel/keys and middle/right-drag pan).

**Tier 2, started:** 2.1 effect catalogue + bundled tools done - 30 effects plus Clone Stamp/
Recolor/Rounded Rectangle/Freeform Shape, all three passes 2026-08-19 - see its own section below
for the full account, including a real infinite-loop bug found and fixed during verification. 2.2
custom dock also done - see `MainView`'s "Dock" panel, `DockEditorDialog`, `Core/DockEntry.cs`.
Hidden by default, `Ctrl+\`` or top-right icon summons it Floating.

Two real bugs found+fixed while building the dock (not just features - read if something about
panel defaults or redo-binding looks wrong later):
1. `WorkspaceLayout.For` used to seed `LastShown = DefaultPlace`, so a Hidden-default panel's
   first toggle-visible was a silent no-op forever. Fixed: Hidden now falls back to Floating.
2. Redo was two separate `AppCommand` registrations (Ctrl+Shift+Z, Ctrl+Y) - visible as a
   duplicate in the dock picker. Added `AppCommand.AlternateGesture` instead.

## Not started - pick one, each is its own multi-hour subsystem

### 2.4 forge integrations
**Explicitly out of scope for this pass.** Local git history is done (2.3); forge half (GitHub/GitLab/Gitea 
OAuth or PAT, token storage per-platform, create-repo/clone-url flows) needs its own design pass before 
code. See 2.3 notes in the Done section for the rough sketch.

### 3.x Paint.NET plugin compatibility
See below in Spikes.

### 2.1 - Effect catalogue
Port from paint.net 3.36's `src/Effects/` (MIT-licensed). The original source is preserved on the
`origin/3.36pdn` branch - verified it's actually there:
`git ls-tree -r origin/3.36pdn --name-only | grep Effects/` lists ~30 files. **Gotcha: don't pull
files with `git show origin/3.36pdn:path`** - some of these files are UTF-16-encoded blobs and
`git show` (unlike `git cat-file -p`) silently applies EOL conversion that corrupts the UTF-16
byte alignment, producing garbage. Use `git cat-file -p $(git rev-parse origin/3.36pdn:path)`
instead, which returns the raw blob untouched; the Read tool then auto-detects UTF-8 vs UTF-16
correctly. (The `src/` directory in the current working tree is unrelated leftover, not this -
don't confuse the two.)

**10 effects done 2026-08-19** (Distort: Bulge, Twist, Polar Inversion, Tile, Frosted Glass,
Pixelate; Stylize: Median, Outline, Relief, Vignette) - algorithms transcribed from the real pdn
source (not reinvented), wired into new `Effects > Distort` / `Effects > Stylize` submenus in
`MainView.axaml` via the existing `AdjustmentDialog`/`OnAdjust` live-preview pattern, same as
Brightness/Contrast etc. Not a literal file port: pdn's classes are built on
WinForms/IndirectUI/PropertySystem plumbing that doesn't exist here, so each effect's core
`OnRender`/`InverseTransform` math was ported onto KawaPaint's own `IEffect` shape instead.

New shared engine infrastructure, reusable for the effects still remaining below:
- `Surface.GetBilinearSampleClamped`/`GetBilinearSampleWrapped` (`Surface.cs`) - KawaPaint had no
  bilinear sampling at all before this; pdn's warp-style effects all depend on it.
- `WarpEffect` abstract base (`Effects.Distort.cs`) - mirrors pdn's `WarpEffectBase`: given a
  destination pixel's position relative to image center, subclasses return the center-relative
  source position to sample (inverse mapping), clamped or wrapped per `EdgeMode`. Bulge/Twist/
  Polar Inversion/Tile all just implement `InverseTransform`.
- `LocalHistogramEffect` abstract base (`Effects.Stylize.cs`) - mirrors pdn's
  `LocalHistogramEffect`: builds a per-channel 256-bin histogram over a circular neighborhood
  around each pixel, subclasses turn that into an output color. Median/Outline both use it.

Deliberate simplifications vs. the pdn originals (flag if any turn out to matter):
- No anti-aliased supersampling (pdn's `Utility.GetRgssOffsets`) - single-sample bilinear only,
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
  blend - simpler and arguably more correct for a "pixelate" effect.
- `OutlineEffect`'s alpha-channel bound-scan uses `ha` throughout; the original pdn source
  actually reads `hb` (blue histogram) for that one loop's leading-zero skip - a real upstream
  bug, invisible on typical fully-opaque images since the alpha histogram is a single spike.
  Ported correctly here rather than faithfully copying the bug.

**Verified for real, not just "it compiles":** all 10 added to `KawaPaint.Sandbox/Program.cs`'s
smoke-test `effects[]` array (runs the full apply→clip→history pipeline through real
`KawaPaint.Engine` types) - passes. A separate scratch harness applied each effect to a 64×64
test-pattern surface, confirmed every one actually changes pixels (not a silent no-op), eyeballed
the PNG output for each (bulge visibly bulges, twist/polar-inversion produce coherent swirl/
kaleidoscope patterns, median visibly removes the thin diagonal test line while preserving block
edges, outline highlights exactly the color-block boundaries, vignette darkens the corners -
all correct), and ran all 10 against deliberately degenerate sizes (1×1, 2×2, 3×7, 1×40) to catch
divide-by-zero/out-of-range crashes from `maxRadius==0`, wrap-modulo on a 1px-wide image, etc. -
no crashes. Beyond the headless checks, also built and launched the real Windows desktop app
(`win/KawaPaint.Win.csproj`) and drove it live: confirmed the `Distort`/`Stylize` submenus list
all 10 items, opened the Bulge dialog and dragged its slider - the live preview visibly bulged the
test image in real time - clicked OK and confirmed it committed (status bar showed "Bulge", title
bar picked up the unsaved-changes `*`, no crash), and opened the Vignette dialog to confirm its
sliders default to pdn's own defaults (Amount 1.00, Radius 0.50).
**Windows UI-automation gotcha for next time:** this box has no input-automation tool preinstalled
either (same as the Linux note below), but plain PowerShell + `user32.dll` P/Invoke
(`SetCursorPos`/`mouse_event` for clicks, `GetWindowRect`/`SetForegroundWindow` for the target
window, `System.Drawing.Graphics.CopyFromScreen` for screenshots) works fine for driving a real
Avalonia window and needs no extra install. **Also hit and worth remembering:** `dotnet build` on
this repo's `win/` project writes to *two* separate output dirs -
`win/bin/Debug/net10.0/KawaPaint.Win.exe` (plain build) and a stale
`win/bin/Debug/net10.0/win-x64/KawaPaint.Win.exe` left over from an earlier RID-specific
build/publish - only the former gets refreshed by a plain `dotnet build`. Launching the `win-x64`
one silently ran yesterday's binary with none of today's changes; always check both paths'
timestamps (or delete the stale one) rather than assuming the first exe `find` turns up is current.

**Remaining 17 effects done 2026-08-19 (second pass, same day)** - Blurs: Motion Blur, Radial
Blur, Zoom Blur, Surface Blur, Unfocus, Fragment; Distort: Dents; Stylize: Reduce Noise; Render:
Clouds, Julia Fractal, Mandelbrot Fractal; Photo: Glow, Red Eye Removal, Soften Portrait; Artistic:
Ink Sketch, Pencil Sketch, Oil Painting. This closes out the full pdn effect list from the original
plan - **2.1 effect catalogue is done** (30 effects total across both passes), modulo the
"Tools that came bundled with these in pdn, not effects" item below, which was never in scope as
an effect.

New files: `PerlinNoise2D.cs` (shared by Dents and Clouds - ported from pdn's own
`PerlinNoise2D.cs`), `Effects.Blur.cs`, `Effects.Noise.cs`, `Effects.Render.cs`, `Effects.Photo.cs`
(also defines internal `BlendOps` - Screen/Overlay/Darken/ColorDodge, standard two-layer blend-mode
formulas used in place of pdn's alpha-compositing-aware `UserBlendOps`, since here one side of
every blend is always effectively opaque), `Effects.Artistic.cs`. `WarpEffect` (`Effects.Distort.cs`)
gained a third `WarpEdgeMode.Reflect` for Dents (pdn's own choice for it - avoids the smeared look
Clamp gives a noise-driven ripple at the image edge).

Compositional reuse, matching how pdn itself builds these on top of each other: `GlowEffect` is
blur+brightness/contrast+Screen-blend, and `InkSketchEffect` calls `GlowEffect` directly for its
background pass, exactly like the pdn original does. `PencilSketchEffect`/`SoftenPortraitEffect`
similarly compose the existing `BoxBlurEffect`/`BrightnessContrastEffect`/`InvertEffect`/
`GrayscaleEffect` rather than duplicating blur/adjust logic.

**A real bug was found and fixed during verification, not just during review:** `WarpEffect`'s new
`ReflectCoord` helper (`value += max` / `value -= max` to bounce a coordinate back into range)
infinite-loops whenever `max <= 0` - i.e. whenever the image is exactly 1px wide or 1px tall, since
adding/subtracting zero never converges. This is invisible on any normal canvas size and only
`DentsEffect` uses Reflect mode, which is exactly why the project's boundary-size test battery
(1×1, 1×40, 40×1, ...) exists - a plain "does it look right" check on a 64×64 test image would
never have caught it. Root-caused by timing each effect against each boundary size individually
after the full batch run hung with zero output (confirmed the hang was real CPU-bound spinning, not
a slow build, via `Get-Process`'s CPU-seconds counter climbing continuously) rather than assuming
a cause. Fixed with a one-line early return (`if (max <= 0) return 0`); re-ran the full boundary
battery clean afterward.

**Other simplifications vs. pdn, same "flag if it turns out to matter" spirit as the first pass:**
- `MotionBlurEffect`/`RadialBlurEffect`/`ZoomBlurEffect` drop pdn's fixed-point rotation-matrix
  math (a perf trick from 2007-era hardware) for plain `Math.Cos`/`Math.Sin` per sample - same
  visual result, much simpler to read and verify.
- `UnfocusEffect` is a genuinely circular-kernel *unweighted* mean (reusing `LocalHistogramEffect`),
  not pdn's alpha-weighted premultiplied version - matches this codebase's existing
  non-alpha-weighted convention (`BoxBlurEffect` already averages B/G/R/A independently, not
  alpha-weighted either), and is the real reason to have Unfocus at all alongside the existing
  square/separable Gaussian Blur menu entry: a genuinely round kernel, visible at hard silhouette
  edges.
- `JuliaFractalEffect`/`MandelbrotFractalEffect` drop pdn's quality-supersampling loop (single
  sample per pixel, consistent with dropping AA everywhere else in this catalogue).
  `MandelbrotFractalEffect`'s `InvertColors` checkbox was dropped from the dialog entirely (always
  false) - `AdjustmentDialog` only has sliders, no checkbox control; flag if worth adding.
- The `BlendOps` formulas (Screen/Overlay/Darken/ColorDodge) are the standard documented two-layer
  blend-mode math, not a transcription of pdn's `UserBlendOps.Generated.cs` - that file is
  macro-generated fixed-point code whose complexity is almost entirely about correct alpha
  compositing for general layer blending, which these effects don't need (one side of each blend
  is always the fully-opaque result of a prior step). Double-checked the base/blend argument order
  against pdn's actual `ColorDodgeBlendOp.Apply(lhs,rhs)` generated code (confirmed `lhs`=base,
  `rhs`=blend) rather than assuming - this mattered: an initial guess at `SoftenPortraitEffect`'s
  Overlay argument order was backwards and got corrected once the real generated code was checked.
- `RedEyeRemoveEffect` ported `UnaryPixelOps.RedEyeRemove` faithfully, including a detail worth
  knowing if it looks weirdly aggressive/timid in practice: the saturation *slider* only controls
  how much residual redness survives removal - detection itself uses a hardcoded 100/255 threshold
  in `GetSaturation()`, unrelated to any slider. That's pdn's actual behavior, not a shortcut.

**Verified the same way as the first pass, no shortcuts taken on rigor:** all 17 added to
`KawaPaint.Sandbox/Program.cs`'s smoke test (41 effects total now, full pipeline). A second scratch
harness ran all 17 against the same 64×64 test pattern + a dedicated strongly-red test surface for
`RedEyeRemoveEffect` (the quadrant pattern has no red pixel saturated enough to trigger it) +
the same degenerate-size battery - this is what caught the ReflectCoord hang. `SurfaceBlurEffect`
showed `changed=False` on the quadrant test image; rather than accepting that, re-tested it against
a noisy-flat image instead (piecewise-constant blocks have no soft gradient for an edge-preserving
blur to act on) - confirmed real: noise variance in a flat region dropped from 24.26 to 0.41 while
the hard region boundary stayed exactly crisp (59 vs. 201, no bleed), proving the "no visible
change" on the first test was correct edge-preserving behavior, not a dead effect. All 17 outputs
individually eyeballed (Clouds renders a real cloud texture, Julia/Mandelbrot render actual
fractals, PencilSketch looks convincingly like a graphite sketch, RedEyeRemove darkened only the
saturated-red test surface). Live in the real Windows app: all 6 new submenus (Blurs/Render/
Photo/Artistic plus the extra Distort/Stylize entries) list the right items; opened Clouds,
confirmed the OK-without-touching-a-slider case is a pre-existing `AdjustmentDialog` quirk (Preview
only runs on a slider's `ValueChanged`, so committing untouched does nothing - true for every
adjustment dialog in the app, not new) rather than mistaking it for a bug in the new effect; dragged
the Power slider and confirmed a real black/white cloud pattern rendered live using the canvas's
actual current Fg/Bg colors, committed it, no crash.

**The four bundled tools are also done, 2026-08-19 (same day, third pass).** Clone Stamp, Recolor,
Rounded Rectangle, and Freeform Shape - the pdn "Tools that came bundled with these" item flagged
above as genuinely out of scope for the effect catalogue turned out to fit this codebase's existing
`ITool` architecture (`Tools.cs`) cleanly once actually looked at: unlike `IEffect`, `ITool` already
gets live pointer input (`PointerDown`/`PointerMove`/`PointerUp`) via `ToolContext`, so these were
never blocked on `AdjustmentDialog` at all - that framing in the paragraph above was about the
*effects*, not a real blocker for the tools themselves. Real source this time came from
`origin/3.36pdn`'s `src/tools/` (not `src/Effects/`) - `CloneStampTool.cs`, `RecoloringTool.cs`,
`RoundedRectangleTool.cs`, `FreeformShapeTool.cs`, `ShapeTool.cs` - fetched the same
`git cat-file -p $(git rev-parse ...)` way as the effects (all five turned out to be plain UTF-8,
no repeat of the encoding gotcha). These are real WinForms `Tool` subclasses with hundreds of lines
of cursor/undo/GDI+ plumbing per file; only the core per-pixel/per-path algorithm was ported from
each, same "not a literal file port" approach as the effects.

New engine primitives (`BrushOps.cs`: `CloneDisc`/`CloneLine`, `RecolorDisc`/`RecolorLine`;
`ShapeOps.cs`: `FillRoundedRectangle`/`DrawRoundedRectangle`, `FillPolygon`/`DrawPolygon`) plus four
new `ITool`s in `Tools.cs`, wired into the Tools panel (new icons in `Icons.cs`) with fresh
shortcuts C/N/U/D (P/E/F/K/L/R/O/G/T/M/S were already taken).

- **Clone Stamp**: Ctrl+click sets the source point (no undo step - nothing was painted), then a
  plain drag paints from that source, offset re-anchored at the start of each stroke so repeated
  strokes stay relative to the same fixed source. `ToolContext` gained a `CtrlHeld` field
  (`SurfaceView.OnPointerPressed` reads `e.KeyModifiers`) since no existing tool needed keyboard
  modifier state before this. Samples from `PreStroke` (the layer as it was before the current
  stroke began), not the live surface, so stamping over the source area mid-stroke can't feed back
  into itself as a smear - pdn's own tool has the same property structurally (it snapshots into
  `PlacedSurface`s), confirmed by reasoning about the source rather than by inspection of a
  specific line, since the actual C++/GDI+ plumbing doesn't translate directly.
- **Recolor**: brushes areas close to the *background* color over to the *foreground* color,
  adding the Bg→Fg offset onto each pixel's actual value rather than flattening to a flat color -
  so antialiasing/shading at the edge of what's being recolored carries through unscathed. Ported
  pdn's `RecoloringTool.DrawOverPoints`'s core color-adjustment math
  (`adjusted = lifted + (replacing - toReplace)`, clamped per channel) faithfully, but the
  tolerance test reuses this codebase's own `FloodFill`-style per-channel max-difference metric
  (already what the Tolerance slider means everywhere else in the app - Paint Bucket, Magic Wand)
  rather than porting pdn's separate, differently-scaled `Utility.ColorDifference`. Also ported
  pdn's `RestrictTolerance()` guard: tolerance is capped at the Fg/Bg color difference so a second
  pass over already-recolored pixels can't keep "recoloring" them and drift/oscillate.
- **Rounded Rectangle**: pdn's original doesn't expose a corner-radius control at all (hardcoded
  `radius = 10`) - matched that "just works, no new UI" spirit with a fixed formula
  (`Math.Max(8, BrushWidth * 2)`) instead of porting its GDI+ arc-path/capsule-fallback
  construction. The rasterizer here is a from-scratch raster-native replacement, not a port: a
  single clamped-distance-to-corner test (`InsideRoundedRect`) handles straight edges and all four
  rounded corners without branching, and - this is the part that isn't just a simplification, it's
  a correctness observation pdn needed a special `GetCapsule` fallback for - the same test
  naturally degrades to a capsule or circle once the radius reaches half the shorter side, with no
  special-casing needed. The outline uses a proper "rounded box" signed-distance-field for the
  stroke ring (`RoundedRectDistance`, the standard SDF formula), which is *more* capable than the
  raw inside-test alone: it's what lets `DrawRoundedRectangle` antialias its edge the same way
  `BrushOps`' round brush already does, at no extra design cost.
- **Freeform Shape**: accumulates points while dragging exactly like the existing `LassoSelectTool`
  (this codebase already had the right shape for this), but stamps a filled/outlined polygon onto
  the layer at release instead of replacing the selection. `ShapeOps.FillPolygon` is the same
  even-odd scanline algorithm already in `Selection.ReplaceWithPolygon`, just writing pixels
  instead of mask bits - reused the algorithm, not the code, since one operates on a `Surface` and
  the other on a mask array.

**Verified with the same rigor as the effects passes.** Engine primitives first, headlessly,
against a dedicated scratch harness (not folded into the visual-pattern harness the effects used,
since these needed different setups per primitive): clone-stamped a distinctive 6×6 patch to a
known offset and confirmed the destination pixels matched while pixels outside the brush radius
stayed untouched; recolored a patch and confirmed (a) the bulk of it flipped to the target color,
(b) an unrelated nearby color was left alone, and (c) a deliberately pre-shaded pixel landed
somewhere between the old and new color rather than flattening to either exactly (debug-printed the
actual channel values to confirm this by eye, not just by the boolean assertion) - two of the
initial assertions in this harness were themselves wrong (mismeasured which point fell inside a
12px brush radius, and misread which of BGR channel was dominant in the test colors), caught by
checking the debug output before accepting a FAIL as a real bug, not by assuming the first failure
must be in the new code. Rounded-rectangle and polygon fill/outline were checked both by pixel
assertion (corner pixel empty vs. edge-center pixel filled; radius 0 behaves like a sharp rectangle;
an oversized radius doesn't throw) and by eye against saved PNGs. All of the above also run against
degenerate 1×1 surfaces and radius-0/point-count-0 inputs - no crashes. Beyond the headless layer,
also **added the new primitives to `KawaPaint.Sandbox`'s permanent smoke test** (unlike the effects,
which got their own scratch harness only - these were cheap enough to fold into the permanent one
directly) alongside the existing brush/shape calls.

Live in the real Windows app, all four were driven through an actual full gesture, not just opened:
Clone Stamp - Ctrl+clicked a red square to set the source, dragged over a distant green area, and
the composited screenshot shows an unmistakable red stroke following the drag path with the brush
cursor circle correctly tracking the pointer. Recolor - set Bg to the square's red and Fg to white
via the palette swatches, dragged across the square; at the default Tolerance (32) nothing visibly
changed (the layer's actual raw color didn't fall within 32 of the exact swatch hex - a real
pixel-value gap worth knowing about, not a bug in the tool), raising Tolerance to 200 and repeating
produced a clean white diagonal stroke cutting through the square exactly along the drag path.
Rounded Rectangle - dragged out a shape small enough that the fixed corner radius exceeded half its
shorter side, and it correctly rendered as a stadium/capsule shape rather than anything jagged,
confirming the corner-radius-clamping behavior live, not just in the headless assertion. Freeform
Shape - dragged a seven-point irregular path and it rendered as a correctly closed, filled polygon
matching the traced path exactly, snapping shut back to the start point as intended.

**Small UI-automation lesson from this pass, for next time:** an editable Avalonia `ComboBox`
(the Size/Tolerance boxes) can be set reliably from PowerShell with a click to focus +
`[System.Windows.Forms.SendKeys]::SendWait("^a"); SendKeys::SendWait("<value>{ENTER}")` - no need
for anything fancier. Also, `Add-Type`'s embedded C# compiler is old enough to reject tuple-type
parameters (`(int,int)[]`) and needs `System.Drawing.Point` pulled in explicitly via
`-AssemblyName System.Drawing` if you want `Point[]` instead of two parallel `int[]` arrays for a
multi-waypoint drag helper - parallel arrays were less fuss than chasing the assembly reference.

This closes out every remaining item from the original 2.1 scope, tools included.

### 2.3 - Git-backed history + forge integrations
**Local half done 2026-08-19 (Windows machine, same session as the JP2 codec above); forge half
deliberately not started - scoped down explicitly with the user before starting, see below.**

**The blocker is solved.** `.kwp` was a ZIP of PNGs - opaque to git, full rewrite every save.
`shared/KawaPaint.Engine/DocumentFile.cs` now has `SaveExploded(doc, directoryPath)`/
`LoadExploded(directoryPath)` alongside the original zip Save/Load, writing plain
`manifest.json` + `layers/N.png` (no outer archive), sharing the same private `Manifest`/
`LayerInfo` shape as the zip path. Verified for real, not just "it compiles": a 3-layer, odd-sized
(23×17) document round-trips byte-exact through `SaveExploded`→`LoadExploded`; layer metadata
(opacity etc.) survives; and re-saving with fewer layers deletes the now-stale `layers/N.png`
files rather than leaving orphans behind for git to keep confusedly tracking.

**`AppSettings.Git` now has a real reader.** New `shared/KawaPaint.App/Core/GitService.cs`
(static, LibGit2Sharp-backed): `EnsureRepository(path)` (git-inits if missing, no-ops if already a
repo), `CommitAll(path, message)` (stages everything, commits, returns `false` - not an error -
when nothing actually changed, so a save that re-encodes to byte-identical PNGs doesn't create an
empty commit), `EnsureGitIgnore(path, patterns)`. Every method swallows its own failures and
reports back via an `out string? error` rather than throwing, same "must never interrupt the
user's actual work" rule `AutosaveService` already follows - a failed commit is a lost convenience,
not a lost save.

Two consumers wire it to the two settings that were previously dead:
- **`TrackConfiguration`** - new `shared/KawaPaint.App/Core/ConfigGitTracker.cs`, constructed
  alongside `AutosaveService` in `MainView`'s constructor. Subscribes to
  `SettingsService.Changed`; the first time `Git.Enabled && Git.TrackConfiguration`, it git-inits
  `AppPaths.Root` (the same directory `settings.json`, `recovery/`, `history-cache/`, and
  `presets/` already live in - see `AppPaths.cs`'s own comment: "turning on git tracking means
  tracking one location") and writes a `.gitignore` excluding `recovery/` (timestamped binary
  autosave snapshots - noise, not history) and `history-cache/` (pure scratch, already deleted on
  every startup). Every subsequent settings save commits.
- **`TrackProjects` / `CommitOnSave` / `CommitOnAutosave`** - a project opts in by explicitly
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
alternative - having `.kwp` open/save work on directories instead of files when git tracking is on
- touches the file-picker flow, the `IStorageFile _currentFile` field threaded through ~10 call
sites in `MainView.axaml.cs`, the recent-files MRU list, and the open dialog's folder-vs-file
picker choice. That's a real UX redesign with regression risk for the 95% of users who will never
turn git on, to save what amounts to one extra file-copy step for the 5% who do. Flag if the user
wants that fuller integration later - the mirror approach doesn't block it, it just doesn't
preclude keeping today's save flow exactly as-is either.

**Verified for real** (`scratchpad/gitspike`, not committed - same `ProjectReference`-to-the-real-
project pattern as the codec spikes): `GitService.EnsureRepository`/`CommitAll` produce an actual
`.git` directory and real commits (inspected via LibGit2Sharp's own `Repository.Commits`, not by
re-parsing our own output); the core "a commit only touches the layers that actually changed"
claim was checked by diffing two real commits' trees (`repo.Diff.Compare<TreeChanges>`) after
editing one pixel in one of three layers - the second commit's tree diff contains exactly
`layers/1.png` and nothing else; a same-content re-save produces no commit at all (checked as a
commit-count-unchanged assertion, not just a return value); and `ConfigGitTracker` was driven
through a real `SettingsService` (a new `FileSettingsStore.Create(root)` test-only overload was
added to `ISettingsStore.cs` so this could point at a scratch directory instead of the user's real
`%APPDATA%\KawaPaint` - the existing `TryCreate()` factory hardcoded that path with no way to
override it) - confirmed `Git.Enabled` defaults false and nothing touches the filesystem until
it's turned on, and confirmed `recovery/`/`history-cache/` end up in the tracked commit's tree
listing exactly zero times once gitignored.

**Verified the WASM/browser build still works with `LibGit2Sharp` as a dependency of the *shared*
`KawaPaint.App` project** (which `KawaPaint.Web.csproj` also references) - a real risk since
LibGit2Sharp ships native `libgit2` binaries with no `browser-wasm` RID asset. Did not assume this
was fine; ran both `dotnet build` and a full `dotnet publish -c Release` of `web/KawaPaint.Web.csproj`
before committing to the single-project design (rather than the alternative of isolating git code
into a separate desktop-only project) - both succeeded, no native-asset resolution error, same
graceful-absence pattern the JXL/JP2 P/Invoke codecs already rely on for platforms without their
native library.

**Signature note:** commits use `repo.Config.BuildSignature` (the user's own configured
`user.name`/`user.email` from git config, so `git log` looks like theirs), falling back to a fixed
`KawaPaint <kawapaint@localhost>` identity only if `BuildSignature` throws (no git identity
configured anywhere on the machine) - `BuildSignature` throws rather than degrading on its own, so
this fallback is required, not defensive-for-its-own-sake.

Scope per the user's ruling: git-compatible history **beats** arbitrary history editing - so this
stays on **snapshot mementos + truncate-only deletion** (already how `HistoryStack` works), not a
replayable command log. Don't revisit that trade without asking.

**Forge integrations (GitHub/GitLab/Gitea) - explicitly out of scope for this pass, not forgotten.**
Before starting 2.3 the user was asked how far to take it in one session: local git only, vs. local
git plus forge integration with an OAuth/token design decided now. They chose local-only - the
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

### 2.4 - Native plugin API
**Done 2026-08-19.** Plugin loader + registries + UI, verified end-to-end.

**Engine-side (KawaPaint.Engine/Plugins/):** `IKawaPaintPlugin` contract with `Register(PluginContext)` 
callback, `PluginParameterSpec` hierarchy (numeric/bool/choice/color - full schema, not just sliders), 
`PluginEffectDescriptor`/`PluginToolDescriptor`, `IPluginTool`/`PluginToolContext` (duplicate of App's 
`ToolContext` by design - Engine cannot reference App). `EffectRegistry`/`ToolRegistry` (dedup-by-id, 
later-wins, `Changed` event), `PluginManager.LoadFrom()` (folder scan, collectible `AssemblyLoadContext` 
per plugin, buffered atomic registration with rollback on throw), `PluginLoadResult` + `PluginStatus` 
enum (Loaded/Disabled/Failed).

**App-side (KawaPaint.App/):** `AppPluginHost` (AppSettings+AppPaths → PluginManager), 
`PluginEffectDialog` (data-driven, mirrors AdjustmentDialog lifecycle), `PluginManagerDialog` 
(list results, enable/disable toggle, reload button), `PluginToolAdapter` (ITool wrapper), 
`RebuildPluginsMenu()`/`RebuildPluginToolButtons()` (dynamic menu/toolbar entries), `SelectTool()` 
guard for "plugin:" tags.

**Sample plugin + fixture:** `KawaPaint.Plugins.Sample` (GlowTintPlugin effect with 3 param kinds + 
GlowDotTool), `KawaPaint.Plugins.ThrowingFixture` (throws in Register to verify rollback). Both 
reference only Engine via Private=false, buildable standalone.

**Headless verification (PluginSmokeTest):** Happy path (load, register, effect applies), bad DLL, 
disabled (code never runs), throwing Register (rollback), Id/folder mismatch. All pass. WASM guard: 
`AppPaths.PluginsDirectory` null on browser, so no code path touches `AssemblyLoadContext` there.

**Live app launch:** No crash despite deliberately-broken BadOne/ plugin present (graceful degradation 
confirmed). Plugins menu visible, both Effects and Tools submenus ready for population.

### 3.x - Real Paint.NET plugin compatibility
**Classic tier (`Effect`/`PropertyBasedEffect`) done 2026-08-19.** User's ask was explicit:
compatible with *current* paint.net plugins, not the ancient 3.36-era interfaces this fork started
from - and "how far" (classic-only vs. also the new v5.0+ `BitmapEffect`/`GpuEffect` tiers) got
scoped with the user via `AskUserQuestion` before any code, then researched for real (not
theorized) via a hands-on spike before committing to a plan. Full design rationale lives in the
approved plan this session - key facts worth keeping here:

- Paint.NET's plugin API is three live generations, not one: classic `Effect`/`PropertyBasedEffect`
  (2004-era, `[Obsolete]` since 5.x but still what most plugins in the wild use), the newer CPU
  `BitmapEffect`/`PropertyBasedBitmapEffect` (v5.0+, non-obsolete), and `GpuEffect`/`GpuImageEffect`/
  `GpuDrawingEffect` (v5.0+, hard Direct2D dependency). User's scope choice: eventually all three,
  phased. **Only the classic tier is built so far** - see below for 2/3.
- **No public NuGet SDK exists** - plugin authors compile against paint.net's own real installed
  `PaintDotNet.*.dll` binaries. Confirmed by reading the actual `License.txt` from the official
  v5.1.12 portable release that the license permits copying/redistributing the *unmodified*
  software but forbids "modify, adapt, ... sell, or create derivative works" - given this fork
  exists specifically to get away from that license (FORK.TXT), **KawaPaint never bundles or
  compiles against those binaries**. The bridge is pure runtime reflection (`System.Reflection`/
  `AssemblyLoadContext`, no `PaintDotNet.*` type ever named in KawaPaint's own compiled code) that
  loads them from a real paint.net install the *user* already has separately, detected via
  `PdnInstallLocator` (well-known Windows paths, or a user-set override) or pointed at manually in
  Manage Plugins. `winget` wasn't available on this dev box (no App Installer on this Windows 10
  IoT LTSC build) - used the official portable ZIP from `github.com/paintdotnet/release` instead,
  which is what `PdnInstallLocator`/the Manage Plugins UI also point users at.
- **Proven end-to-end with a real hands-on spike before writing any production code**: instantiated
  a real `PropertyBasedEffect` (`GaussianBlurEffect` from `PaintDotNet.Effects.Legacy.dll`) in a
  plain console app with no paint.net process running, drove its real `PropertyCollection`, called
  its real `Render(...)`, got a correct blur (edge pixel blended R:113/B:142, far pixels stayed
  pure). This is the same fixture `PdnPluginSmokeTest` re-checks against the real production
  pipeline.

**New files, all under `shared/KawaPaint.Engine/Plugins/Pdn/`:** `PdnInstallLocator` (finds/
validates a real install - manifest-only peek via `AssemblyName.GetAssemblyName`, never loads or
executes speculatively), `PdnAssembliesLoadContext` (one shared, non-collectible ALC holding the
real `PaintDotNet.*.dll` set + their own dependency closure for the process lifetime - resolves via
the default context FIRST, only falling back to probing paint.net's install directory, so
BCL-adjacent types like `System.Drawing.Rectangle` keep the *same* type identity KawaPaint's own
compiled code uses; see bug #1 below for why this matters), `PdnPluginLoadContext` (one collectible
ALC per third-party plugin DLL, redirects any `PaintDotNet.*` reference **by name only, ignoring
version** to the shared instance - this is what lets a plugin compiled 10+ years ago against an old
paint.net still resolve today), `PdnReflectionSchema` (every `Type`/`MethodInfo`/`PropertyInfo` the
bridge needs, resolved once, throws one clear "PDN bridge unavailable: ..." at construction on a
shape mismatch instead of a confusing null-ref later), `PdnPropertyMapper` (`PropertyCollection` →
the existing 4 `PluginParameterSpec` types, with honest documented fallbacks for what doesn't map:
unmapped property types stay silently at their paint.net-declared default forever rather than
failing the whole plugin; color pickers are guessed via a `"color"`/`"colour"` substring heuristic
on the property name since `PropertyCollection` alone can't distinguish them from plain bounded
integers; a plugin's custom `OnCreateConfigUI`/`CreateConfigDialog()` is never invoked at all - it
still loads and renders via the default per-property layout), `PdnSurfaceBridge` (KawaPaint's
`Surface` ↔ real `PaintDotNet.Surface`/`RenderArgs` - both already byte-identical BGRA32, so this is
one `Buffer.MemoryCopy` via `MemoryBlock.Pointer` per direction, not a pixel loop; zero-copy was
investigated and ruled out - `MemoryBlock` has no public constructor wrapping an external pointer,
confirmed by reflecting its full member list, not guessed), `PdnClassicEffectAdapter` (the `IEffect`
that builds a fresh effect instance + token on every `Apply()`, matching how
`PluginEffectDescriptor.Build` is already invoked fresh on every dialog preview tick), and
`PdnEffectDiscovery` (the entry point - flat-folder DLL scan, one `PluginEffectDescriptor` per
discovered `PropertyBasedEffect` type, registers straight into the **existing**
`EffectRegistry.Register(...)`, zero changes to that registry).

**App-side:** `shared/KawaPaint.App/Core/Plugins/Pdn/AppPdnPluginHost.cs` (new, parallel to
`AppPluginHost`), `PdnPluginSettings` on `AppSettings` (`Enabled`, `InstallDirectoryOverride`,
`SearchPaths`, `Disabled`), `AppPaths.PdnPluginsDirectory` (new `pdn-plugins` folder - flat,
one loose `*.dll` per plugin, deliberately different from the native plugin system's
one-folder-per-plugin convention since that's how real third-party DLLs are actually distributed).
`PluginManagerDialog` gained a second section (install-path override with Browse/Auto-detect,
status line, plugin list with the same enable/disable-checkbox pattern as native plugins).
`MainView.axaml.cs`/`PluginEffectDialog.cs` needed **zero changes** - both already operate
generically on `EffectRegistry`/`PluginParameterSpec`, so PDN effects just appear once registered,
correctly grouped under their own "Paint.NET Plugins" submenu by the existing category-grouping
code.

**Two real bugs found and fixed during verification, not guessed:**
1. `PdnAssembliesLoadContext`'s first version blindly probed paint.net's install directory for
   *any* requested assembly name, which shadowed `System.Drawing.Primitives` with a second, separate
   copy - silently breaking `PdnReflectionSchema`'s exact-signature `GetMethod` lookup for
   `Effect.Render(..., Rectangle[])` even though the method genuinely exists (different ALC = different
   type identity for `Rectangle`, so the parameter-type array in the `GetMethod` call never matched).
   Fixed by trying the default context first and only falling back to directory-probing for names
   the default context can't already provide.
2. ~9 of paint.net's own 44 bundled legacy effects (Clouds, fractals, Twist/PolarInversion/Dents/
   Tile/RadialBlur/Crystalize) threw during property-collection discovery. Root-caused via an
   unwrapped `TargetInvocationException` (not accepted at face value) to real internal
   `PaintDotNet.AppModel.ISettingsService`/`IEnumLocalizerFactory` service lookups these *specific*
   effects make - undocumented, DI-container-only types that only exist inside the real running
   paint.net app, and that a genuine third-party plugin author wouldn't have access to at all (they
   compile against the public `Effects`/`PropertyBasedEffect` surface, not paint.net's internal
   `AppModel` namespace). Not fixed - flagged as a real, narrow, well-understood limitation specific
   to a handful of paint.net's own bundled effects, not expected to affect genuine third-party
   plugins. A smaller, separate fix (setting `Effect.EnvironmentParameters` to the static
   `EffectEnvironmentParameters.DefaultParameters` before probing/rendering) *was* applied and *did*
   matter - several Warp-family effects read canvas-size-relative defaults from it during
   `OnCreatePropertyCollection` and would otherwise throw with zero plugins involved at all.

**Verified for real, twice, not just "it compiles":**
- **Headless** (`shared/KawaPaint.Sandbox/PdnPluginSmokeTest.cs`, wired into `Program.cs`, gated on
  `KAWAPAINT_PDN_TEST_INSTALL_DIR` so CI/other machines skip gracefully): deploys the real
  `PaintDotNet.Effects.Legacy.dll` as a stand-in third-party plugin, runs it through the actual
  production `PdnEffectDiscovery` → `EffectRegistry` → `PdnClassicEffectAdapter` path, re-checks the
  exact blur fixture the original spike used (same pass/fail bar). Confirmed both the configured
  path (34/44 legacy effects load) and the unset-env-var skip path.
- **Live UI, full gesture** (`win/KawaPaint.Win.csproj`, real portable v5.1.12 install, PowerShell +
  `user32.dll` P/Invoke - no input-automation tool preinstalled on this box, same technique noted
  below in Working Notes): launched the real app with a real plugin DLL in `pdn-plugins/`, navigated
  Plugins ▸ Effects ▸ Paint.NET Plugins (a real 3-level nested Avalonia flyout - needed a smooth
  multi-step mouse *glide* rather than teleporting the cursor directly to the target, since a
  straight jump crosses dead space between flyout panels and Avalonia closes the whole chain),
  confirmed the full humanized effect list (Bulge, Gaussian Blur, Hue And Saturation Adjustment,
  etc.), opened "Gaussian Blur...", confirmed the real `Radius` property rendered as a slider
  (default 2, matching the real `Int32Property`), dragged it to 168 and watched the live preview
  actually blur the canvas in real time, pressed Enter (the OK button is `IsDefault`) to commit, and
  confirmed the canvas kept the blurred result with the title bar's unsaved-changes marker still
  set - the real `TileDeltaMemento`/history commit path, unmodified. Test settings/plugin-folder
  changes made to this machine's real `%APPDATA%\KawaPaint` for the pass were reverted afterward.

**Phase 2/3:**
- **`BitmapEffect`/`PropertyBasedBitmapEffect` (v5.0+ CPU tier) - spiked 2026-08-20, PROVEN
  IMPOSSIBLE to drive from outside paint.net's own compiled binaries. Do not re-attempt without new
  information; this isn't a scope or effort question, it's a hard CLR access-control wall.**

  Real API shape confirmed by driving an actual `BitmapEffectRenderer` end-to-end against the real
  v5.1.12 portable install (`scratchpad/pdn5/spike2`, this session): `IEffect.Initialize(IServiceProvider,
  IEffectEnvironment2)` (internal, reflection-callable) must run before render or it throws
  `NotInitializedException`; the internal `BitmapEffectRenderer` class is the sanctioned driver
  (`Initialize`/`SetToken`/`Render(void* pBuffer, stride, size, ref PixelFormat, RectInt32)`); pixel
  format conversion between our buffer and whatever the effect declares (e.g. `Prgba128Float`) is
  automatic, so `Surface` (BGRA32) can be the render target directly with zero color-space math -
  that part all works and is the same shape the classic-tier bridge already uses.

  **The actual blocker**: `IEffectEnvironment`, `IEffectDocumentInfo`, `IEffectLayerInfo`,
  `IEffectSelectionInfo`, `IBitmapSource<T>` - every interface a host must implement to supply an
  environment - directly require `PaintDotNet.IInternalImpl`, whose sole member
  (`InternalImplementationOnly()`) has C#/CLR `internal` (assembly-only) accessibility. Built a full
  `System.Reflection.Emit`-based dynamic-proxy layer (`TypeBuilder`, one type per interface, boxed
  dispatch trampolines - real, working IL, not a sketch) to implement all of these against KawaPaint's
  own `Document`/`Layer`/`Selection`, wired through a real fixture (the official `SquareBlurBitmapEffect`
  sample plus two custom multi-layer/selection-reading fixtures, compiled against the real assemblies
  in `scratchpad/pdn5/fixture`, never committed). It compiled and ran right up to
  `TypeBuilder.CreateType()`, which the CLR refused with `TypeLoadException: ... is overriding a
  method that is not visible from that assembly` - proven empirically, not inferred. Checked for an
  escape hatch: paint.net's own `RefTrackedObject` base class (public, unsealed, what their real
  internal environment classes derive from) implements `IObjectRef`/`IDisposable`/`IIsDisposed` but
  *not* `IInternalImpl` - confirming the barrier is deliberately placed on the higher-level
  interfaces, not incidental. No public factory/test-host type exists anywhere in the real install
  that bridges this gap either (searched all 25 `PaintDotNet.*.dll` assemblies for one). The only
  remaining "workaround" would be spoofing the dynamic assembly's identity to fool the CLR's
  internal-visibility check - not attempted; that's circumventing an intentional access boundary in
  someone else's SDK, not a legitimate technique, and out of bounds regardless of how the feature is
  scoped.

  This is unconditional, not scope-dependent: even the narrowest possible cut (single layer, no
  selection) still needs a real `IEffectEnvironment`, which needs `IInternalImpl` just the same.
  Classic tier's `Effect`/`Surface`/`RenderArgs` were deliberately public and host-agnostic (a
  holdover from paint.net's older fully-open-source SDK era); the v5.0+ tier's environment
  interfaces were deliberately sealed against external implementation. All code written for this
  (a full working IL-emit bridge, ~5 files) was reverted after confirming the block - see git
  history around 2026-08-20 if the spike code itself is ever needed for reference.
- **`GpuEffect`/`GpuImageEffect`/`GpuDrawingEffect` (v5.0+ Direct2D tier).** Genuinely unexplored -
  real public base-class names weren't even found during this session's reflection spike. Hard
  Direct2D/COM dependency, realistically Windows-only. Not designed here at all; needs its own
  dedicated spike just to identify the real API shape before anything else can be scoped.
- **JPEG XL / JPEG 2000 - spiked 2026-08-19, feasibility confirmed, not yet wired.** Neither is
  impossible, both are impractical-but-accepted (user's explicit call). Both are desktop-only, no
  WASM path, permanent packaging/CI-matrix tax either way. Wire through the existing
  `IImageCodec`/`CodecRegistry` - that plumbing is exactly what's needed.

  **Option A - Magick.NET-Q16-AnyCPU 14.16.0 (verified working).** Added the nuget package in a
  throwaway console project, ran an actual encode+decode roundtrip (not just a format-list check):
  both JXL and JP2 round-tripped an 8x8 test image correctly. Ships prebuilt natives for all 8 RIDs
  (win-x64/x86/arm64, linux-x64/arm64/musl-x64, osx-x64/arm64) - covers every desktop target in one
  dependency. Cost: one native blob per platform, 19-38MB, full ImageMagick rather than scoped to
  just these two formats - acceptable if a single dependency beats two hand-written P/Invoke
  bindings.

  **Option B - direct P/Invoke, verified working for JXL 2026-08-19.** Wrote real bindings
  (`JxlEncoderCreate`/`SetBasicInfo`/`FrameSettingsCreate`/`SetFrameLossless`/`AddImageFrame`/
  `ProcessOutput`, `JxlDecoderCreate`/`SubscribeEvents`/`SetInput`/`ProcessInput`/`GetBasicInfo`/
  `ImageOutBufferSize`/`SetImageOutBuffer`, plus the `JxlBasicInfo`/`JxlPixelFormat` struct layouts
  from `/usr/include/jxl/{decode,encode,codestream_header,types}.h`) against the system
  `libjxl.so` 0.12.0 in a throwaway console project, encoded a 4x4 RGBA random-pixel image
  lossless and decoded it back - byte-for-byte pixel match, not just "it ran". No color-encoding
  call needed: per libjxl's own doc comment on `JxlEncoderAddImageFrame`, omitting
  `JxlEncoderSetColorEncoding`/`SetICCProfile` defaults to nonlinear sRGB for UINT8/16, which is
  what KawaPaint's `Surface` already assumes.

  **Size, decisive point per the user's "as optimized as possible" ask (2026-08-19):**
  hand-rolled needs `libjxl.so` (5.4M) + `libjxl_cms` (200K) + `libhwy` (60K) + brotli enc/dec/common
  (~1.1M) = **~6.7MB total, JXL only**. Magick.NET's single native blob is **20-38MB per platform**
  for JXL+JP2 riding along with 274 unused formats. Hand-rolled is ~3-5x smaller and scoped to
  exactly what's used.

  JP2 not yet spiked the same way (`libopenjp2` 2.5.4 confirmed present via pkg-config, its C API
  not yet bound) - same pattern would apply, do it before wiring either format in.

  **Decided: Option B (hand-rolled P/Invoke), not Magick.NET.**

  **JXL - actually wired in, 2026-08-19, not just spiked.** `shared/KawaPaint.Engine/Codecs/JxlCodec.cs`,
  registered in `CodecRegistry`. Bindings use `LibraryImport` (source-generated marshalling), not
  `DllImport` - no reflection-based stub, AOT-compatible, in line with the "as optimized as
  possible" ask that decided against Magick.NET in the first place. `JxlBasicInfo`'s 100-byte
  padding field had to become an `unsafe fixed byte[100]` rather than a `byte[]` with
  `MarshalAs(ByValArray)` - the source generator (`SYSLIB1051`) rejects non-blittable struct
  fields, `DllImport` would have accepted it silently. Surface is BGRA; libjxl has no BGR(A) pixel
  format (its own `types.h` says so), so encode/decode each do one channel-swap pass over the
  buffer - the only per-pixel cost this adds.

  Verified through the real path, not the throwaway spike project: a 37x29 `Surface` (odd
  non-power-of-two dims, random BGR, alpha swept 0-255 across pixels to catch a premultiply
  mistake) round-tripped through `CodecRegistry.Encode`/`.Decode` - lossless came back
  byte-for-byte identical including alpha, and `Decode` with no filename correctly header-sniffed
  the JXL container via `MatchesHeader`/`JxlSignatureCheck` rather than needing the extension.
  Lossy (`Quality = 80`) encodes smaller and decodes back to the right dimensions (not checked
  pixel-exact, lossy by definition isn't).

  Still open: JP2 P/Invoke binding (not started - see resume plan right below), and the
  still-unsolved Windows/macOS packaging story (vcpkg for `libopenjp2`; a libjxl release/build for
  the same platforms) - the P/Invoke path assumes the system already has `libjxl`/`libopenjp2`
  installed, true here via CachyOS's package but not true on a clean Windows/macOS machine.
  `JxlCodec.IsAvailable` degrades cleanly to false there (probes `JxlDecoderVersion()`, catches
  `DllNotFoundException`) so it just won't show up in file dialogs rather than crashing - but it
  needs bundled natives shipped with the app on those platforms before this is actually usable off
  this box.

  **JP2 - actually wired in, 2026-08-19 (same day, different machine - see the machine-switch note
  below), not just spiked.** `shared/KawaPaint.Engine/Codecs/Jp2Codec.cs`, registered in
  `CodecRegistry`. Same `IImageCodec`/`LibraryImport` pattern as `JxlCodec.cs`.

  The two differences flagged in the old resume plan were real and both got solved:
  1. **No buffer-in/buffer-out API** - solved with an `opj_stream_t` wired to
     `[UnmanagedCallersOnly]` read/write/skip/seek callbacks against a `GCHandle`-tracked state
     object (`Native.StreamState`). Decode reads sequentially out of a `byte[]` already buffered
     from the input `Stream` (same as `JxlCodec.Decode`, sidesteps needing the caller's stream to
     be seekable). Encode writes into an internal `MemoryStream` - required, not a style choice:
     `opj_end_compress` seeks *backward* mid-write to patch box-length headers, which only a
     random-access sink supports - then copies the finished bytes to the real output `Stream` in
     one sequential pass at the end, so the caller's stream never needs to support seeking either.
  2. **Planar pixel buffers** - solved with a per-component gather/scatter loop against `Surface`'s
     interleaved BGRA, folding in the same R/B swizzle `JxlCodec` needs (`OPJ_CLRSPC_SRGB` implies
     component order R,G,B,[A]). Decode handles 1/2/3/4-component images (gray, gray+alpha, RGB,
     RGBA) and rescales non-8-bit `prec` by shifting rather than assuming 8-bit blindly.

  **The actual hard part turned out to be `opj_cparameters_t`'s layout, not the two flagged
  risks.** It's ~18.7KB with 100+ fields, including a 32-element array of nested `opj_poc_t`
  structs (148 bytes each, itself hand-computed) sitting *before* every field this codec actually
  sets (`irreversible`, `tcp_numlayers`, `tcp_rates`, `numresolution`) - so a single wrong offset
  anywhere in the first 4.8KB would have silently misaligned every field after it. No C compiler
  was available on the Windows box this got built on (no cl.exe/gcc/clang, no vcpkg/choco/scoop -
  checked), so there was no `offsetof()` ground truth to check the hand-transliteration against, the
  way a normal port of a struct like this would get verified. Solved by cross-checking at runtime
  against the real `openjp2.dll` instead: `opj_set_default_encoder_parameters`'s doc comment
  explicitly lists its defaults ("Lossless / 1 tile / 64x64 code-block / 6 resolutions / LRCP / no
  ROI upshifted"), so calling it and reading back `prog_order`, `cblockw_init`, `cblockh_init`,
  `numresolution`, `irreversible`, `roi_compno`, etc. at their hand-computed offsets against those
  documented values is a real correctness check, not a guess - and if any offset upstream were
  wrong, at least one of these would read back garbage. All of them matched on the first run after
  a copy-paste fix (see bugs below), across the smallest fields (`tile_size_on` at offset 0) through
  the ones sitting right after the 4.8KB POC block (`numresolution`, `irreversible`) - meaning the
  offset chain is correct through the field furthest from the start that this codec touches.

  **Verification, real round trips, not just the struct-layout check:** downloaded the official
  `uclouvain/openjpeg` v2.5.4 Windows x64 release (prebuilt `openjp2.dll` + headers + the
  `opj_compress`/`opj_decompress` reference CLI tools) for a from-scratch spike project
  (`scratchpad/jp2spike`, not committed) with a `ProjectReference` to the real
  `KawaPaint.Engine.csproj`. Checked, in order: (1) our encoder's output decodes correctly with the
  *real* `opj_decompress.exe`, not just our own decoder; (2) our own encode→decode round trip is
  byte-exact lossless, including a 37×29 (odd, non-power-of-two) surface with a full BGR random
  sweep and a 0–255 alpha ramp, run through the actual `CodecRegistry.Encode`/`.Decode` - not the
  spike's own bindings - with decode header-sniffed (no filename given), matching the bar the JXL
  codec was held to; (3) our decoder correctly reads the *real* `opj_compress.exe`'s output,
  byte-exact - closes the loop the other direction, so a bug that happened to be symmetric between
  our own encode and decode paths couldn't hide; (4) lossy path produces smaller output and stays
  visually close on a worst-case random-noise image; (5) tiny images down to 1×1 round-trip
  byte-exact (see bug 2 below); (6) with `openjp2.dll` removed from the output directory,
  `Jp2Codec.IsAvailable` returns `false` without throwing and the format silently drops out of
  `CodecRegistry.Encoders`/`.Decoders` - the graceful-degradation path actually works, not just in
  theory.

  **Two real bugs found and fixed during verification (both would have shipped silently broken
  without the round-trip tests above - the struct-layout check alone would not have caught
  either):**
  1. The write callback wrote into the output `MemoryStream` without first syncing its `Position`
     to the codec's own tracked position. `opj_end_compress` seeks backward to patch box-length
     headers, and `MemoryStream.Write` only advances *its own* internal position - never told about
     the seek, so post-seek writes landed at the wrong offset and silently corrupted the box
     headers. Symptom: both the real `opj_decompress.exe` and our own decoder failed identically
     ("Expected a SOC marker") on our encoder's output. Fixed by setting
     `output.Position = state.Position` before every write, matching what the read callback already
     did.
  2. openjpeg's default `numresolution = 6` requires `2^(numresolutions-1) <= min(width, height)`,
     i.e. the short side must be at least 32px - fails outright ("Number of resolutions is too high
     in comparison to the size of tiles") on anything smaller, which includes ordinary icon-sized
     images. Fixed with a `ClampResolutions(width, height)` helper (`shared/KawaPaint.Engine/Codecs/
     Jp2Codec.cs`) that picks the largest valid resolution count, always applied rather than only
     for small images. Verified against 16×16, 4×4, 3×7, and 1×1.

  Quality mapping: unlike JPEG's IJG 1-100 scale or JXL's `JxlEncoderDistanceFromQuality`, JP2 has
  no standard "quality" concept - `EncodeOptions.Quality` maps onto a compression ratio
  (`tcp_rates[0] = 101 - quality`, `cp_disto_alloc = 1`), a documented judgment call, not a
  perceptual calibration. Revisit if real images show it's poorly scaled in practice.

  **Windows natives bundled, 2026-08-19 (same day, third session on this Windows box).** Both
  `JxlCodec.IsAvailable` and `Jp2Codec.IsAvailable` are now true out of the box on Windows, no
  system install required - closes the "still open" gap noted above and immediately below.

  Fetched real prebuilt Windows x64 binaries rather than building from source (no C compiler was
  available on this box, per the note below): `openjpeg-v2.5.4-windows-x64.zip` from
  `uclouvain/openjpeg`'s own GitHub release (matches the 2.5.4 already verified against), and
  `jxl-x64-windows.zip` from `libjxl/libjxl`'s v0.12.0 release (matches the libjxl version this
  codec was originally verified against on CachyOS).

  **The libjxl release ships more DLLs than `JxlCodec` actually needs** (`gif.dll`, `jpeg62.dll`,
  `libpng16.dll`, `libsharpyuv.dll`, `libwebp.dll`, `zlib1.dll`, `jxl_threads.dll` - support for
  `jxl_extras`/the `cjxl`/`djxl` CLI tools' format conversion, not the core codestream API this
  binds against). Didn't just bundle the whole folder: empirically bisected the minimal working set
  with `LoadLibraryEx(..., LOAD_WITH_ALTERED_SEARCH_PATH)` + `GetProcAddress`/`JxlDecoderVersion()`
  calls from a throwaway PowerShell P/Invoke harness (no dumpbin/objdump/python on this box to read
  the import table directly) - `jxl.dll` + `jxl_cms.dll` + `brotlicommon`/`brotlidec`/`brotlienc`
  (5 files, no separate `hwy.dll` - Highway is statically linked into this build) is the real
  minimum; confirmed by calling `JxlDecoderVersion()` through it and getting back `12000` (0.12.0),
  not just a successful `LoadLibrary`. Kept openjpeg's full official `bin/` folder as-is (9 files:
  `openjp2.dll` + `concrt140`/`msvcp140*`/`vcruntime140*`) rather than bisecting further - this
  machine's own VC++ redist already installed made `openjp2.dll` load standalone here, which would
  have been a false-negative "don't need the runtime DLLs" signal specific to this box; shipping the
  official self-contained set avoids that trap on a cleaner target machine. Combined: **5.8MB**,
  in line with the ~6.7MB estimate above.

  Wired into `win/KawaPaint.Win.csproj` as `<None Include="natives\win-x64\*.dll" Link="..."
  CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />` - DLLs live in
  the new `win/natives/win-x64/` folder (committed to git, not gitignored) and land flat next to
  `KawaPaint.Win.exe` in both `dotnet build` and `dotnet publish` output, which is what makes
  bundled-native P/Invoke resolution work on .NET/Windows (confirmed: SkiaSharp/HarfBuzzSharp/
  LibGit2Sharp's own native DLLs already sit in this same flat output folder via NuGet's runtimes/
  mechanism - this is that same pattern, just via a plain `None` item instead of a NuGet package).
  The `Link` metadata matters: without it MSBuild preserves the `natives\win-x64\` subfolder in the
  output tree instead of flattening - caught this because the first build put the DLLs one level
  too deep and `IsAvailable` still read false.

  **Verified for real, not just "the files are present":** a headless harness
  (`scratchpad/codectest`, not committed, `ProjectReference` to the real
  `KawaPaint.Engine.csproj`) copied into the actual `win/bin/Debug/net10.0/` output directory and
  run from there - `JxlCodec.IsAvailable`/`Jp2Codec.IsAvailable` both true, and a full
  `CodecRegistry.Encode`/`.Decode` round trip (37×29 odd-sized BGRA-random/alpha-swept surface,
  lossless byte-exact including alpha for both formats, lossy encodes smaller, header-sniffed with
  no filename given, plus 1×1 and 4×4 degenerate sizes exercising `Jp2Codec`'s
  `ClampResolutions`) all passed - same bar the original JXL/JP2 verification used. Also ran a real
  `dotnet publish -c Release -r win-x64 --self-contained` and confirmed all 14 native DLLs land in
  the publish output next to the apphost exe, not just the build output. Launched the real
  `KawaPaint.Win.exe` after this change - starts clean, no crash, autosave/crash-recovery correctly
  restored the prior session's canvas.

  Still open: macOS packaging (same pattern, needs a macOS box or cross-fetching prebuilt osx-x64/
  osx-arm64 releases from both upstreams - not attempted here, this session only had a Windows
  box). `IsAvailable` still correctly degrades to false wherever the bundled natives aren't present
  for the current RID.

  **Mid-project machine switch, worth knowing if something here looks inconsistent:** the JXL work
  and this file's original resume plan were written on a Linux (CachyOS) box; this JP2 work was
  done in the very next session, same day, on a Windows box instead - different filesystem, no
  system package manager for native libs, and initially no C compiler of any kind (see above). The
  dotnet SDK *is* installed on this Windows box (10.0.400) but is not on `PATH` in a fresh shell -
  invoke it via its full path, `C:\Program Files\dotnet\dotnet.exe`, or add that directory to
  `PATH` for the session, or things like `dotnet build` will fail with a plain "not recognized"
  error that has nothing to do with the project itself.

### 4.x - Deferred, gated on other decisions
Branching/non-linear history and git-as-literal-undo-timeline are gated on revisiting the
snapshot-vs-command-log ruling above - don't build without an explicit go-ahead, the user
prioritized git-compat truncate-only over this.

## Open decisions (assumed defaults below; flag if the user should be asked explicitly)
- Is the browser/WASM build first-class? Assumed: demo target, features gracefully absent there.
- Snapshot vs. replayable command log for history? Assumed: snapshots (settled per the ruling
  above, but the command-log alternative is what unlocks Tier 4 - noted here as the fork point).
- Git scope: backup/versioning of projects + config, or the literal undo timeline? Assumed:
  backup/versioning only.
- ~~Native plugin API before Paint.NET compat, or the reverse?~~ Resolved: native API first (2.4),
  Paint.NET compat as a reflection-based bridge on top of it (3.x classic tier, done 2026-08-19) -
  played out exactly as assumed, `EffectRegistry`/`PluginParameterSpec`/`PluginEffectDialog` all
  reused unchanged.

## Working notes for whoever resumes

- **Do not bulk-edit these files with `perl -0pi` from Git Bash on the Windows box.** It reads the
  file as Latin-1, so every non-ASCII character comes back double-encoded: the em dashes all over
  this codebase's comments and UI strings turned the em dash (`e2 80 94`) into `â` (`c3a2 c280 c294`), and
  the app then renders "* untitled â KawaPaint" in its own title bar. It corrupts the file even on
  a substitution that matches nothing, because `-p` rewrites unconditionally. Hit this during the
  demo-recorder work and repaired 56 sequences in `MainView.axaml.cs`. Use the editing tools, or
  pass `perl -CSD`. To detect it afterwards, scan for `[Â-ô][-¿]{1,3}`.
- **No input-automation tool in this sandbox** (no xdotool/wl-copy/ydotool/xclip). Verification
  pattern used throughout: (1) headless console harness project under
  `/tmp/.../scratchpad/codectest` with a `ProjectReference` to the real `KawaPaint.Engine`/
  `KawaPaint.App` csproj - exercises actual production types, not reimplementations; (2) for
  anything requiring a real render, a *temporary* debug hook spliced into `MainWindow.axaml.cs` or
  `MainView.axaml.cs` (auto-open a dialog, force-select a tool, inject fake data) + `spectacle -b
  -n -a -o out.png` to screenshot, then revert the hook before committing. Never ship a debug hook.
- Build: `dotnet build KawaPaint.slnx`. Run desktop: `dotnet run --project linux/KawaPaint.Linux.csproj`
  (or `dotnet linux/bin/Debug/net10.0/KawaPaint.Linux.dll` after a build, faster for repeat runs).
- Settings/state live at `~/.config/KawaPaint/` on Linux - delete it to reset to defaults when
  testing first-run behavior (several bugs above only showed up on a truly fresh install).
- **On Windows** (this box, as of the 2026-08-19 JP2 session): `dotnet` is installed (10.0.400) but
  not on `PATH` in a fresh shell - use the full path `C:\Program Files\dotnet\dotnet.exe`, or
  `$env:PATH += ';C:\Program Files\dotnet'` for the session. Desktop project is
  `win/KawaPaint.Win.csproj`, not the Linux one. No C compiler (cl.exe/gcc/clang) and no
  vcpkg/choco/scoop were present - checked directly, don't assume any of them exist without
  checking again. Network access to github.com worked fine for pulling reference native libraries
  when a spike genuinely needed real ground truth to verify against (see the JP2 entry above).
