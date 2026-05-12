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

## Future Plans

- **Add to KOM project** — add `file:./Packages/timeline-controller` to `KitchenOfMemories_Unity6/Packages/manifest.json`
- **Namespace cleanup** — all classes are currently in the global namespace; move to `TLM.TimelineController` namespace to avoid collisions in a multi-package project
- **Remove Addressables dependency from Samples** — verify the `Samples~/Basic Example` scenes have no lingering Addressable references
- **Nested BindingData as sub-asset** — already implemented for top-level assets; nested timeline bindings (`NestedTimlineBinding`) still live flat inside the parent `TimelineBindingData`
