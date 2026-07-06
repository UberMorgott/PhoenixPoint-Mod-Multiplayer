using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE (Unity-free) per-site rate limiter for the WA-2 mission-drift dirty hook (spec §5 gap 1c).
    /// <c>GeoUpdateableMission.Update</c> fires on every native mission tick (haven-defense deployments drift
    /// hourly, GeoHavenDefenseMission.cs:47-108) — each fire re-snapshots the whole owning site on channel #5,
    /// so the hook throttles to ≤1 dirty-mark per site per <see cref="MinIntervalSeconds"/> (wall-clock;
    /// last-wins snapshots make skipped ticks harmless: the NEXT allowed mark re-reads live values).
    /// Not thread-safe by design: the geoscape sim (Timing updateables) and the Harmony postfix run on the
    /// Unity main thread only.
    /// </summary>
    public sealed class MissionDriftThrottle
    {
        public readonly double MinIntervalSeconds;
        private readonly Dictionary<int, double> _lastMark = new Dictionary<int, double>();

        public MissionDriftThrottle(double minIntervalSeconds = 1.0)
        {
            MinIntervalSeconds = minIntervalSeconds;
        }

        /// <summary>True → the caller should dirty-mark <paramref name="siteId"/> now (records the mark);
        /// false → suppressed (a mark for this site happened less than <see cref="MinIntervalSeconds"/> ago).
        /// Sites throttle INDEPENDENTLY. Negative ids never mark.</summary>
        public bool ShouldMark(int siteId, double nowSeconds)
        {
            if (siteId < 0) return false;
            if (_lastMark.TryGetValue(siteId, out var last) && nowSeconds - last < MinIntervalSeconds)
                return false;
            _lastMark[siteId] = nowSeconds;
            return true;
        }

        /// <summary>Drop all recorded marks (session teardown / rebind).</summary>
        public void Reset() => _lastMark.Clear();
    }
}
