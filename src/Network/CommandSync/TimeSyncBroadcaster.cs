using UnityEngine;

namespace Multipleer.Network.CommandSync
{
    // Host-only continuous clock mirror. Ticked from NetworkEngine.Update() every frame; throttles a
    // 0x34 TimingState broadcast to ~0.5s of real time, plus BroadcastNow() for immediate push after a
    // time change / auto-pause. Reads the host clock via TimeBridge.RecordHostState.
    public static class TimeSyncBroadcaster
    {
        private const float IntervalSeconds = 0.5f;
        private static float _accum;

        // Call once per frame from the host's NetworkEngine.Update().
        public static void Tick(NetworkEngine engine, float deltaTime)
        {
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            _accum += deltaTime;
            if (_accum < IntervalSeconds) return;
            _accum = 0f;
            BroadcastNow();
        }

        // Immediate push of the current host clock state to all peers (no-op off-host / no clock).
        public static void BroadcastNow()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || !engine.IsHost) return;
            var snap = TimeBridge.RecordHostState();
            if (snap == null) return;
            engine.BroadcastTimingState(snap.Payload);
        }
    }
}
