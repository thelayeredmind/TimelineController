using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLM.TimelineController
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class TimelineReference : MonoBehaviour
    {
        public static readonly Dictionary<string, List<GameObject>> IdMap = new Dictionary<string, List<GameObject>>();

        // Fired when a new instance enters IdMap for the first time — signals the controller that
        // scene state has changed and a full discovery sweep (RegisterAll) should be scheduled.
        public static event Action OnSceneStateChanged;

        [SerializeField, ShowAsReadOnly]
        public string Id = Guid.NewGuid().ToString();

        void Awake()
        {
            Register();
        }

        void OnEnable()
        {
            Register();
        }

        // Sweeps all TimelineReferences in loaded scenes, including inactive ones, and registers
        // each into IdMap. Called by TimelineController after OnSceneStateChanged fires.
        public static void RegisterAll()
        {
            var all = FindObjectsByType<TimelineReference>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var r in all)
                r.Register();
        }

        void Register()
        {
            if (!IdMap.TryGetValue(Id, out var instances))
            {
                instances = new List<GameObject>();
                IdMap.Add(Id, instances);
            }

            instances.RemoveAll(go => go == null);

            if (!instances.Contains(gameObject))
            {
                instances.Add(gameObject);
                OnSceneStateChanged?.Invoke();
            }
        }

        void OnDestroy()
        {
            if (IdMap.TryGetValue(Id, out var instances))
            {
                instances.Remove(gameObject);
                if (instances.Count == 0)
                    IdMap.Remove(Id);
            }
        }
    }
}
