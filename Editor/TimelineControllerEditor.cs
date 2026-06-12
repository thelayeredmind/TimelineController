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

            var bindingData = timelineController.BindingData;
            var currentAsset = director.playableAsset as TimelineAsset;

            if (bindingData == null)
            {
                EditorGUILayout.HelpBox("No Binding Data assigned.", MessageType.Warning);
                if (GUILayout.Button("Create Binding Data"))
                {
                    if (currentAsset == null)
                    {
                        EditorUtility.DisplayDialog("No Asset", "Assign a TimelineAsset to the PlayableDirector first.", "OK");
                    }
                    else
                    {
                        Undo.RecordObject(timelineController, "Create Binding Data");
                        var data = CreateBindingDataAsset(currentAsset);
                        var so = serializedObject;
                        so.Update();
                        so.FindProperty("bindingData").objectReferenceValue = data;
                        so.ApplyModifiedProperties();
                        EditorUtility.SetDirty(timelineController);
                    }
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
                    "This will clear all stored bindings and re-capture from the current scene state. Stale entries will be discarded.\n\nThis cannot be undone.",
                    "Reset", "Cancel"))
                {
                    Undo.RecordObject(timelineController, "Reset Timeline Bindings");
                    timelineController.ResetBindings();
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

        static TimelineBindingData CreateBindingDataAsset(TimelineAsset timelineAsset)
        {
            var data = ScriptableObject.CreateInstance<TimelineBindingData>();
            data.name = "BindingData";
            AssetDatabase.AddObjectToAsset(data, timelineAsset);
            AssetDatabase.SaveAssets();
            var path = AssetDatabase.GetAssetPath(timelineAsset);
            AssetDatabase.ImportAsset(path);
            // ImportAsset can reload/reinstantiate sub-assets, invalidating `data` — re-fetch
            // the persisted instance so the reference we return/assign is the live one.
            return AssetDatabase.LoadAllAssetsAtPath(path).OfType<TimelineBindingData>().FirstOrDefault(d => d.name == "BindingData");
        }
    }
}
