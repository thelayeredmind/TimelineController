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
- `TimelineController` retains live flat lists (`trackBindings`, `nestedTimelineBindings`) as the active working set
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

When iterating bindings to update them, objects in unloaded additive scenes resolve to `null`. The rule for what to do with a stale entry is encapsulated in `MergeRule()` overloads — one for `TrackBinding`, one for `NestedTimlineBinding`. They read `additiveSceneWorkflow` (default `true`) on `TimelineController`:

- `true` — skip the unresolvable entry, preserve the stored GUID (binding restores when the scene reloads)
- `false` — remove the stale entry (classic rebuild behavior)

**Apply this pattern whenever a new update loop is added** that iterates bindings and might encounter unloaded-scene nulls. Always call `MergeRule(list, ...)` at the null-check site and `continue` — never inline the remove/skip logic at the call site.

### Control Track Clip Reference Fix

`UpdateNestedTimelineBindingList` originally called `nestedTimelineBindings.Clear()` at the top of every frame — same bug as the earlier `trackBindings` fix. When a layered scene is unloaded, `sourceGameObject.Resolve()` returns `null`, the entry is skipped, and the `Clear()` wiped the stored GUID. Fixed with the same merge strategy: removed the `Clear()`, find entry by `trackIndex`+`clipIndex`, update in place when live, call `MergeRule` when null.

## Future Plans

- **Nested BindingData as sub-asset** — nested timeline bindings (`NestedTimlineBinding`) still live flat inside the parent `TimelineBindingData`; could be embedded as sub-assets like the top-level ones
