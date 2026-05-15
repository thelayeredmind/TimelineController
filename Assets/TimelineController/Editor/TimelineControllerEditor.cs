using UnityEngine;
using UnityEditor;
using TLM.TimelineController;


    [CustomEditor(typeof(TimelineController))]
    public class TimelineControllerEditor : Editor
    {
        TimelineController timelineController;
        private void OnEnable()
        {
            timelineController = serializedObject.targetObject as TimelineController;
        }


        public override void OnInspectorGUI()
        {
            if (timelineController.gameObject.scene != null && timelineController.gameObject.scene.isLoaded)
            {
                GameObject prefab = PrefabUtility.GetCorrespondingObjectFromOriginalSource(timelineController.gameObject);
                if (prefab && PrefabUtility.HasPrefabInstanceAnyOverrides(timelineController.gameObject, false))
                {
                    if (GUILayout.Button("Save"))
                    {
                        PrefabUtility.ApplyPrefabInstance(timelineController.gameObject, InteractionMode.AutomatedAction);
                    }
                }
            }
            using (new EditorGUI.DisabledGroupScope(true))
                base.OnInspectorGUI();
        }
    }
