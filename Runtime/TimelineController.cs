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
    public class TrackBinding
    {
        public int trackIndex;
        public string id;
    }

    [Serializable]
    public class NestedTimlineBinding
    {
        public int trackIndex;
        public int clipIndex;
        public string id;
        public List<TrackBinding> nestedTimelineTrackBindings;
        public PlayableAsset timelineAsset;
    }

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
        bool additiveSceneWorkflow = true;
        List<TrackBinding> trackBindings = new List<TrackBinding>();
        List<NestedTimlineBinding> nestedTimelineBindings = new List<NestedTimlineBinding>();
        [SerializeField]
        List<TimelineAssetEntry> timelineEntries = new List<TimelineAssetEntry>();

        PlayableDirector playableDirector;
        Action onComplete;
        Dictionary<string, GameObject> runtimeObjMap = new Dictionary<string, GameObject>();
        List<TimelineReference> timelineReferences = new List<TimelineReference>(10);
        PlayableAsset _lastKnownAsset;
#if UNITY_EDITOR
        // Non-serialized — rebuilt each capture pass.
        readonly HashSet<(int track, int clip)> _selfClipIndices = new HashSet<(int, int)>();
        bool _bindingsDirty = true;
#endif

        public event Action<TimelineAsset> OnTimelineChanged;

#if UNITY_EDITOR
        [NonSerialized]
        public bool ActiveInScene;
        public List<TimelineAssetEntry> TimelineEntries => timelineEntries;
        public IReadOnlyList<TrackBinding> TrackBindings => trackBindings;
        public IReadOnlyList<NestedTimlineBinding> NestedTimelineBindings => nestedTimelineBindings;

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

                ActiveInScene = true;
                playableDirector = GetComponent<PlayableDirector>();
                ObjectChangeEvents.changesPublished += OnObjectChangesPublished;
                TimelineReference.OnRegistered += InstallRuntimeBindings;
                // Both playableAsset and the bindingData sub-asset reference inside timelineEntries
                // are cross-asset references that Unity resolves asynchronously after OnEnable.
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
                    if (entry?.bindingData != null)
                        LoadBindingsFromSO(entry);
                    _bindingsDirty = true;
                    InstallRuntimeBindings();
                };
                EditorApplication.delayCall += tryLoad;
                return;
            }
#endif
            playableDirector = GetComponent<PlayableDirector>();
            playableDirector.stopped += OnPlayableDirectorStopped;
            TimelineReference.OnRegistered += InstallRuntimeBindings;
        }

        private void Start()
        {
#if UNITY_EDITOR
            if (!EditorApplication.isPlaying)
                return;
#endif
            // All Awakes have fired — IdMap is fully populated. Safe to install bindings.
            _lastKnownAsset = playableDirector.playableAsset;
            LoadBindingsFromSO(playableDirector.playableAsset as TimelineAsset);
            InstallRuntimeBindings();
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                ObjectChangeEvents.changesPublished -= OnObjectChangesPublished;
                TimelineReference.OnRegistered -= InstallRuntimeBindings;
            }
#endif
            if (!Application.isPlaying)
                return;

            runtimeObjMap.Clear();
            playableDirector.stopped -= OnPlayableDirectorStopped;
            TimelineReference.OnRegistered -= InstallRuntimeBindings;
            onComplete = null;
        }

        public void Play(Action onComplete)
        {
            InstallRuntimeBindings();
            playableDirector.Play();
            this.onComplete = onComplete;
        }

        public void SetTimeline(TimelineAsset asset, bool playableAssigned = false)
        {
#if UNITY_EDITOR
            if (!EditorApplication.isPlaying)
                FlushBindingsToSO(playableDirector.playableAsset as TimelineAsset);
            _bindingsDirty = true;
#endif
            if (!playableAssigned)
            {
                playableDirector.playableAsset = asset;
            }
            LoadBindingsFromSO(asset);
            InstallRuntimeBindings();
            OnTimelineChanged?.Invoke(asset);
        }

        // Copies live lists → SO for the given asset (called before swap or on capture)
        void FlushBindingsToSO(TimelineAsset asset)
        {
            if (asset == null) return;
            var entry = timelineEntries.Find(e => e.timelineAsset == asset);
            if (entry?.bindingData == null) return;

            entry.bindingData.trackBindings.Clear();
            foreach (var b in trackBindings)
                entry.bindingData.trackBindings.Add(new TrackBinding { trackIndex = b.trackIndex, id = b.id });

            entry.bindingData.nestedTimelineBindings.Clear();
            foreach (var nb in nestedTimelineBindings)
                entry.bindingData.nestedTimelineBindings.Add(nb);

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(entry.bindingData);
#endif
        }

        // Copies SO → live lists for the given asset.
        void LoadBindingsFromSO(TimelineAsset asset)
        {
            if (asset == null) return;
            var entry = timelineEntries.Find(e => e.timelineAsset == asset);
            if (entry?.bindingData == null) return;
            LoadBindingsFromSO(entry);
        }

        void LoadBindingsFromSO(TimelineAssetEntry entry)
        {
            trackBindings.Clear();
            nestedTimelineBindings.Clear();
            trackBindings.AddRange(entry.bindingData.trackBindings);
            nestedTimelineBindings.AddRange(entry.bindingData.nestedTimelineBindings);
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

#if UNITY_EDITOR
        static void AddGroupTrackIds(IEnumerable<TrackAsset> tracks, HashSet<int> ids)
        {
            foreach (var track in tracks)
            {
                if (track is GroupTrack gt)
                {
                    ids.Add(gt.GetInstanceID());
                    AddGroupTrackIds(gt.GetChildTracks(), ids);
                }
            }
        }

        void OnObjectChangesPublished(ref ObjectChangeEventStream stream)
        {
            var timelineAsset = playableDirector?.playableAsset as TimelineAsset;
            if (timelineAsset == null) return;

            var watchedIds = new HashSet<int>();
            watchedIds.Add(timelineAsset.GetInstanceID());
            for (int i = 0; i < timelineAsset.outputTrackCount; i++)
            {
                var track = timelineAsset.GetOutputTrack(i);
                if (track != null) watchedIds.Add(track.GetInstanceID());
            }
            AddGroupTrackIds(timelineAsset.GetRootTracks(), watchedIds);

            for (int i = 0; i < stream.length; i++)
            {
                var kind = stream.GetEventType(i);
                switch (kind)
                {
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var propEvent);
                        if (watchedIds.Contains(propEvent.instanceId)) { _bindingsDirty = true; return; }
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        stream.GetChangeGameObjectStructureHierarchyEvent(i, out var hierEvent);
                        if (watchedIds.Contains(hierEvent.instanceId)) { _bindingsDirty = true; return; }
                        break;
                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                        stream.GetDestroyGameObjectHierarchyEvent(i, out var destroyEvent);
                        if (watchedIds.Contains(destroyEvent.instanceId)) { _bindingsDirty = true; return; }
                        break;
                    case ObjectChangeKind.DestroyAssetObject:
                        stream.GetDestroyAssetObjectEvent(i, out var destroyAssetEvent);
                        if (watchedIds.Contains(destroyAssetEvent.instanceId)) { _bindingsDirty = true; return; }
                        break;
                    case ObjectChangeKind.ChangeRootOrder:
                        stream.GetChangeRootOrderEvent(i, out var rootOrderEvent);
                        if (watchedIds.Contains(rootOrderEvent.instanceId)) { _bindingsDirty = true; return; }
                        break;
                    case ObjectChangeKind.ChangeAssetObjectProperties:
                        stream.GetChangeAssetObjectPropertiesEvent(i, out var assetEvent);
                        if (watchedIds.Contains(assetEvent.instanceId)) { _bindingsDirty = true; return; }
                        break;
                    case ObjectChangeKind.ChangeScene:
                        _bindingsDirty = true;
                        return;
                }
            }
        }

        // freshBindingData: if provided, replaces the entry's SO before re-capture (editor passes a newly created one)
        public void ResetActiveBindings(TimelineBindingData freshBindingData = null)
        {
            var asset = playableDirector.playableAsset as TimelineAsset;
            var entry = timelineEntries.Find(e => e.timelineAsset == asset);

            if (freshBindingData != null && entry != null)
                entry.bindingData = freshBindingData;

            trackBindings.Clear();
            nestedTimelineBindings.Clear();

            UpdateBindingList(playableDirector, trackBindings, false);
            UpdateNestedTimelineBindingList(playableDirector, nestedTimelineBindings);
            FlushBindingsToSO(asset);
            InstallRuntimeBindings();

            EditorUtility.SetDirty(this);
            if (entry?.bindingData != null)
                EditorUtility.SetDirty(entry.bindingData);
        }

        void Update()
        {
            if (Application.isPlaying)
                return;

            if (!ActiveInScene)
                return;
            if (_bindingsDirty)
            {
                _bindingsDirty = false;
                UpdateBindingList(playableDirector, trackBindings, false);
                UpdateNestedTimelineBindingList(playableDirector, nestedTimelineBindings);
                FlushBindingsToSO(playableDirector.playableAsset as TimelineAsset);
                EditorUtility.SetDirty(this);
            }

            InstallRuntimeBindings();
        }
#endif

        // Runtime-only: detects external playableAsset swaps (e.g. direct director.playableAsset assignment)
        // and reacts the same way SetTimeline would.
        void LateUpdate()
        {
#if UNITY_EDITOR
            if (!EditorApplication.isPlaying)
                return;
#endif
            var current = playableDirector.playableAsset;
            if (current == _lastKnownAsset)
                return;

            _lastKnownAsset = current;
            SetTimeline(current as TimelineAsset, true);
        }

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

        // Rebuilds trackBindings from scratch using GetOutputTrack indices.
        // Live bindings are captured at their current index.
        // Empty slots: if additiveSceneWorkflow, a previously stored GUID is preserved at the new index
        // only if that GUID wasn't already captured at another index (i.e. it's genuinely unloaded, not moved).
        public bool UpdateBindingList(PlayableDirector pd, List<TrackBinding> trackBindings, bool includeChildObject)
        {
            var timelineAsset = pd.playableAsset as TimelineAsset;
            if (timelineAsset == null)
                return false;

            // Previous entries keyed by index, for unloaded-scene slot preservation.
            var previous = new Dictionary<int, string>(trackBindings.Count);
            foreach (var b in trackBindings)
                previous[b.trackIndex] = b.id;

            PrefabUtility.RecordPrefabInstancePropertyModifications(this);

            var next = new List<TrackBinding>(timelineAsset.outputTrackCount);
            // Track which GUIDs we captured live so we don't also preserve them as unloaded.
            var capturedGuids = new HashSet<string>();

            // First pass: capture all live bindings.
            for (int i = 0; i < timelineAsset.outputTrackCount; i++)
            {
                TrackAsset trackAsset = timelineAsset.GetOutputTrack(i);
                if (trackAsset == null)
                    continue;

                var binding = pd.GetGenericBinding(trackAsset);
                var owner = binding as GameObject;
                var comp = binding as Component;
                if (comp != null)
                    owner = comp.gameObject;

                if (owner == null)
                    continue;

                if (!includeChildObject && IsChildOf(owner.transform, pd.transform))
                    continue;

                var guid = GetTimelineId(owner);
                next.Add(new TrackBinding { trackIndex = i, id = guid });
                capturedGuids.Add(guid);
            }

            // Second pass: preserve unloaded-scene GUIDs for empty slots, skipping any already captured.
            if (additiveSceneWorkflow)
            {
                for (int i = 0; i < timelineAsset.outputTrackCount; i++)
                {
                    TrackAsset trackAsset = timelineAsset.GetOutputTrack(i);
                    if (trackAsset == null)
                        continue;

                    var binding = pd.GetGenericBinding(trackAsset);
                    var owner = binding as GameObject;
                    var comp = binding as Component;
                    if (comp != null)
                        owner = comp.gameObject;

                    if (owner != null)
                        continue;

                    if (previous.TryGetValue(i, out var preserved) && !capturedGuids.Contains(preserved))
                        next.Add(new TrackBinding { trackIndex = i, id = preserved });
                }
            }

            trackBindings.Clear();
            trackBindings.AddRange(next);

            return false;
        }

        void UpdateNestedTimelineBindingList(PlayableDirector pd, List<NestedTimlineBinding> nestedTimelineBindings)
        {
            var timelineAsset = pd.playableAsset as TimelineAsset;
            if (timelineAsset == null)
                return;

            _selfClipIndices.Clear();

            for (int trackIndex = 0; trackIndex < timelineAsset.outputTrackCount; trackIndex++)
            {
                TrackAsset trackAsset = timelineAsset.GetOutputTrack(trackIndex);
                ControlTrack controlTrack = trackAsset as ControlTrack;
                if (controlTrack == null)
                    continue;

                int clipIndex = -1;
                foreach (TimelineClip clip in controlTrack.GetClips())
                {
                    clipIndex++;
                    ControlPlayableAsset playableAsset = (ControlPlayableAsset)clip.asset;
                    GameObject resolvedObj = playableAsset.sourceGameObject.Resolve(pd);

                    switch (ClassifyNestedOwner(resolvedObj))
                    {
                        case NestedOwnerResolution.Missing:
                            MergeRule(nestedTimelineBindings, trackIndex, clipIndex);
                            continue;
                        case NestedOwnerResolution.Self:
                            _selfClipIndices.Add((trackIndex, clipIndex));
                            continue;
                    }

                    PlayableDirector resolvedDirector = resolvedObj.GetComponent<PlayableDirector>();
                    if (resolvedDirector == null)
                        continue;

                    if (IsChildOf(resolvedObj.transform, transform))
                        continue;

                    var timelineRef = resolvedObj.GetComponent<TimelineReference>();
                    if (timelineRef == null)
                    {
                        bool hasPrefab = false;
                        GameObject resolvedObjInPrefab = PrefabUtility.GetCorrespondingObjectFromOriginalSource(resolvedObj);
                        if (resolvedObjInPrefab != null)
                        {
                            hasPrefab = true;
                            timelineRef = resolvedObjInPrefab.AddComponent<TimelineReference>();
                            PrefabUtility.SavePrefabAsset(resolvedObjInPrefab.transform.root.gameObject);
                        }

                        if (!hasPrefab)
                        {
                            timelineRef = resolvedObj.AddComponent<TimelineReference>();
                            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(resolvedObj.scene);
                        }
                    }

                    List<TrackBinding> nestedTrackBindings = new List<TrackBinding>();
                    int existingIndex = nestedTimelineBindings.FindIndex(b => b.trackIndex == trackIndex && b.clipIndex == clipIndex);
                    if (existingIndex >= 0)
                        nestedTrackBindings = nestedTimelineBindings[existingIndex].nestedTimelineTrackBindings;

                    UpdateBindingList(resolvedDirector, nestedTrackBindings, true);

                    var entry = new NestedTimlineBinding()
                    {
                        trackIndex = trackIndex,
                        clipIndex = clipIndex,
                        id = timelineRef.Id,
                        timelineAsset = resolvedDirector.playableAsset,
                        nestedTimelineTrackBindings = nestedTrackBindings,
                    };

                    if (existingIndex >= 0)
                        nestedTimelineBindings[existingIndex] = entry;
                    else
                        nestedTimelineBindings.Add(entry);
                }
            }
        }
#endif

        enum NestedOwnerResolution { Missing, Self, Resolved }

        // Resolves a nested owner from the IdMap (install path).
        // Self means the owner is this GameObject — always present, Unity handles it natively.
        NestedOwnerResolution ResolveNestedOwner(string id, out GameObject owner)
        {
            owner = GetBindTarget(id);
            if (owner == null) return NestedOwnerResolution.Missing;
            if (owner == gameObject) return NestedOwnerResolution.Self;
            return NestedOwnerResolution.Resolved;
        }

        // Classifies an already-resolved GameObject (capture path).
        NestedOwnerResolution ClassifyNestedOwner(GameObject resolvedObj)
        {
            if (resolvedObj == null) return NestedOwnerResolution.Missing;
            if (resolvedObj == gameObject) return NestedOwnerResolution.Self;
            return NestedOwnerResolution.Resolved;
        }

        void MergeRule(List<NestedTimlineBinding> list, int trackIndex, int clipIndex)
        {
            if (!additiveSceneWorkflow)
            {
                var stale = list.FindIndex(b => b.trackIndex == trackIndex && b.clipIndex == clipIndex);
                if (stale >= 0) list.RemoveAt(stale);
            }
        }

        GameObject GetBindTarget(string id)
        {
            if (runtimeObjMap.TryGetValue(id, out var bindTarget))
                return bindTarget;

            if (!TimelineReference.IdMap.TryGetValue(id, out var instances))
                return null;

            return instances.Count == 0 ? null : instances[0];
        }

        bool BindTrack(PlayableDirector pd, TrackBinding binding)
        {
            TimelineAsset timelineAsset = pd.playableAsset as TimelineAsset;

            if (binding.trackIndex >= timelineAsset.outputTrackCount)
            {
                Debug.LogWarningFormat("trackIndex out of bounds:{0}, {1}", timelineAsset.ToString(), binding.trackIndex);
                return false;
            }
            TrackAsset trackAsset = timelineAsset.GetOutputTrack(binding.trackIndex);

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

            GameObject bindTarget = GetBindTarget(binding.id);
            if (bindTarget == null)
            {
                Debug.LogWarningFormat("Bind failed, didn't find bind object: {0}, {1}, {2}", timelineAsset.ToString(), trackAsset.ToString(), binding.id);
                return false;
            }

            UnityEngine.Object target = bindTarget;
            if (isComponent)
                target = bindTarget.GetComponent(outputType);

            var oldBinding = pd.GetGenericBinding(trackAsset);
            if (oldBinding != target)
                pd.SetGenericBinding(trackAsset, target);

            return true;
        }

        public void InstallRuntimeBindings()
        {
            foreach (var entry in trackBindings)
            {
                if (string.IsNullOrEmpty(entry.id))
                    continue;
                if (ResolveNestedOwner(entry.id, out _) == NestedOwnerResolution.Self)
                    continue;
                BindTrack(playableDirector, entry);
            }

            for (int i = 0; i < nestedTimelineBindings.Count; i++)
            {
                NestedTimlineBinding entry = nestedTimelineBindings[i];
                if (entry.timelineAsset == null || string.IsNullOrEmpty(entry.id))
                    continue;

                var resolution = ResolveNestedOwner(entry.id, out GameObject owner);
                if (resolution != NestedOwnerResolution.Resolved)
                    continue;

                TimelineAsset timelineAsset = playableDirector.playableAsset as TimelineAsset;
                if (entry.trackIndex >= timelineAsset.outputTrackCount)
                {
                    Debug.LogWarningFormat("trackIndex out of bounds: {0}, {1}", timelineAsset.ToString(), entry.trackIndex);
                    continue;
                }
                TrackAsset trackAsset = timelineAsset.GetOutputTrack(entry.trackIndex);
                int clipIndex = -1;
                ControlPlayableAsset clipAsset = null;
                foreach (var clip in trackAsset.GetClips())
                {
                    clipIndex++;
                    if (clipIndex == entry.clipIndex)
                    {
                        clipAsset = clip.asset as ControlPlayableAsset;
                        break;
                    }
                }

                if (clipAsset == null)
                {
                    Debug.LogWarningFormat("NestedTimeline: no ControlPlayableAsset at track {0} clip {1} in {2}", entry.trackIndex, entry.clipIndex, timelineAsset);
                    continue;
                }

                PlayableDirector nestedDirector = owner.GetComponent<PlayableDirector>();

                if (nestedDirector.playableAsset != entry.timelineAsset)
                    nestedDirector.playableAsset = entry.timelineAsset;

                foreach (var binding in entry.nestedTimelineTrackBindings)
                {
                    if (string.IsNullOrEmpty(binding.id))
                        Debug.LogWarningFormat("Bind child timeline failed, empty id: {0}, {1}",
                            nestedDirector.playableAsset.ToString(), playableDirector.playableAsset.ToString());
                    else
                        BindTrack(nestedDirector, binding);
                }

                // SetReferenceValue only takes effect if the graph is rebuilt — save and restore time to avoid resetting A.
                double savedTime = playableDirector.time;
                playableDirector.SetReferenceValue(clipAsset.sourceGameObject.exposedName, owner);
                playableDirector.RebuildGraph();
                playableDirector.time = savedTime;
            }
        }
    }
}
