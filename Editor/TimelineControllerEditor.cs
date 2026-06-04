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

        public override void OnInspectorGUI()
        {
            if (timelineController.gameObject.scene == null || !timelineController.gameObject.scene.isLoaded)
            {
                using (new EditorGUI.DisabledGroupScope(true))
                    base.OnInspectorGUI();
                return;
            }

            EditorGUILayout.Space();

            // --- Timeline entry navigator ---
            EditorGUILayout.LabelField("Timeline Assets", EditorStyles.boldLabel);

            var entries = timelineController.TimelineEntries;
            var currentAsset = director.playableAsset as TimelineAsset;

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
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Current Asset"))
            {
                var asset = director.playableAsset as TimelineAsset;
                if (asset == null)
                {
                    EditorUtility.DisplayDialog("No Asset", "Assign a TimelineAsset to the PlayableDirector first.", "OK");
                }
                else if (entries.Exists(e => e.timelineAsset == asset))
                {
                    EditorUtility.DisplayDialog("Already Added", $"{asset.name} is already in the list.", "OK");
                }
                else
                {
                    Undo.RecordObject(timelineController, "Add Timeline Entry");
                    var bindingData = CreateBindingDataAsset(asset);
                    entries.Add(new TimelineAssetEntry { timelineAsset = asset, bindingData = bindingData });
                    EditorUtility.SetDirty(timelineController);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            // --- Reset bindings ---
            var prevColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.35f, 0.35f);
            if (GUILayout.Button("Reset Bindings for Current Timeline"))
            {
                GUI.backgroundColor = prevColor;
                var asset = director.playableAsset as TimelineAsset;
                string assetName = asset != null ? asset.name : "(none)";
                if (EditorUtility.DisplayDialog(
                    "Reset Bindings",
                    $"This will destroy ALL existing BindingData assets for \"{assetName}\", create a fresh one, and re-capture bindings from the current scene state. Stale entries will be discarded.\n\nThis cannot be undone.",
                    "Reset", "Cancel"))
                {
                    Undo.RecordObject(timelineController, "Reset Timeline Bindings");
                    var freshData = asset != null ? RebuildBindingDataAsset(asset) : null;
                    timelineController.ResetActiveBindings(freshData);
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
            using (new EditorGUI.DisabledGroupScope(true))
                base.OnInspectorGUI();
        }

        static TimelineBindingData CreateBindingDataAsset(TimelineAsset timelineAsset)
        {
            var data = ScriptableObject.CreateInstance<TimelineBindingData>();
            data.name = "BindingData";
            AssetDatabase.AddObjectToAsset(data, timelineAsset);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(timelineAsset));
            return data;
        }

        // Destroys all existing TimelineBindingData sub-assets inside the .playable file,
        // then creates and returns a single fresh one.
        static TimelineBindingData RebuildBindingDataAsset(TimelineAsset timelineAsset)
        {
            var path = AssetDatabase.GetAssetPath(timelineAsset);
            Debug.Log($"[TC] RebuildBindingDataAsset path={path}");
            var toDestroy = new System.Collections.Generic.List<TimelineBindingData>();
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                Debug.Log($"[TC] sub-asset: {sub?.name} type={sub?.GetType().Name}");
                if (sub is TimelineBindingData bd)
                    toDestroy.Add(bd);
            }
            Debug.Log($"[TC] found {toDestroy.Count} TimelineBindingData to destroy");
            foreach (var bd in toDestroy)
            {
                var bdName = bd.name;
                AssetDatabase.RemoveObjectFromAsset(bd);
                DestroyImmediate(bd, true);
                Debug.Log($"[TC] destroyed {bdName}");
            }
            return CreateBindingDataAsset(timelineAsset);

        }
    }
}
