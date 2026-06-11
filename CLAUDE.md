## Context

This is a fork of the project timeline Controller by carlself which was discontinued 4 years ago.
The goal is to port into a portable package for the modern Unity 6.x and Timeline packages. As of now it was meant for binding to objects at runtime.
Our goal is to use it to bind to cross-scene references, for timeline authoring workflows involving Additive scenes.

The target project is **Kitchen of Memories** (KitchenOfMemories_Unity6), a gastronomical XR experience for Meta Quest 3. It uses an additive scene architecture: Bootstrap + Restaurant are always loaded, Memory scenes are loaded/unloaded on top. Timelines in one scene (e.g. Restaurant) need to drive objects in another (e.g. Memory1).

The authoring workflow requires all relevant scenes to be open additively in the editor simultaneously. This is both the edit-time and runtime model — no special handling needed for missing references.

## What Was Done

### Package Conversion
Converted the original Unity project into a proper embedded UPM package:

- `package.json` created — name: `com.tlm.timeline-controller`, author: TLM, dependencies: `com.unity.timeline` + `com.unity.modules.director` only (Addressables was a project-level dep, not used in code)
- `Runtime/` — `TimelineController.cs`, `TimelineReference.cs`, `ShowAsReadOnlyAttribute.cs` + `TLM.TimelineController.asmdef`
- `Editor/` — `TimelineControllerEditor.cs`, `ShowAsReadOnlyDrawer.cs` + `TLM.TimelineController.Editor.asmdef` (Editor platform only)
- `Samples~/Basic Example/` — original `Assets/Example` content (Prefabs, Scenes, Scripts, Timeline)
- Removed inner `Packages/` folder (was legacy Unity project manifest, not needed in a package)
- Added `file:./Packages/timeline-controller` entry to KOM_TechTests `Packages/manifest.json`

### Cross-Scene Binding — Validated in TechTests
Dummy scenario: `BaseSceneWithTimeline` (PlayableDirector + TimelineController) + `LayeredScene1` (Cube) + `LayeredScene2` (Sphere) loaded additively. Confirmed working.

Key fixes made during validation:
- `[ExecuteInEditMode]` → `[ExecuteAlways]` on both `TimelineController` and `TimelineReference`
- `ActiveInScene` (NonSerialized) was resetting to `false` on every domain reload, causing `Update()` to bail. Fixed by re-initializing it in `OnEnable()` so it survives recompiles
- `UpdateBindingList` was calling `Clear()` every frame — if a bound object was null (scene unloaded), the stored GUID was lost. Fixed with a merge strategy: only update an entry when a live binding exists; if `owner == null`, skip and keep the stored GUID
- `InstallRuntimeBindings()` added to `Update()` loop so bindings are restored as soon as a scene reloads and `TimelineReference.Awake` re-registers into `IdMap`
- `TimelineReference` is stamped automatically by the controller — authors never add it manually

Validated behaviour:
- Bind objects in Timeline window normally → GUIDs captured automatically
- Unload a layered scene → GUIDs persist in `trackBindings`
- Reload the scene → bindings restored automatically next `Update()` tick

### Multi-Asset Support — One Director, Multiple Timelines

Added `TimelineBindingData` ScriptableObject and `TimelineAssetEntry` list to support swapping multiple `TimelineAsset`s on a single `PlayableDirector` while preserving bindings per asset.

**Architecture:**
- `TimelineBindingData` SO — owns `trackBindings` + `nestedTimelineBindings` for one `TimelineAsset`. Embedded as a sub-asset inside its owning `.playable` file (visible as foldable child in Project browser)
- `TimelineController` retains live flat lists (`trackBindings`, `nestedTimelineBindings`) as the active working set — these are **not** `[SerializeField]`; they are a pure runtime cache rebuilt from the SO on every `OnEnable`. The scene file never contains binding data.
- On scene load, `OnEnable` defers via a retrying `delayCall` until both `playableAsset` and `bindingData` sub-asset references have resolved (Unity restores cross-asset references asynchronously). `TimelineReference.OnRegistered` is also subscribed in edit-mode so Control Track activation of an inactive nested GameObject triggers a reinstall.
- `List<TimelineAssetEntry>` on the controller maps each `TimelineAsset` → its `TimelineBindingData` SO
- `FlushBindingsToSO()` — mirrors live lists → SO (called every editor frame and before swap)
- `LoadBindingsFromSO()` — copies SO → live lists (called after swap)
- `SetTimeline(TimelineAsset)` — flush outgoing, swap asset, load incoming, install bindings, fire `OnTimelineChanged` event
- `InstallRuntimeBindings()` always reads from live lists — no SO lookup at runtime

**Editor navigator** (`TimelineControllerEditor`):
- Flat button list of all registered timeline assets; active one shown as `[ Name ]` (disabled)
- Clicking an inactive entry calls `SetTimeline` — both `director` and `timelineController` are `Undo.RecordObject`'d before the call and marked dirty after
- **Add Current Asset** button — reads the director's current asset, creates a `BindingData` sub-asset embedded inside the `.playable` file via `AssetDatabase.AddObjectToAsset`, registers the pair
- `✕` button removes an entry from the list

**Key fix during validation:**
- `Undo.RecordObject(timelineController)` must be called before `SetTimeline` modifies the live lists, otherwise Unity doesn't serialize the loaded bindings and they revert on next repaint/reload

### MergeRule Pattern

When iterating bindings to update them, objects in unloaded additive scenes resolve to `null`. The rule for what to do with a stale entry is encapsulated in `MergeRule()` overloads — one for `TrackBinding`, one for `ClipBinding`. They read `additiveSceneWorkflow` (default `true`) on `TimelineController`:

- `true` — skip the unresolvable entry, preserve the stored GUID (binding restores when the scene reloads)
- `false` — remove the stale entry (classic rebuild behavior)

**Apply this pattern whenever a new update loop is added** that iterates bindings and might encounter unloaded-scene nulls. Always call `MergeRule(list, ...)` at the null-check site and `continue` — never inline the remove/skip logic at the call site.

### Control Track Clip Reference Fix

`UpdateNestedTimelineBindingList` originally called `nestedTimelineBindings.Clear()` at the top of every frame — same bug as the earlier `trackBindings` fix. When a layered scene is unloaded, `sourceGameObject.Resolve()` returns `null`, the entry is skipped, and the `Clear()` wiped the stored GUID. Fixed with the same merge strategy: removed the `Clear()`, find entry by `trackIndex`+`clipIndex`, update in place when live, call `MergeRule` when null.

### Self-Targeting Binding Rule

Tracks or clips whose bound object is the `TimelineController`'s own GameObject must never be written into `trackBindings` or `nestedTimelineBindings`. The loop index still advances past them (index counting is unaffected), but no entry is stored — Unity handles those bindings natively.

The resolution outcome is encapsulated in `NestedOwnerResolution` (`Missing` / `Self` / `Resolved`). Use `ClassifyNestedOwner()` at capture and `ResolveNestedOwner()` at install. Never add inline `== gameObject` checks outside these two methods.

### ClipBinding (formerly NestedTimlineBinding)

There are only two structural binding dimensions: **track bindings** (`TrackBinding`, `pd.GetGenericBinding(track)`) and **clip bindings** (`ClipBinding`, a Control Track clip's `sourceGameObject` exposed reference). Previously, clip bindings were only captured when the resolved object had its own `PlayableDirector` (i.e. drove a nested timeline) — Control Track clips targeting plain GameObjects (activate/deactivate, trigger components, etc.) were silently skipped and never persisted, so their references were lost across additive scene unload/reload.

`ClipBinding` now captures the `id` for **any** resolved, non-self clip target. `timelineAsset`/`nestedTimelineTrackBindings` are populated only when the target also has a `PlayableDirector` — they're an optional addendum (the target's own track bindings), not a gate on whether the clip binding itself is captured. Install always restores the `sourceGameObject` reference via `SetReferenceValue`/`RebuildGraph`; the nested-director `playableAsset`/track-binding install only runs `if (entry.timelineAsset != null)`.

### Reset Bindings

`ResetActiveBindings()` (#if UNITY_EDITOR) clears the live lists and the active timeline's `BindingData` SO, then immediately re-captures from the current scene state. Exposed as a red button in the Inspector. Use this whenever the timeline's track layout changes and stored indices go stale.

## Unity API Invariants

These are permanent behavioural facts about Unity's Timeline API that must be kept in mind when working on this package.

### `GetOutputTrack` flat index ordering
`GetOutputTrack(i)` skips `GroupTrack` entirely but appends subtracks *after* all root-level tracks — not inline at their visual position in the Timeline window. Index order is: all root bindable tracks first, then subtracks grouped by parent. Both capture and install must use the same API so indices remain consistent. A reorder or group move changes indices globally — any stored index is stale after such an operation.

### `MarkerTrack` returns a spurious binding
`GetGenericBinding` on a `MarkerTrack` returns a non-null object even when the binding field is visually empty in the Timeline window. It must be explicitly skipped in any binding capture loop — it is never a valid cross-scene binding target.

### `TimelineReference.IdMap` is wiped on domain reload
`IdMap` is a static field — it is reset to empty on every domain reload. `Awake` only fires for objects whose scene was loaded or reloaded after the reload, so objects in always-loaded scenes (e.g. Bootstrap) never re-register via `Awake` alone. `OnEnable` must also call `Register()` since it fires on domain reload for `[ExecuteAlways]` components in already-loaded scenes. `Register()` must guard against duplicate entries.

## Diff-Based SO Autosave

Solves an edge case where a developer on an older branch pulls, opens a scene with stale/empty `TimelineBindingData`, and any incidental scene edit triggers `_bindingsDirty` — recapturing bindings against an uninitialized `IdMap` and overwriting the SO with empty/wrong data, which then gets committed.

**Mechanism:** `LoadBindingsFromSO` snapshots the loaded SO state into `_cachedTrackBindings`/`_cachedNestedBindings` ([NonSerialized], scene-volatile). On every `_bindingsDirty` tick, after `UpdateBindingList`/`UpdateNestedTimelineBindingList` recapture the live lists, `ApplyBindingDiffToSO` compares the fresh capture against the cache:
- No diff → no SO write at all (regardless of how stale the capture is, if it matches what's cached, nothing changes)
- Diff found → full flush via `FlushBindingsToSO` (which also re-syncs the cache), and the diff entries are recorded in `_lastDiffSummary` for the Inspector

This makes the SO write a one-cycle, diff-gated event rather than an unconditional per-tick flush. `ResetActiveBindings` and `SetTimeline`'s pre-swap flush remain full, unconditional writes — they're explicit user-triggered rebuilds, not autosave.

## Future Plans

- **Nested BindingData as sub-asset** — clip bindings (`ClipBinding`) still live flat inside the parent `TimelineBindingData`; could be embedded as sub-assets like the top-level ones
- **Subtrack support** — subtracks inside groups are reachable via `GetOutputTrack` but their indices are appended after all root tracks, not at their visual position; a path-based scheme (group index + child index) would be more stable
- **Track index invalidation detection** — reordering tracks changes stored indices silently; storing track name/type alongside the index would allow a sanity check at install time
- **Vanilla (no-SO) persistence** — when no `TimelineAssetEntry`/`TimelineBindingData` is assigned for the active asset, `trackBindings`/`nestedTimelineBindings` are pure runtime caches and never persist. The diff/SO-write machinery only applies when a `bindingData` SO is assigned via "Add Current Asset". For the no-SO case, consider making `trackBindings`/`nestedTimelineBindings` `[SerializeField]` again so bindings persist in the scene file as they did before SO support was added — vanilla mode should remain a simple component-serialization workflow, distinct from the SO-backed multi-asset workflow.
