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
        public static event Action OnRegistered;

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

        void Register()
        {
            if (!IdMap.TryGetValue(Id, out var instances))
            {
                instances = new List<GameObject>();
                IdMap.Add(Id, instances);
            }

            if (!instances.Contains(gameObject))
            {
                instances.Add(gameObject);
                OnRegistered?.Invoke();
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
