using System.Collections.Generic;
using UnityEngine;

namespace Multipleer.Network.CommandSync
{
    // SD-AIDR INC-3 client vehicle render (client-only, PURE MIRROR). Renders each mirrored vehicle's on-globe
    // ICON between the host's sparse ~10Hz 0x35 GeoStateDiff snapshots so it FLIES smoothly instead of jerking.
    // Two render modes, both pure render (no game-logic sim, no authority side-effects, host stays sole
    // authority — the client never runs NavigateRoutine/StartTravel/Navigate, geo producers stay suppressed):
    //
    //   ── SEGMENT mode (native-identical, preferred) ──────────────────────────────────────────────────────
    //   Reproduces the EXACT native travel equation as render. GeoNavComponent.NavigateRoutine animates a leg
    //   along ONE great-circle PathSegment at a uniform TIME rate (GeoNavComponent.cs:104-119):
    //       totalTime = GeoMap.Distance(start,end).InMeters / (Speed.InMeters/3600)   (:104-105)
    //       num       = totalTime.Ratio01(startTime, Timing.Now)                       (:112)
    //       pos       = Vector3.Slerp(start, end, num)                                 (:117)
    //   The client clock is host-slaved (0x34 TimeSync → Timing.Now == host ±0.5s), the path is deterministic,
    //   and DestinationSites are mirrored — so evaluating this SAME equation against the slaved clock yields
    //   the native arc at the native speed WITHOUT running any authority code. On the discrete Travelling=true
    //   transition a segment is started (segStart = current rendered pos, segEnd = mirrored
    //   DestinationSites[0].WorldPosition, startTime = slaved Timing.Now, speed = mirrored Stats.Speed); each
    //   frame Tick recomputes num from the slaved clock and Slerps. Continuous 0x35 SurfacePos pushes during a
    //   segment are NOT snapped — they GENTLY, BOUNDEDLY phase-correct the segment startTime so the
    //   equation-driven motion re-locks to host truth without a visible jump (InterpolationMath.CorrectedStartSec).
    //
    //   ── EASE mode (fallback) ────────────────────────────────────────────────────────────────────────────
    //   When segment data is unavailable/degenerate (no speed, no/unsynced destination, ~zero-length leg, no
    //   clock, or a non-travel push), fall back to the original exponential ease-to-latest toward the latest
    //   host SurfacePos (Vector3.Slerp(current, target, SmoothFactor(K,dt))) — frame-rate independent, never
    //   overshoots, settles exactly at the latest host position. Never extrapolates past host truth.
    //
    // Both modes re-orient the icon pivot through the native primitive GeoBridge.PlaceGlobeIconAt →
    // GeoActor.SetOrientedGlobeWorldPosition (GeoActor.cs:66-77; the same per-frame effect NavigateRoutine
    // produces at GeoNavComponent.cs:117-119). Host-side this is a no-op (Tick self-gates on !IsHost).
    internal static class ClientVehicleInterpolator
    {
        // EASE rate (1/seconds). factor = 1 - exp(-K*dt). At K=18 the icon closes ~95% of the gap to a new host
        // position within ~0.17s — smooth, no perceptible lag, always settles exactly at the latest host
        // position if updates pause (no extrapolation). Used only in the EASE fallback path.
        private const float K = 18f;

        // SEGMENT phase-correction strength + bound. Each 0x35 SurfacePos sample nudges the segment startTime a
        // small fraction toward the host's implied phase, clamped to ±CorrectionMaxShiftSeconds (the clock-sync
        // tolerance) so a transient outlier can never rubber-band the icon. Subtle by design.
        private const float CorrectionFactor = 0.15f;
        private const float CorrectionMaxShiftSeconds = 0.5f;
        // Below this great-circle angle (radians) a "segment" is a non-move — render it via ease (avoids a
        // degenerate totalTime≈0 div and pointless slerp). ~5.7e-4 rad ≈ a few km on the globe.
        private const float MinSegmentAngleRad = 5e-4f;

        private sealed class Entry
        {
            public object Vehicle;     // resolved live GeoVehicle ref (re-validated each tick)
            public Vector3 Current;    // current RENDERED globe world position
            public Vector3 Target;     // latest host-confirmed (mirrored) globe world position (EASE mode)
            public bool HasCurrent;    // false until the first SetTarget/StartSegment seeds Current

            // ── SEGMENT mode state (active only while SegActive) ──
            public bool SegActive;     // true → render via the native equation; false → EASE toward Target
            public Vector3 SegStart;   // segment departure world point (great-circle arc start)
            public Vector3 SegEnd;     // segment destination world point (DestinationSites[0].WorldPosition)
            public Vector3 SegCenter;  // globe centre the arc is measured/slerped about
            public double SegStartSec; // slaved Timing.Now (seconds) when the segment began (phase origin)
            public float SegTotalSec;  // native totalTime for this leg (distance/speed), seconds
            public float SegAngleRad;  // great-circle angle of the leg (cached for the correction projection)
        }

        // Tracked vehicles keyed by stable (FactionGuid, VehicleID) identity. Reused across frames — no
        // per-frame allocation; entries are reused in place.
        private static readonly Dictionary<(string, int), Entry> _tracked =
            new Dictionary<(string, int), Entry>();

        // Reusable scratch list for dead-entry removal so the steady-state Tick allocates nothing on the heap.
        private static readonly List<(string, int)> _dead = new List<(string, int)>();

        // EASE-mode record of the latest host-confirmed world position for a vehicle (the original path; kept
        // as the fallback and for non-travel pushes / discrete arrivals).
        //   snap=true  -> first mirror / load / discrete ARRIVAL: jump rendered position to target, place NOW,
        //                 and END any active segment (parked/arrived craft lands exactly, not an ease behind).
        //   snap=false -> continuous travel push: update target only; Tick eases toward it.
        public static void SetTarget(object vehicle, (string, int) identity, Vector3 worldPos, bool snap)
        {
            if (vehicle == null) return;

            var e = GetOrCreate(identity);
            e.Vehicle = vehicle;      // refresh the ref
            e.Target = worldPos;

            if (snap || !e.HasCurrent)
            {
                e.Current = worldPos;
                e.HasCurrent = true;
                e.SegActive = false;  // a snap (first mirror / arrival) ends segment render
                GeoBridge.PlaceGlobeIconAt(vehicle, worldPos); // land exactly, this frame
            }
        }

        // Begin (or re-derive) NATIVE-EQUATION segment render for a vehicle that is travelling. Called from the
        // apply path on the Travelling=true transition (and on multi-hop / reroute, where segEnd moves). Pure
        // render: derives the leg geometry from already-mirrored state only.
        //   segStartWorld : departure point — the current rendered position (continuous arc, no teleport).
        //   segEndWorld   : mirrored DestinationSites[0].WorldPosition.
        //   center        : globe centre (GeoMap.Distance reference, == GlobeUnits.GlobeCenter).
        //   startSec      : slaved Timing.Now in seconds at the moment travel started (phase origin).
        //   speedEarthVal : mirrored GeoVehicle.Speed.Value (EarthUnits.Value).
        // Returns true if a valid segment was started; false (degenerate speed / ~zero-length leg) so the
        // caller falls back to EASE. Idempotent for multi-hop: if an active segment already targets ~the same
        // end, it is left running (continuous pos pushes phase-correct it) rather than restarted.
        public static bool StartSegment(object vehicle, (string, int) identity,
            Vector3 segStartWorld, Vector3 segEndWorld, Vector3 center, double startSec, float speedEarthVal)
        {
            if (vehicle == null) return false;

            float angle = InterpolationMath.GreatCircleAngleRad(
                segStartWorld.x, segStartWorld.y, segStartWorld.z,
                segEndWorld.x, segEndWorld.y, segEndWorld.z,
                center.x, center.y, center.z);
            float total = InterpolationMath.SegmentTotalSeconds(angle, speedEarthVal);
            if (total < 0f || angle < MinSegmentAngleRad) return false; // invalid speed / non-move → ease

            var e = GetOrCreate(identity);
            e.Vehicle = vehicle;

            // Seed the rendered position if this is the first time we see this craft.
            if (!e.HasCurrent) { e.Current = segStartWorld; e.HasCurrent = true; }

            // Multi-hop idempotence: an already-active segment whose END essentially matches the new one keeps
            // running (its phase is being corrected by the pos stream) — don't reset its clock mid-flight.
            if (e.SegActive && Approximately(e.SegEnd, segEndWorld)) { e.Vehicle = vehicle; return true; }

            // Start (or re-derive) the segment from the current rendered position so the arc continues smoothly.
            e.SegStart = e.HasCurrent ? e.Current : segStartWorld;
            // Recompute the angle/total from the actual arc start (current rendered pos may differ slightly).
            e.SegAngleRad = InterpolationMath.GreatCircleAngleRad(
                e.SegStart.x, e.SegStart.y, e.SegStart.z,
                segEndWorld.x, segEndWorld.y, segEndWorld.z,
                center.x, center.y, center.z);
            float total2 = InterpolationMath.SegmentTotalSeconds(e.SegAngleRad, speedEarthVal);
            if (total2 < 0f || e.SegAngleRad < MinSegmentAngleRad) return false; // degenerate from here → ease
            e.SegEnd = segEndWorld;
            e.SegCenter = center;
            e.SegStartSec = startSec;
            e.SegTotalSec = total2;
            e.Target = segEndWorld;   // keep EASE target coherent for any later fallback
            e.SegActive = true;
            return true;
        }

        // Gentle, BOUNDED phase correction from a continuous 0x35 SurfacePos sample during an active segment.
        // Does NOT snap: projects the authoritative sample onto the arc (its angle-ratio = where the host
        // truly is now) and exp-blends the segment startTime a small fraction toward the host's implied phase,
        // clamped to ±CorrectionMaxShiftSeconds. Keeps equation-driven motion locked to host truth without a
        // visible jump. No-op if the identity has no active segment (the caller uses EASE for those).
        public static void CorrectionSample((string, int) identity, Vector3 worldPos, double nowSec)
        {
            if (!_tracked.TryGetValue(identity, out var e) || !e.SegActive) return;
            if (double.IsNaN(nowSec)) return;

            float angToSample = InterpolationMath.GreatCircleAngleRad(
                e.SegStart.x, e.SegStart.y, e.SegStart.z,
                worldPos.x, worldPos.y, worldPos.z,
                e.SegCenter.x, e.SegCenter.y, e.SegCenter.z);
            float sampleNum = InterpolationMath.ArcRatioFromAngles(angToSample, e.SegAngleRad);
            e.SegStartSec = InterpolationMath.CorrectedStartSec(
                e.SegStartSec, nowSec, e.SegTotalSec, sampleNum, CorrectionFactor, CorrectionMaxShiftSeconds);
        }

        // End segment render and SNAP exactly to the final host position (discrete arrival / CurrentSite set).
        public static void EndSegmentSnap(object vehicle, (string, int) identity, Vector3 worldPos)
        {
            var e = GetOrCreate(identity);
            e.Vehicle = vehicle;
            e.Current = worldPos;
            e.Target = worldPos;
            e.HasCurrent = true;
            e.SegActive = false;
            if (vehicle != null) GeoBridge.PlaceGlobeIconAt(vehicle, worldPos);
        }

        // True if an identity is currently rendering in segment mode (lets the apply path route a continuous
        // SurfacePos push to CorrectionSample vs the EASE SetTarget).
        public static bool IsSegmentActive((string, int) identity)
            => _tracked.TryGetValue(identity, out var e) && e.SegActive;

        // Per-frame client tick: advance every tracked vehicle (segment equation OR ease) and re-place its
        // icon. Self-gates to client-only. Frame-rate independent; never extrapolates past host truth.
        public static void Tick(float dt)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return; // client-only
            if (_tracked.Count == 0) return;

            float easeFactor = InterpolationMath.SmoothFactor(K, dt);
            double nowSec = GeoBridge.NowSeconds(); // slaved clock; NaN if no geoscape → segments hold via ease

            _dead.Clear();
            foreach (var kv in _tracked)
            {
                var e = kv.Value;
                // Drop entries whose vehicle ref has gone away. Unity overloads == so a destroyed Object
                // compares == null; the (Object) cast routes to that overload.
                if (e.Vehicle == null || (Object)e.Vehicle == (Object)null || !e.HasCurrent)
                {
                    if (e.Vehicle == null || (Object)e.Vehicle == (Object)null) _dead.Add(kv.Key);
                    continue;
                }

                if (e.SegActive && !double.IsNaN(nowSec))
                {
                    // NATIVE EQUATION: num = Clamp01((Now-start)/totalTime); pos = Slerp(start,end,num). The
                    // arc + uniform rate are native-identical (the slaved clock drives num); no ease needed.
                    // The slerp is CENTERED on the globe centre (SegCenter), mirroring native NavigateRoutine
                    // which slerps in GlobeCollider-LOCAL space (sphere centre == local origin,
                    // GeoNavComponent.cs:117). Slerping about world origin instead would keep the endpoints
                    // right but bow the mid-arc off the geodesic (and fight CorrectionSample, which measures
                    // the angle about SegCenter). Recenter: C + Slerp(start-C, end-C, num).
                    float num = InterpolationMath.SegmentNum(e.SegStartSec, nowSec, e.SegTotalSec);
                    e.Current = e.SegCenter
                        + Vector3.Slerp(e.SegStart - e.SegCenter, e.SegEnd - e.SegCenter, num);
                    GeoBridge.PlaceGlobeIconAt(e.Vehicle, e.Current);
                }
                else
                {
                    // EASE fallback: close `easeFactor` of the remaining arc toward the latest host position —
                    // asymptotic, never overshoots, settles exactly at Target. (Also covers a segment that
                    // momentarily can't read the clock — it holds-then-eases rather than jumping.)
                    e.Current = Vector3.Slerp(e.Current, e.Target, easeFactor);
                    GeoBridge.PlaceGlobeIconAt(e.Vehicle, e.Current);
                }
            }

            for (int i = 0; i < _dead.Count; i++)
                _tracked.Remove(_dead[i]);
        }

        // Drop a tracked vehicle (hooked from the 0x36 VehicleRemoved apply path). Safe if absent.
        public static void Remove((string, int) identity)
        {
            _tracked.Remove(identity);
        }

        // Clear all tracked vehicles — session reset / teardown (NetworkEngine.Shutdown).
        public static void Reset()
        {
            _tracked.Clear();
            _dead.Clear();
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

        // Cheap squared-distance "same point" test for multi-hop idempotence (world units; ~1m² threshold —
        // globe world coords are small, so this only matches a genuinely identical destination).
        private static bool Approximately(Vector3 a, Vector3 b)
        {
            return (a - b).sqrMagnitude < 1e-4f;
        }
    }
}
