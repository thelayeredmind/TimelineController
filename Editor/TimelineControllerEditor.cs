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

            // --- SO contents (ground truth) — green if confirmed (resolved by
            // InstallRuntimeBindings this session), red if not yet confirmed. ---
            var confirmedIds = timelineController.ConfirmedBindingIds;
            var activeEntry = entries.Find(e => e.timelineAsset == currentAsset);
            var soTrackBindings = activeEntry?.bindingData?.trackBindings;
            var soNestedBindings = activeEntry?.bindingData?.nestedTimelineBindings;

            EditorGUILayout.LabelField("Track Bindings (from SO)", EditorStyles.boldLabel);
            if (soTrackBindings == null || soTrackBindings.Count == 0)
            {
                EditorGUILayout.HelpBox("No bindings in SO.", MessageType.None);
            }
            else
            {
                foreach (var b in soTrackBindings)
                    DrawBindingRow($"  track {b.trackIndex}", b.id, confirmedIds.Contains(b.id));
            }

            if (soNestedBindings != null && soNestedBindings.Count > 0)
            {
                EditorGUILayout.LabelField("Clip Bindings (from SO)", EditorStyles.boldLabel);
                foreach (var nb in soNestedBindings)
                    DrawBindingRow($"  track {nb.trackIndex} clip {nb.clipIndex}", nb.id, confirmedIds.Contains(nb.id));
            }

            // --- Binding change status ---
            EditorGUILayout.Space();
            const double recentWindow = 2.0;
            double timeSinceDiff = EditorApplication.timeSinceStartup - timelineController.LastDiffTime;
            bool recentlyUpdated = timelineController.LastDiffTime >= 0 && timeSinceDiff < recentWindow;

            if (recentlyUpdated)
            {
                EditorGUILayout.HelpBox("Change detected, updated SO", MessageType.Info);
                var lastDiff = timelineController.LastDiffSummary;
                using (new EditorGUI.DisabledGroupScope(true))
                {
                    foreach (var line in lastDiff)
                        EditorGUILayout.LabelField($"  {line}");
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Bindings locked — listening for binding changes...", MessageType.None);
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledGroupScope(true))
                base.OnInspectorGUI();
        }

        // Confirmed = resolved by InstallRuntimeBindings at least once this session, so it's
        // eligible for SO diff/write. Unconfirmed entries (target scene not loaded yet) are
        // preserved as-is and shown in red. Baked into GUIStyle.normal/onNormal because
        // GUI.contentColor is overridden by the skin while GUI.enabled is false.
        static readonly Color ConfirmedColor = new Color(0.4f, 1f, 0.4f);
        static readonly Color UnconfirmedColor = new Color(1f, 0.35f, 0.35f);
        static GUIStyle _confirmedStyle;
        static GUIStyle _unconfirmedStyle;

        static void DrawBindingRow(string label, string id, bool confirmed)
        {
            if (_confirmedStyle == null)
            {
                _confirmedStyle = new GUIStyle(EditorStyles.label);
                _confirmedStyle.normal.textColor = ConfirmedColor;
                _confirmedStyle.onNormal.textColor = ConfirmedColor;

                _unconfirmedStyle = new GUIStyle(EditorStyles.label);
                _unconfirmedStyle.normal.textColor = UnconfirmedColor;
                _unconfirmedStyle.onNormal.textColor = UnconfirmedColor;
            }

            var style = confirmed ? _confirmedStyle : _unconfirmedStyle;
            EditorGUILayout.LabelField(label, id, style);
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
            foreach (var bd in AssetDatabase.LoadAllAssetsAtPath(path).OfType<TimelineBindingData>().ToList())
            {
                AssetDatabase.RemoveObjectFromAsset(bd);
                DestroyImmediate(bd, true);
            }
            return CreateBindingDataAsset(timelineAsset);
        }
    }
}
