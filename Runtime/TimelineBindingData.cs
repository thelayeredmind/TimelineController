using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace TLM.TimelineController
{
    [CreateAssetMenu(fileName = "TimelineBindingData", menuName = "Timeline Controller/Timeline Binding Data")]
    public class TimelineBindingData : ScriptableObject
    {
        public List<TrackBinding> trackBindings = new List<TrackBinding>();
        public List<NestedTimlineBinding> nestedTimelineBindings = new List<NestedTimlineBinding>();
    }
}
