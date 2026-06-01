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
        [SerializeField]
        List<TrackBinding> trackBindings = new List<TrackBinding>();
        [SerializeField]
        List<NestedTimlineBinding> nestedTimelineBindings = new List<NestedTimlineBinding>();
        [SerializeField]
        List<TimelineAssetEntry> timelineEntries = new List<TimelineAssetEntry>();

        PlayableDirector playableDirector;
        Action onComplete;
        Dictionary<string, GameObject> runtimeObjMap = new Dictionary<string, GameObject>();
        List<TimelineReference> timelineReferences = new List<TimelineReference>(10);
        int _lastNestedBindingCount = -1;
#if UNITY_EDITOR
        // Non-serialized — rebuilt each capture pass. If layout shifts, Update detects drift and resets.
        readonly HashSet<int> _selfTrackIndices = new HashSet<int>();
        readonly HashSet<(int track, int clip)> _selfClipIndices = new HashSet<(int, int)>();
#endif

        public event Action<TimelineAsset> OnTimelineChanged;

#if UNITY_EDITOR
        [NonSerialized]
        public bool ActiveInScene;
        public List<TimelineAssetEntry> TimelineEntries => timelineEntries;

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
                InstallRuntimeBindings();
                return;
            }
#endif
            playableDirector = GetComponent<PlayableDirector>();
            playableDirector.stopped += OnPlayableDirectorStopped;
        }

        private void OnDisable()
        {
            if (!Application.isPlaying)
                return;

            runtimeObjMap.Clear();
            playableDirector.stopped -= OnPlayableDirectorStopped;
            onComplete = null;
        }

        public void Play(Action onComplete)
        {
            InstallRuntimeBindings();
            playableDirector.Play();
            this.onComplete = onComplete;
        }

        public void SetTimeline(TimelineAsset asset)
        {
            FlushBindingsToSO(playableDirector.playableAsset as TimelineAsset);
            playableDirector.playableAsset = asset;
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

        // Copies SO → live lists for the given asset (called after swap)
        void LoadBindingsFromSO(TimelineAsset asset)
        {
            trackBindings.Clear();
            nestedTimelineBindings.Clear();
            if (asset == null) return;

            var entry = timelineEntries.Find(e => e.timelineAsset == asset);
            if (entry?.bindingData == null) return;

            foreach (var b in entry.bindingData.trackBindings)
                trackBindings.Add(new TrackBinding { trackIndex = b.trackIndex, id = b.id });

            foreach (var nb in entry.bindingData.nestedTimelineBindings)
                nestedTimelineBindings.Add(nb);
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
        public void ResetActiveBindings()
        {
            var asset = playableDirector.playableAsset as TimelineAsset;

            trackBindings.Clear();
            nestedTimelineBindings.Clear();

            var entry = timelineEntries.Find(e => e.timelineAsset == asset);
            if (entry?.bindingData != null)
            {
                entry.bindingData.trackBindings.Clear();
                entry.bindingData.nestedTimelineBindings.Clear();
                EditorUtility.SetDirty(entry.bindingData);
            }

            UpdateBindingList(playableDirector, trackBindings, false);
            UpdateNestedTimelineBindingList(playableDirector, nestedTimelineBindings);
            FlushBindingsToSO(asset);
            InstallRuntimeBindings();

            EditorUtility.SetDirty(this);
        }

        void Update()
        {
            if (Application.isPlaying)
                return;

            if (!ActiveInScene)
                return;

            if (SelfReferenceLayoutDrifted())
            {
                ResetActiveBindings();
                return;
            }

            UpdateBindingList(playableDirector, trackBindings, false);
            UpdateNestedTimelineBindingList(playableDirector, nestedTimelineBindings);
            FlushBindingsToSO(playableDirector.playableAsset as TimelineAsset);
            InstallRuntimeBindings();
        }
#endif

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

        public void UpdateBindingList(PlayableDirector pd, List<TrackBinding> trackBindings, bool includeChildObject)
        {
            var timelineAsset = pd.playableAsset as TimelineAsset;
            if (timelineAsset == null)
                return;

            PrefabUtility.RecordPrefabInstancePropertyModifications(this);

            if (!includeChildObject)
                _selfTrackIndices.Clear();

            for (int i = 0; i < timelineAsset.outputTrackCount; i++)
            {
                TrackAsset trackAsset = timelineAsset.GetOutputTrack(i);
                if (trackAsset == null)
                    continue;

                // GroupTrack is a container with no binding — skip it.
                if (trackAsset is GroupTrack)
                    continue;

                var owner = pd.GetGenericBinding(trackAsset) as GameObject;
                var comp = pd.GetGenericBinding(trackAsset) as Component;
                if (comp != null)
                    owner = comp.gameObject;

                if (owner == null)
                {
                    MergeRule(trackBindings, i);
                    continue;
                }

                if (!includeChildObject && IsChildOf(owner.transform, pd.transform))
                {
                    _selfTrackIndices.Add(i);
                    continue;
                }

                var guid = GetTimelineId(owner);
                var existing = trackBindings.FindIndex(b => b.trackIndex == i);
                if (existing >= 0)
                    trackBindings[existing] = new TrackBinding() { trackIndex = i, id = guid };
                else
                    trackBindings.Add(new TrackBinding() { trackIndex = i, id = guid });
            }
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

        // Returns true if any previously recorded self-referencing track or clip no longer resolves to self.
        // A layout change (reorder, add, remove) shifts indices and makes stored bindings invalid.
        bool SelfReferenceLayoutDrifted()
        {
            var timelineAsset = playableDirector.playableAsset as TimelineAsset;
            if (timelineAsset == null) return false;

            foreach (int trackIndex in _selfTrackIndices)
            {
                if (trackIndex >= timelineAsset.outputTrackCount) return true;
                var binding = playableDirector.GetGenericBinding(timelineAsset.GetOutputTrack(trackIndex));
                var owner = binding as GameObject;
                var comp = binding as Component;
                if (comp != null) owner = comp.gameObject;
                if (owner == null) return true;
                if (!IsChildOf(owner.transform, transform)) return true;
            }

            foreach (var (trackIndex, clipIndex) in _selfClipIndices)
            {
                if (trackIndex >= timelineAsset.outputTrackCount) return true;
                var controlTrack = timelineAsset.GetOutputTrack(trackIndex) as ControlTrack;
                if (controlTrack == null) return true;
                int ci = -1;
                foreach (var clip in controlTrack.GetClips())
                {
                    ci++;
                    if (ci == clipIndex)
                    {
                        var resolved = (clip.asset as ControlPlayableAsset)?.sourceGameObject.Resolve(playableDirector);
                        if (ClassifyNestedOwner(resolved) != NestedOwnerResolution.Self) return true;
                        break;
                    }
                }
            }

            return false;
        }

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

        void MergeRule(List<TrackBinding> list, int trackIndex)
        {
            if (!additiveSceneWorkflow)
            {
                var stale = list.FindIndex(b => b.trackIndex == trackIndex);
                if (stale >= 0) list.RemoveAt(stale);
            }
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

            if (nestedTimelineBindings.Count != _lastNestedBindingCount)
            {
                _lastNestedBindingCount = nestedTimelineBindings.Count;
                var sb = new System.Text.StringBuilder();
                sb.AppendFormat("[TC] nestedTimelineBindings count changed to {0}:\n", nestedTimelineBindings.Count);
                foreach (var b in nestedTimelineBindings)
                    sb.AppendFormat("  track={0} clip={1} id={2} asset={3}\n", b.trackIndex, b.clipIndex, b.id, b.timelineAsset);
                Debug.Log(sb.ToString());
            }

            for (int i = 0; i < nestedTimelineBindings.Count; i++)
            {
                NestedTimlineBinding entry = nestedTimelineBindings[i];
                if (entry.timelineAsset == null || string.IsNullOrEmpty(entry.id))
                    continue;

                var resolution = ResolveNestedOwner(entry.id, out GameObject owner);
                if (resolution == NestedOwnerResolution.Self)
                    Debug.LogWarningFormat("[TC] Self entry survived to install — track={0} clip={1} id={2}", entry.trackIndex, entry.clipIndex, entry.id);
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

                playableDirector.SetReferenceValue(clipAsset.sourceGameObject.exposedName, owner);
                PlayableDirector nestedDirector = owner.GetComponent<PlayableDirector>();
                nestedDirector.playableAsset = entry.timelineAsset;

                foreach (var binding in entry.nestedTimelineTrackBindings)
                {
                    if (string.IsNullOrEmpty(binding.id))
                        Debug.LogWarningFormat("Bind child timeline failed, empty id: {0}, {1}",
                            nestedDirector.playableAsset.ToString(), playableDirector.playableAsset.ToString());
                    else
                        BindTrack(nestedDirector, binding);
                }

                nestedDirector.RebuildGraph();
            }
        }
    }
}
