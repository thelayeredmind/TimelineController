using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace TLM.TimelineController
{
    [CustomEditor(typeof(TimelineController))]
    public class TimelineControllerEditor : Editor
    {
        TimelineController timelineController;
        PlayableDirector director;

        private void OnEnable()
        {
            timelineController = serializedObject.targetObject as TimelineController;
            director = timelineController.GetComponent<PlayableDirector>();
        }

        public override bool RequiresConstantRepaint() => true;

        public override void OnInspectorGUI()
        {
            if (timelineController.gameObject.scene == null || !timelineController.gameObject.scene.isLoaded)
            {
                using (new EditorGUI.DisabledGroupScope(true))
                    base.OnInspectorGUI();
                return;
            }

            EditorGUILayout.Space();

            var currentAsset = director.playableAsset as TimelineAsset;
            var entries = timelineController.TimelineEntries;

            // --- Timeline entry navigator ---
            EditorGUILayout.LabelField("Timeline Assets", EditorStyles.boldLabel);

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                bool isActive = entry.timelineAsset == currentAsset;
                string label = entry.timelineAsset != null ? entry.timelineAsset.name : "(none)";

                EditorGUILayout.BeginHorizontal();

                using (new EditorGUI.DisabledGroupScope(isActive))
                {
                    if (GUILayout.Button(isActive ? $"[ {label} ]" : label))
                    {
                        Undo.RecordObject(director, "Switch Timeline Asset");
                        Undo.RecordObject(timelineController, "Switch Timeline Asset");
                        timelineController.SetTimeline(entry.timelineAsset);
                        EditorUtility.SetDirty(director);
                        EditorUtility.SetDirty(timelineController);
                    }
                }

                if (GUILayout.Button("✕", GUILayout.Width(22)))
                {
                    Undo.RecordObject(timelineController, "Remove Timeline Entry");
                    entries.RemoveAt(i);
                    EditorUtility.SetDirty(timelineController);
                    break;
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space();

            // --- Add entry from current director asset ---
            if (GUILayout.Button("Add Current Asset"))
            {
                if (currentAsset == null)
                {
                    EditorUtility.DisplayDialog("No Asset", "Assign a TimelineAsset to the PlayableDirector first.", "OK");
                }
                else if (entries.Exists(e => e.timelineAsset == currentAsset))
                {
                    EditorUtility.DisplayDialog("Already Added", $"{currentAsset.name} is already in the list.", "OK");
                }
                else
                {
                    Undo.RecordObject(timelineController, "Add Timeline Entry");
                    var data = CreateBindingDataAsset(currentAsset);
                    entries.Add(new TimelineAssetEntry { timelineAsset = currentAsset, bindingData = data });
                    EditorUtility.SetDirty(timelineController);
                    timelineController.SetTimeline(currentAsset, true);
                }
            }

            EditorGUILayout.Space();

            var bindingData = timelineController.BindingData;
            if (bindingData == null)
            {
                var activeEntryMissing = entries.Find(e => e.timelineAsset == currentAsset);
                if (activeEntryMissing != null)
                {
                    EditorGUILayout.HelpBox("Binding Data reference is missing or broken for this timeline asset.", MessageType.Warning);
                    if (GUILayout.Button("Repair Binding Data"))
                    {
                        Undo.RecordObject(timelineController, "Repair Timeline Binding Data");
                        var freshData = CreateBindingDataAsset(currentAsset);
                        activeEntryMissing.bindingData = freshData;
                        EditorUtility.SetDirty(timelineController);
                        timelineController.ResetBindings(freshData);
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("No Binding Data for the active timeline asset. Use \"Add Current Asset\" above.", MessageType.Warning);
                }
                EditorGUILayout.Space();
                using (new EditorGUI.DisabledGroupScope(true))
                    base.OnInspectorGUI();
                return;
            }

            // --- Reset bindings ---
            var prevColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.35f, 0.35f);
            if (GUILayout.Button("Reset Bindings"))
            {
                GUI.backgroundColor = prevColor;
                if (EditorUtility.DisplayDialog(
                    "Reset Bindings",
                    "This will destroy ALL existing BindingData sub-assets for this timeline, create a fresh one, and re-capture bindings from the current scene state.\n\nThis cannot be undone.",
                    "Reset", "Cancel"))
                {
                    Undo.RecordObject(timelineController, "Reset Timeline Bindings");
                    var freshData = CreateBindingDataAsset(currentAsset);
                    var activeEntry = entries.Find(e => e.timelineAsset == currentAsset);
                    if (activeEntry != null)
                        activeEntry.bindingData = freshData;
                    EditorUtility.SetDirty(timelineController);
                    timelineController.ResetBindings(freshData);
                }
            }
            GUI.backgroundColor = prevColor;

            EditorGUILayout.Space();

            // --- Save prefab overrides ---
            GameObject prefab = PrefabUtility.GetCorrespondingObjectFromOriginalSource(timelineController.gameObject);
            if (prefab && PrefabUtility.HasPrefabInstanceAnyOverrides(timelineController.gameObject, false))
            {
                if (GUILayout.Button("Save Prefab"))
                    PrefabUtility.ApplyPrefabInstance(timelineController.gameObject, InteractionMode.AutomatedAction);
            }

            EditorGUILayout.Space();

            // --- SO contents (ground truth) — green if the binding currently resolves to a
            // live object, red if its key exists but the target doesn't resolve right now
            // (could be unloaded, or genuinely missing). ---
            EditorGUILayout.LabelField("Track Bindings", EditorStyles.boldLabel);
            if (bindingData.trackBindings.Count == 0)
            {
                EditorGUILayout.HelpBox("No track bindings.", MessageType.None);
            }
            else
            {
                foreach (var b in bindingData.trackBindings)
                {
                    bool resolved = TimelineReference.IdMap.TryGetValue(b.id, out var instances) && instances.Count > 0;
                    DrawBindingRow($"  {DescribeKey(b.key)}", b.id, resolved);
                }
            }

            if (bindingData.clipBindings.Count > 0)
            {
                EditorGUILayout.LabelField("Clip Bindings", EditorStyles.boldLabel);
                foreach (var cb in bindingData.clipBindings)
                {
                    bool resolved = TimelineReference.IdMap.TryGetValue(cb.id, out var instances) && instances.Count > 0;
                    DrawBindingRow($"  {DescribeKey(cb.trackKey)} clip {cb.clipIndex}", cb.id, resolved);
                }
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledGroupScope(true))
                base.OnInspectorGUI();
        }

        static string DescribeKey(TrackKey key)
        {
            string path = string.IsNullOrEmpty(key.groupPath) ? key.trackName : $"{key.groupPath}/{key.trackName}";
            return key.occurrence == 0 ? path : $"{path} #{key.occurrence}";
        }

        static readonly Color ResolvedColor = new Color(0.4f, 1f, 0.4f);
        static readonly Color UnresolvedColor = new Color(1f, 0.35f, 0.35f);
        static GUIStyle _resolvedStyle;
        static GUIStyle _unresolvedStyle;

        static void DrawBindingRow(string label, string id, bool resolved)
        {
            if (_resolvedStyle == null)
            {
                _resolvedStyle = new GUIStyle(EditorStyles.label);
                _resolvedStyle.normal.textColor = ResolvedColor;
                _resolvedStyle.onNormal.textColor = ResolvedColor;

                _unresolvedStyle = new GUIStyle(EditorStyles.label);
                _unresolvedStyle.normal.textColor = UnresolvedColor;
                _unresolvedStyle.onNormal.textColor = UnresolvedColor;
            }

            var style = resolved ? _resolvedStyle : _unresolvedStyle;
            EditorGUILayout.LabelField(label, id, style);
        }

        // Destroys any existing TimelineBindingData sub-assets inside timelineAsset, then
        // creates and returns a single fresh one. Prevents duplicate BindingData sub-assets
        // from accumulating if this is called more than once for the same asset.
        static TimelineBindingData CreateBindingDataAsset(TimelineAsset timelineAsset)
        {
            var path = AssetDatabase.GetAssetPath(timelineAsset);
            foreach (var existing in AssetDatabase.LoadAllAssetsAtPath(path).OfType<TimelineBindingData>().ToList())
            {
                AssetDatabase.RemoveObjectFromAsset(existing);
                DestroyImmediate(existing, true);
            }

            var data = ScriptableObject.CreateInstance<TimelineBindingData>();
            data.name = "BindingData";
            AssetDatabase.AddObjectToAsset(data, timelineAsset);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);
            // ImportAsset can reload/reinstantiate sub-assets, invalidating `data` — re-fetch
            // the persisted instance so the reference we return/assign is the live one.
            return AssetDatabase.LoadAllAssetsAtPath(path).OfType<TimelineBindingData>().FirstOrDefault(d => d.name == "BindingData");
        }
    }
}
