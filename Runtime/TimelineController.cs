using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace TLM.TimelineController
{
    [Serializable]
    public class TimelineAssetEntry
    {
        public TimelineAsset timelineAsset;
        public TimelineBindingData bindingData;
    }

    [RequireComponent(typeof(PlayableDirector))]
    [ExecuteAlways]
    public class TimelineController : MonoBehaviour
    {
        [SerializeField]
        List<TimelineAssetEntry> timelineEntries = new List<TimelineAssetEntry>();
        // Active entry's bindingData — pure runtime cache, rebuilt on OnEnable/SetTimeline.
        TimelineBindingData bindingData;

        PlayableDirector playableDirector;
        Action onComplete;
        Dictionary<string, GameObject> runtimeObjMap = new Dictionary<string, GameObject>();
        List<TimelineReference> timelineReferences = new List<TimelineReference>(10);
        PlayableAsset _lastKnownAsset;
        // Set by OnSceneStateChanged; cleared in Update/LateUpdate after sweep + install.
        bool _referencesDirty;
#if UNITY_EDITOR
        // True from OnEnable until the deferred tryLoad completes — Update() must not run a
        // capture pass while true, the bindingData cross-asset reference may not be resolved yet.
        bool _pendingLoad;
#endif
        // Incremented each time InstallRuntimeBindings runs — readable by smoke tests.
        [NonSerialized] public int InstallCount;

#if UNITY_EDITOR
        public List<TimelineAssetEntry> TimelineEntries => timelineEntries;
        public TimelineBindingData BindingData => bindingData;

        void OnValidate()
        {
            playableDirector = GetComponent<PlayableDirector>();
        }
#endif

        private void Awake()
        {
#if UNITY_EDITOR
            if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewSceneObject(this))
                return;
#endif
            playableDirector = GetComponent<PlayableDirector>();
            if (!Application.isPlaying)
                InstallRuntimeBindings();
        }

        private void OnEnable()
        {
#if UNITY_EDITOR
            if (!EditorApplication.isPlaying)
            {
                if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewSceneObject(this))
                    return;

                _pendingLoad = true;
                playableDirector = GetComponent<PlayableDirector>();
                TimelineReference.OnSceneStateChanged += OnReferencesChanged;
                // playableAsset and the bindingData sub-asset reference inside timelineEntries
                // are cross-asset references Unity resolves asynchronously after OnEnable.
                // Retry each editor tick until both are non-null (max 10 ticks).
                int retriesLeft = 10;
                EditorApplication.CallbackFunction tryLoad = null;
                tryLoad = () =>
                {
                    if (this == null) { EditorApplication.delayCall -= tryLoad; return; }
                    var asset = playableDirector.playableAsset as TimelineAsset;
                    var entry = asset != null ? timelineEntries.Find(e => e.timelineAsset == asset) : null;
                    if (entry?.bindingData == null && --retriesLeft > 0) return;
                    EditorApplication.delayCall -= tryLoad;
                    _lastKnownAsset = playableDirector.playableAsset;
                    bindingData = entry?.bindingData;
                    TimelineReference.RegisterAll();
                    InstallRuntimeBindings();
                    _pendingLoad = false;
                };
                EditorApplication.delayCall += tryLoad;
                return;
            }
#endif
            playableDirector = GetComponent<PlayableDirector>();
            playableDirector.stopped += OnPlayableDirectorStopped;
            TimelineReference.OnSceneStateChanged += OnReferencesChanged;
        }

        private void Start()
        {
#if UNITY_EDITOR
            if (!EditorApplication.isPlaying)
                return;
#endif
            // All Awakes have fired — safe to sweep for any missed inactive references then install.
            _lastKnownAsset = playableDirector.playableAsset;
            bindingData = FindBindingData(playableDirector.playableAsset as TimelineAsset);
            TimelineReference.RegisterAll();
            InstallRuntimeBindings();
        }

        TimelineBindingData FindBindingData(TimelineAsset asset)
        {
            if (asset == null) return null;
            return timelineEntries.Find(e => e.timelineAsset == asset)?.bindingData;
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                TimelineReference.OnSceneStateChanged -= OnReferencesChanged;
            }
#endif
            if (!Application.isPlaying)
                return;

            runtimeObjMap.Clear();
            playableDirector.stopped -= OnPlayableDirectorStopped;
            TimelineReference.OnSceneStateChanged -= OnReferencesChanged;
            onComplete = null;
        }

        public void Play(Action onComplete)
        {
            InstallRuntimeBindings();
            playableDirector.Play();
            this.onComplete = onComplete;
        }

        // Swaps the active TimelineAsset on the director, loads the matching bindingData
        // (if any) and installs its bindings.
        public void SetTimeline(TimelineAsset asset, bool playableAssigned = false)
        {
            if (!playableAssigned)
                playableDirector.playableAsset = asset;

            _lastKnownAsset = asset;
            bindingData = FindBindingData(asset);
            InstallRuntimeBindings();
        }

        public void AddRuntimeObject(GameObject bindingObject)
        {
            timelineReferences.Clear();
            bindingObject.GetComponentsInChildren(true, timelineReferences);
            if (timelineReferences.Count == 0)
                return;

            foreach (var timelineRef in timelineReferences)
                runtimeObjMap.Add(timelineRef.Id, timelineRef.gameObject);
        }

        void OnPlayableDirectorStopped(PlayableDirector pd)
        {
            onComplete?.Invoke();
        }

        // ------------------------------------------------------------------
        // Structural keying
        // ------------------------------------------------------------------

        // Walks the track tree (root tracks + nested GroupTrack children) in visual order,
        // computing a TrackKey for each bindable track. GroupTrack and MarkerTrack are
        // skipped as bindable entries but GroupTrack still contributes to groupPath/recursion.
        static void WalkTracks(TimelineAsset timelineAsset, Action<TrackAsset, TrackKey> visit)
        {
            var occurrenceCounts = new Dictionary<(string, string, string), int>();
            WalkTrackList(timelineAsset.GetRootTracks(), "", occurrenceCounts, visit);
        }

        static void WalkTrackList(IEnumerable<TrackAsset> tracks, string groupPath,
            Dictionary<(string, string, string), int> occurrenceCounts, Action<TrackAsset, TrackKey> visit)
        {
            foreach (var track in tracks)
            {
                if (track == null)
                    continue;

                if (track is GroupTrack group)
                {
                    string childPath = string.IsNullOrEmpty(groupPath) ? group.name : groupPath + "/" + group.name;
                    WalkTrackList(group.GetChildTracks(), childPath, occurrenceCounts, visit);
                    continue;
                }

                if (track is MarkerTrack)
                    continue;

                string trackType = track.GetType().Name;
                var occKey = (groupPath, track.name, trackType);
                occurrenceCounts.TryGetValue(occKey, out int occurrence);
                occurrenceCounts[occKey] = occurrence + 1;

                var key = new TrackKey
                {
                    groupPath = groupPath,
                    trackName = track.name,
                    trackType = trackType,
                    occurrence = occurrence,
                };
                visit(track, key);
            }
        }

        // ------------------------------------------------------------------
        // Runtime install path
        // ------------------------------------------------------------------

        GameObject GetBindTarget(string id)
        {
            if (runtimeObjMap.TryGetValue(id, out var bindTarget))
                return bindTarget;

            if (!TimelineReference.IdMap.TryGetValue(id, out var instances))
                return null;

            return instances.Count == 0 ? null : instances[0];
        }

        bool BindTrack(PlayableDirector pd, TrackAsset trackAsset, string id)
        {
            Type outputType = null;
            foreach (var output in trackAsset.outputs)
            {
                outputType = output.outputTargetType;
                break;
            }

            if (outputType == null)
                return false;

            bool isComponent = typeof(Component).IsAssignableFrom(outputType);
            bool isGameObject = typeof(GameObject).IsAssignableFrom(outputType);
            if (!isComponent && !isGameObject)
                return false;

            GameObject bindTarget = GetBindTarget(id);
            if (bindTarget == null)
                return false;

            if (bindTarget == gameObject)
                return false;

            UnityEngine.Object target = bindTarget;
            if (isComponent)
                target = bindTarget.GetComponent(outputType);

            var oldBinding = pd.GetGenericBinding(trackAsset);
            if (oldBinding != target)
                pd.SetGenericBinding(trackAsset, target);

            return true;
        }

        void OnReferencesChanged()
        {
            _referencesDirty = true;
        }

        public void InstallRuntimeBindings()
        {
            InstallCount++;

            if (bindingData == null)
                return;

            var timelineAsset = playableDirector.playableAsset as TimelineAsset;
            if (timelineAsset == null)
                return;

            var trackByKey = new Dictionary<TrackKey, TrackAsset>();
            var clipsByKey = new Dictionary<TrackKey, List<TimelineClip>>();
            WalkTracks(timelineAsset, (track, key) =>
            {
                trackByKey[key] = track;
                if (track is ControlTrack)
                {
                    var clips = new List<TimelineClip>();
                    foreach (var clip in track.GetClips())
                        clips.Add(clip);
                    clipsByKey[key] = clips;
                }
            });

            foreach (var entry in bindingData.trackBindings)
            {
                if (string.IsNullOrEmpty(entry.id))
                    continue;
                if (!trackByKey.TryGetValue(entry.key, out var trackAsset))
                    continue;
                BindTrack(playableDirector, trackAsset, entry.id);
            }

            foreach (var entry in bindingData.clipBindings)
            {
                if (string.IsNullOrEmpty(entry.id))
                    continue;
                if (!clipsByKey.TryGetValue(entry.trackKey, out var clips))
                    continue;
                if (entry.clipIndex < 0 || entry.clipIndex >= clips.Count)
                    continue;

                GameObject owner = GetBindTarget(entry.id);
                if (owner == null || owner == gameObject)
                    continue;

                var clipAsset = clips[entry.clipIndex].asset as ControlPlayableAsset;
                if (clipAsset == null)
                    continue;

                var current = clipAsset.sourceGameObject.Resolve(playableDirector);
                if (current == owner)
                    continue;

                // SetReferenceValue only takes effect if the graph is rebuilt — save and restore
                // time so the rebuild doesn't reset playback position.
                double savedTime = playableDirector.time;
                playableDirector.SetReferenceValue(clipAsset.sourceGameObject.exposedName, owner);
                playableDirector.RebuildGraph();
                playableDirector.time = savedTime;
            }
        }

        // ------------------------------------------------------------------
        // Editor capture / reconciliation path
        // ------------------------------------------------------------------

#if UNITY_EDITOR
        static bool IsChildOf(Transform child, Transform parent)
        {
            if (parent == null || child == null)
                return false;

            do
            {
                if (child == parent)
                    return true;
            } while (child = child.parent);

            return false;
        }

        // Returns the TimelineReference.Id for owner, stamping a TimelineReference component
        // onto it (or its prefab source) if it doesn't have one yet.
        string GetTimelineId(GameObject owner)
        {
            var timelineRef = owner.GetComponent<TimelineReference>();
            if (timelineRef == null)
            {
                bool hasPrefab = false;

                GameObject ownerPrefab = PrefabUtility.GetCorrespondingObjectFromOriginalSource(owner);
                if (ownerPrefab != null && (ownerPrefab.hideFlags & HideFlags.NotEditable) == 0)
                {
                    hasPrefab = true;
                    timelineRef = ownerPrefab.AddComponent<TimelineReference>();
                    PrefabUtility.SavePrefabAsset(ownerPrefab.transform.root.gameObject);
                }

                if (!hasPrefab)
                {
                    timelineRef = owner.AddComponent<TimelineReference>();
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(owner.scene);
                }
            }
            return timelineRef.Id;
        }

        // Resolves the bound owner GameObject for a track, or null if unbound/self/not a
        // GameObject-or-Component binding.
        GameObject ResolveTrackOwner(PlayableDirector pd, TrackAsset trackAsset)
        {
            var binding = pd.GetGenericBinding(trackAsset);
            var owner = binding as GameObject;
            var comp = binding as Component;
            if (comp != null)
                owner = comp.gameObject;

            if (owner == null || owner == gameObject)
                return null;

            if (IsChildOf(owner.transform, transform))
                return null;

            return owner;
        }

        // Recaptures live bindings from the current scene/timeline state and reconciles them
        // into bindingData: live id != stored id -> update; live null -> no-op (don't write,
        // don't overwrite — target's scene may simply be unloaded); stored key with no live
        // match -> drop (structure changed).
        public void CaptureAndReconcile()
        {
            if (bindingData == null)
                return;

            var timelineAsset = playableDirector.playableAsset as TimelineAsset;
            if (timelineAsset == null)
                return;

            PrefabUtility.RecordPrefabInstancePropertyModifications(this);

            var liveTrackKeys = new HashSet<TrackKey>();
            var liveClipKeys = new HashSet<(TrackKey, int)>();

            WalkTracks(timelineAsset, (track, key) =>
            {
                liveTrackKeys.Add(key);

                var owner = ResolveTrackOwner(playableDirector, track);
                if (owner != null)
                    UpsertTrackBinding(key, GetTimelineId(owner));

                if (track is ControlTrack controlTrack)
                {
                    int clipIndex = -1;
                    foreach (TimelineClip clip in controlTrack.GetClips())
                    {
                        clipIndex++;
                        liveClipKeys.Add((key, clipIndex));

                        var playableAsset = clip.asset as ControlPlayableAsset;
                        if (playableAsset == null)
                            continue;

                        GameObject resolvedObj = playableAsset.sourceGameObject.Resolve(playableDirector);
                        if (resolvedObj == null || resolvedObj == gameObject)
                            continue;

                        if (IsChildOf(resolvedObj.transform, transform))
                            continue;

                        UpsertClipBinding(key, clipIndex, GetTimelineId(resolvedObj));
                    }
                }
            });

            // Drop entries whose structural key no longer exists.
            bindingData.trackBindings.RemoveAll(b => !liveTrackKeys.Contains(b.key));
            bindingData.clipBindings.RemoveAll(b => !liveClipKeys.Contains((b.trackKey, b.clipIndex)));

            EditorUtility.SetDirty(bindingData);
        }

        void UpsertTrackBinding(TrackKey key, string id)
        {
            var existing = bindingData.trackBindings.Find(b => b.key.Equals(key));
            if (existing != null)
                existing.id = id;
            else
                bindingData.trackBindings.Add(new TrackBinding { key = key, id = id });
        }

        void UpsertClipBinding(TrackKey trackKey, int clipIndex, string id)
        {
            var existing = bindingData.clipBindings.Find(b => b.trackKey.Equals(trackKey) && b.clipIndex == clipIndex);
            if (existing != null)
                existing.id = id;
            else
                bindingData.clipBindings.Add(new ClipBinding { trackKey = trackKey, clipIndex = clipIndex, id = id });
        }

        // Adopts freshBindingData (already empty) as the active binding data and re-captures
        // from the current scene state from scratch.
        public void ResetBindings(TimelineBindingData freshBindingData)
        {
            bindingData = freshBindingData;
            if (bindingData == null)
                return;

            CaptureAndReconcile();
            InstallRuntimeBindings();
        }

        void Update()
        {
            if (Application.isPlaying)
                return;

            if (_pendingLoad)
                return;

            CaptureAndReconcile();

            if (_referencesDirty)
            {
                _referencesDirty = false;
                TimelineReference.RegisterAll();
            }

            InstallRuntimeBindings();
        }
#endif

        // Runtime-only: handles deferred reference discovery and external playableAsset swaps.
        void LateUpdate()
        {
#if UNITY_EDITOR
            if (!EditorApplication.isPlaying)
                return;
#endif
            if (_referencesDirty)
            {
                _referencesDirty = false;
                TimelineReference.RegisterAll();
                InstallRuntimeBindings();
            }

            var current = playableDirector.playableAsset;
            if (current == _lastKnownAsset)
                return;

            SetTimeline(current as TimelineAsset, true);
        }
    }
}
