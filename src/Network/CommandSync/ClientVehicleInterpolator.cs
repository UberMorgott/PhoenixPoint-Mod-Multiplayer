using System.Collections.Generic;
using UnityEngine;

namespace Multipleer.Network.CommandSync
{
    // SD-AIDR INC-A client vehicle render (client-only, PURE MIRROR, SINGLE WRITER). Places each mirrored
    // vehicle's on-globe ICON RAW at the LATEST APPLIED host transform — for EVERY vehicle, regardless of who
    // initiated travel. The client runs NO native vehicle travel routine of its own (NavigateRoutine /
    // StartTravel / Navigate are never invoked on a mirrored craft, the geo producers stay suppressed); the
    // host is the SOLE authority and pushes 0x35 GeoStateDiff state only. This component is the SOLE writer of
    // the client icon pivot.
    //
    // RAW placement only: on each applied 0x35 SurfacePos record we store the latest world position and place
    // the icon there immediately; every frame Tick re-places the stored position so the icon survives globe
    // redraws between the host's sparse (~10Hz) snapshots. There is NO snapshot buffer and NO lerp/slerp —
    // motion steps discretely from one applied host position to the next. Jerky-but-continuous is the EXPECTED
    // INC-A result; smoothing/interpolation is INC-C, deliberately not done here.
    //
    // Placement goes through the native primitive GeoBridge.PlaceGlobeIconAt → GeoActor
    // .SetOrientedGlobeWorldPosition (GeoActor.cs:66-77; the same per-frame effect NavigateRoutine produces at
    // GeoNavComponent.cs:117-119). Host-side this is a no-op (Tick self-gates on !IsHost).
    internal static class ClientVehicleInterpolator
    {
        private sealed class Entry
        {
            public object Vehicle;   // resolved live GeoVehicle ref (re-validated each tick)
            public Vector3 Pos;      // latest host-confirmed (mirrored) globe world position — placed RAW
            public bool HasPos;      // false until the first SetTarget seeds Pos
        }

        // Tracked vehicles keyed by stable (FactionGuid, VehicleID) identity. Reused across frames — no
        // per-frame allocation; entries are reused in place.
        private static readonly Dictionary<(string, int), Entry> _tracked =
            new Dictionary<(string, int), Entry>();

        // Reusable scratch list for dead-entry removal so the steady-state Tick allocates nothing on the heap.
        private static readonly List<(string, int)> _dead = new List<(string, int)>();

        // DIAG/INC-A: rate-limited (~1/sec per identity) marker proving this component is the SOLE writer that
        // placed the icon for an identity. Remove with the rest of the INC-A diagnostics in INC-D.
        private static readonly Dictionary<(string, int), float> _diagNextLogTime =
            new Dictionary<(string, int), float>();

        // Record the latest host-confirmed world position for a vehicle and place its icon RAW at that position
        // THIS frame. Called from the 0x35 apply path on every SurfacePos record (first mirror + every push) —
        // there is no travel/arrival branching: every applied position is simply "the latest", placed raw.
        public static void SetTarget(object vehicle, (string, int) identity, Vector3 worldPos)
        {
            if (vehicle == null) return;

            var e = GetOrCreate(identity);
            e.Vehicle = vehicle;     // refresh the ref
            e.Pos = worldPos;
            e.HasPos = true;
            GeoBridge.PlaceGlobeIconAt(vehicle, worldPos); // land exactly, this frame

            // DIAG/INC-A: prove the single writer (rate-limited per identity).
            float now = Time.realtimeSinceStartup;
            _diagNextLogTime.TryGetValue(identity, out var nextLog);
            if (now >= nextLog)
            {
                _diagNextLogTime[identity] = now + 1f;
                Debug.Log($"[Multipleer] DIAG/INC-A icon placed (SOLE writer) {identity.Item1}#{identity.Item2} -> {worldPos}");
            }
        }

        // Per-frame client tick: re-place every tracked vehicle's icon RAW at its latest applied position so the
        // icon holds between host snapshots (nothing else drives the pivot on the client). Self-gates to
        // client-only. No lerp/slerp, no extrapolation — pure raw placement (INC-A).
        public static void Tick(float dt)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return; // client-only
            if (_tracked.Count == 0) return;

            _dead.Clear();
            foreach (var kv in _tracked)
            {
                var e = kv.Value;
                // Drop entries whose vehicle ref has gone away. Unity overloads == so a destroyed Object
                // compares == null; the (Object) cast routes to that overload.
                if (e.Vehicle == null || (Object)e.Vehicle == (Object)null)
                {
                    _dead.Add(kv.Key);
                    continue;
                }
                if (!e.HasPos) continue;
                GeoBridge.PlaceGlobeIconAt(e.Vehicle, e.Pos);
            }

            for (int i = 0; i < _dead.Count; i++)
                _tracked.Remove(_dead[i]);
        }

        // Drop a tracked vehicle (hooked from the 0x36 VehicleRemoved apply path). Safe if absent.
        public static void Remove((string, int) identity)
        {
            _tracked.Remove(identity);
            _diagNextLogTime.Remove(identity);
        }

        // Clear all tracked vehicles — session reset / teardown (NetworkEngine.Shutdown).
        public static void Reset()
        {
            _tracked.Clear();
            _dead.Clear();
            _diagNextLogTime.Clear();
        }

        private static Entry GetOrCreate((string, int) identity)
        {
            if (!_tracked.TryGetValue(identity, out var e))
            {
                e = new Entry();
                _tracked[identity] = e;
            }
            return e;
        }
    }
}
