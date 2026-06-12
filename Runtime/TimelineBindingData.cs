using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLM.TimelineController
{
    // Structural key for a track: stable under reordering. occurrence disambiguates
    // multiple tracks with the same groupPath+name+type (rare; 0 for the common case).
    [Serializable]
    public struct TrackKey : IEquatable<TrackKey>
    {
        public string groupPath;
        public string trackName;
        public string trackType;
        public int occurrence;

        public bool Equals(TrackKey other) =>
            groupPath == other.groupPath && trackName == other.trackName
            && trackType == other.trackType && occurrence == other.occurrence;

        public override bool Equals(object obj) => obj is TrackKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (groupPath?.GetHashCode() ?? 0);
                hash = hash * 31 + (trackName?.GetHashCode() ?? 0);
                hash = hash * 31 + (trackType?.GetHashCode() ?? 0);
                hash = hash * 31 + occurrence;
                return hash;
            }
        }
    }

    [Serializable]
    public class TrackBinding
    {
        public TrackKey key;
        public string id;
    }

    // A Control Track clip's sourceGameObject binding, keyed by its parent track's
    // TrackKey plus its index within that track's clip list.
    [Serializable]
    public class ClipBinding
    {
        public TrackKey trackKey;
        public int clipIndex;
        public string id;
    }

    [CreateAssetMenu(fileName = "TimelineBindingData", menuName = "Timeline Controller/Timeline Binding Data")]
    public class TimelineBindingData : ScriptableObject
    {
        public List<TrackBinding> trackBindings = new List<TrackBinding>();
        public List<ClipBinding> clipBindings = new List<ClipBinding>();
    }
}
