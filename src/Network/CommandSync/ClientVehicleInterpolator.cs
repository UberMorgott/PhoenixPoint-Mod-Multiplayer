using System.Collections.Generic;
using UnityEngine;

namespace Multipleer.Network.CommandSync
{
    // SD-AIDR INC-C client vehicle render (client-only, PURE MIRROR, SINGLE WRITER). Renders each mirrored
    // vehicle's on-globe ICON by SNAPSHOT INTERPOLATION of the host's authoritative samples — for EVERY vehicle,
    // regardless of who initiated travel. The client runs NO native vehicle travel routine of its own
    // (NavigateRoutine / StartTravel / Navigate are never invoked on a mirrored craft, the geo producers stay
    // suppressed); the host is the SOLE authority and pushes 0x35 GeoStateDiff state only. This component is the
    // SOLE writer of the client icon pivot and the smoothed Surface.rotation.
    //
    // INC-A placed the latest applied sample RAW each frame → correct but JERKY (stepped at ~15Hz). INC-C keeps
    // the exact same SINGLE-WRITER, no-native-sim invariant but renders SMOOTH: every applied 0x35 record pushes
    // {arrivalStamp, position, rotation} into a small per-identity ring buffer; each frame we render at
    // renderTime = now − InterpDelaySeconds and LERP position / SLERP rotation between the two samples that
    // bracket renderTime. This is NOT a second simulator — it only interpolates samples the host already sent.
    //
    //   • Timestamp = Time.realtimeSinceStartup at apply time (monotonic, pause-independent, self-consistent:
    //     stamp local, render at the same local now − delay). No clock crosses the wire.
    //   • InterpDelaySeconds = 0.35f ≈ 5× the host send interval (0.066s, INC-B) + jitter headroom. INC-D raised
    //     it from 0.2f after the live log showed constant mid-travel underrun (renderTime overran the newest
    //     sample → Hold → jerk); see the const below for the evidence.
    //   • UNDERRUN (renderTime newer than the newest sample, e.g. host paused / craft parked): HOLD the last
    //     sample. NO extrapolation — the icon simply stands still until fresh samples arrive.
    //   • EDGES: first sample (or buffer of one) → place directly (full mirror until ≥2 samples). Arrival /
    //     removal / reroute need no local finalize: the host's final samples bring the craft to its destination
    //     and the buffer naturally drains to HOLD at the last reported transform.
    //
    // Position goes through the native primitive GeoBridge.PlaceGlobeIconAt → GeoActor.SetOrientedGlobeWorldPosition
    // (GeoActor.cs:66-77), which orients the icon PIVOT tangent to the globe at that position. Heading/nose comes
    // from the wire quaternion written to Surface.rotation (a DIFFERENT transform — the two never clash; P4): the
    // slerp'd wire quaternion is the SOLE, LAST orientation writer. Host-side this whole component is a no-op
    // (Tick self-gates on !IsHost).
    internal static class ClientVehicleInterpolator
    {
        // Per-identity render-delay: render the host stream this far in the past so two real samples almost always
        // bracket renderTime. INC-D (jerk RCA): the 0.2s value UNDERRAN constantly mid-travel — the live client
        // log showed the moving craft flipping Interp↔Hold every ~1s with a FULL depth=12 buffer and underruns
        // climbing into the hundreds (renderTime = now−delay overran the newest sample because real inter-arrival
        // jitter exceeds 0.2s, even though the host flush nominally runs at 15Hz/0.066s). Each overrun HOLDs the
        // last sample then jumps when the next arrives → the "надрывно"/stepped jerk. Raising the delay to 0.35s
        // (≈5× the 0.066s send interval) pushes renderTime back far enough that two real samples bracket it across
        // the observed jitter; the 12-sample/~0.8s ring still has comfortable headroom. Trade-off: +0.15s of
        // intentional render latency on the (slow) geoscape craft — invisible to the eye and far preferable to the
        // stutter. Still a PURE single-writer mirror (no extrapolation; underrun holds the last sample).
        private const float InterpDelaySeconds = 0.35f;

        // Ring-buffer capacity per identity. At 15Hz, 12 samples ≈ 0.8s of history — still comfortably more than the
        // 0.35s render delay (INC-D) needs to bracket renderTime, with slack for a brief send-rate spike. Oldest is
        // overwritten when full.
        private const int RingCapacity = 12;

        private sealed class Entry
        {
            public object Vehicle;                    // resolved live GeoVehicle ref (refreshed each SetTarget)

            // Per-identity host↔local clock offset estimator. The ring Times are now keyed by the host's
            // HostSendTime (geoscape clock at sample emission), and we render at (estimatedHostNow − delay) so
            // the mirrored craft tracks HOST speed (no playback stretch from irregular packet arrival spacing).
            // Fallback: if a record carries no HostSendTime (0), SetTarget feeds local-arrival as the host time
            // AND observes (arrival,arrival) → offset 0 → the previous arrival-time behavior (backward-sane).
            public readonly HostClockOffsetEstimator Offset = new HostClockOffsetEstimator();

            // Scale-aware render window: tracks the observed inter-sample HostSendTime gap so the render delay
            // adapts to the geoscape time-scale (gap ≈ 0.066 × Scale game-s). The fixed InterpDelaySeconds is the
            // floor. This is what fixes the high-scale "lags + jerky bursts": a fixed 0.35 game-s delay is far
            // newer than the newest buffered sample at Scale=3600 (samples ~237 game-s apart) → chronic underrun.
            public readonly AdaptiveRenderWindow Window = new AdaptiveRenderWindow();

            // Parallel ring buffers (no per-frame heap alloc; written in place). Ordered oldest→newest in
            // [0..Count) after compaction in Tick. We append at Head and keep Count<=RingCapacity. Times is
            // DOUBLE: it is keyed by HostSendTime (the geoscape clock, ~6.4e10 game-s) where a float32 ULP
            // (~8192 s) exceeds the ~231 s between samples — a float key collapsed every buffered sample to one
            // value → Select returned Direct every frame → the in-game lag/jerk. double keeps the keys distinct.
            public readonly double[] Times = new double[RingCapacity];  // keyed by HostSendTime (host clock seconds)
            public readonly Vector3[] Pos = new Vector3[RingCapacity];
            public readonly Quaternion[] Rot = new Quaternion[RingCapacity];
            public int Count;                         // number of valid samples
            public int Head;                          // index of the NEXT write slot (circular)

            // Scratch ascending views reused each Tick so Select() sees a contiguous oldest→newest array with no
            // allocation. Sized to RingCapacity once. Times are double (see above).
            public readonly double[] OrderedTimes = new double[RingCapacity];
            public readonly int[] OrderedIdx = new int[RingCapacity];

            public void Push(double t, Vector3 p, Quaternion r)
            {
                Times[Head] = t; Pos[Head] = p; Rot[Head] = r;
                Head = (Head + 1) % RingCapacity;
                if (Count < RingCapacity) Count++;
            }

            // Fill OrderedTimes/OrderedIdx with the Count valid samples in ascending (oldest→newest) order.
            // The circular buffer's oldest element is at (Head - Count) mod cap. Returns Count.
            public int BuildOrdered()
            {
                int start = ((Head - Count) % RingCapacity + RingCapacity) % RingCapacity;
                for (int i = 0; i < Count; i++)
                {
                    int idx = (start + i) % RingCapacity;
                    OrderedIdx[i] = idx;
                    OrderedTimes[i] = Times[idx];
                }
                return Count;
            }
        }

        // Tracked vehicles keyed by stable (FactionGuid, VehicleID) identity. Reused across frames — no
        // per-frame allocation; entries are reused in place.
        private static readonly Dictionary<(string, int), Entry> _tracked =
            new Dictionary<(string, int), Entry>();

        // Reusable scratch list for dead-entry removal so the steady-state Tick allocates nothing on the heap.
        private static readonly List<(string, int)> _dead = new List<(string, int)>();

        // DIAG/INC-C: rate-limited (~1/sec per identity) buffer-depth + underrun marker. Proves this component is
        // the SOLE writer and exposes how often renderTime overruns the newest sample (tune InterpDelay/INC-B).
        // Remove with the rest of the DIAG in INC-D.
        private static readonly Dictionary<(string, int), float> _diagNextLogTime =
            new Dictionary<(string, int), float>();
        private static readonly Dictionary<(string, int), int> _diagUnderruns =
            new Dictionary<(string, int), int>();

        // DIAG-INTERP resnap (TEMP, logging only): per-identity ~1/sec throttle for the overwrite-proof trace
        // below — shows the interpolator's target pos vs the craft's CURRENT pos (what NavigateRoutine just set),
        // proving interpTarget != routinePos (the re-snap). Cleared in Remove/Reset alongside the other DIAG dicts.
        private static readonly Dictionary<(string, int), float> _diagResnapNextLogTime =
            new Dictionary<(string, int), float>();

        // Push the latest host-confirmed transform sample for a vehicle into its ring buffer. Called from the
        // 0x35 apply path on every SurfacePos record (first mirror + every push). NO placement happens here —
        // Tick is the sole writer and renders the buffered samples at now − InterpDelay. (INC-A placed raw here;
        // that reintroduced jerk and a second write per frame — removed in INC-C.)
        public static void SetTarget(object vehicle, (string, int) identity, Vector3 worldPos, Quaternion worldRot,
            double hostSendTime)
        {
            if (vehicle == null) return;

            var e = GetOrCreate(identity);
            e.Vehicle = vehicle;                              // refresh the ref

            double localArrival = Time.realtimeSinceStartup;
            // Key the ring by HOST time so renderTime (estimatedHostNow − delay) replays host SAMPLING spacing,
            // not packet ARRIVAL spacing. Missing/old record (hostSendTime <= 0) → fall back to arrival time:
            // observe (arrival,arrival) so the offset floor is 0 and rendering matches the legacy behavior.
            // hostSendTime is DOUBLE (geoscape clock ~6.4e10) — a float key here collapsed all samples in-game.
            bool haveHostTime = hostSendTime > 0.0;
            double timeKey = haveHostTime ? hostSendTime : localArrival;
            e.Offset.Observe(timeKey, localArrival);
            // Feed the host-time gap estimator only with REAL host stamps (the arrival-fallback key is not a
            // host-clock spacing). At Scale=1 / fallback the window stays at the InterpDelaySeconds floor.
            if (haveHostTime) e.Window.Observe(hostSendTime);
            e.Push(timeKey, worldPos, worldRot);
        }

        // Per-frame client tick: render every tracked vehicle's icon at renderTime = now − InterpDelaySeconds by
        // lerp(position)/slerp(rotation) between the two bracketing samples. Self-gates to client-only. NO
        // extrapolation — underrun holds the newest sample.
        public static void Tick(float dt)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return; // client-only
            if (_tracked.Count == 0) return;

            float localNow = Time.realtimeSinceStartup;

            // RENDER CLOCK = the per-identity SAMPLE-STREAM host-time ESTIMATE (offset + rate), NOT the stamped
            // geoscape Timing.Now. RCA: the host-slaved Timing.Now is re-anchored only every ~0.5s by host
            // TimingState stamps and chronically LAGS the host's true now by MORE than the entire ring span, so
            // every frame snapped to the OLDEST buffered sample (DIAG showed mode=Direct, underruns=0) → stepped
            // ~1s-cadence jerk + huge lag. The fix: HostClockOffsetEstimator estimates host-now from the received
            // (HostSendTime, localArrival) stream — phase + a RATE derived from the timestamps (≈ Scale, never
            // read off the engine) — and advances smoothly EVERY frame at that rate, so renderTime lands between
            // the two newest-ish samples → Interp every frame at ANY time-scale. (TimeBridge.GetHostNowSeconds is
            // still used HOST-side to stamp records and by ClientTimeMirror for the clock DISPLAY — just not as
            // the client RENDER clock.) Per-entry below: renderTime = estimateHostNow(localNow) − adaptiveWindow.

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
                if (e.Count == 0) continue;

                // Render at renderTime = estimateHostNow(localNow) − W, where W is the SCALE-AWARE adaptive
                // window (≥ InterpDelaySeconds floor) and estimateHostNow is the per-identity sample-stream clock
                // estimate (offset + derived rate). The ring Times are host-time keyed, so Select brackets on the
                // host timeline; the estimate advances smoothly at the host rate every frame so two real samples
                // bracket renderTime at ANY geoscape time-scale → Interp every frame (the fix for the in-game
                // stepped jerk + lag). No dependency on the lagging stamped Timing.Now.
                // All TIME math stays double — renderTime keys the geoscape clock (~6.4e10); a float cast here
                // would re-introduce the precision collapse the ring fix removes.
                double window = e.Window.Window(InterpDelaySeconds);
                double hostNowEst = e.Offset.EstimateHostNow(localNow);
                double renderTime = hostNowEst - window;

                int n = e.BuildOrdered();
                var b = ClientInterpolationCore.Select(e.OrderedTimes, n, renderTime);

                Vector3 pos;
                Quaternion rot;
                Vector3 travelDir = Vector3.zero; // FIX B: instantaneous motion vector (only known in the Interp case)
                switch (b.Mode)
                {
                    case ClientInterpolationCore.SampleMode.Empty:
                        continue;
                    case ClientInterpolationCore.SampleMode.Interp:
                    {
                        int i0 = e.OrderedIdx[b.I0];
                        int i1 = e.OrderedIdx[b.I1];
                        pos = Vector3.Lerp(e.Pos[i0], e.Pos[i1], b.Frac);
                        rot = Quaternion.Slerp(e.Rot[i0], e.Rot[i1], b.Frac);
                        travelDir = e.Pos[i1] - e.Pos[i0]; // direction the host is moving this bracket
                        break;
                    }
                    default: // Direct / Hold → place the single bracket sample raw (no interpolation, no extrapolation)
                    {
                        int i = e.OrderedIdx[b.I0];
                        pos = e.Pos[i];
                        rot = e.Rot[i];
                        if (b.Mode == ClientInterpolationCore.SampleMode.Hold)
                            BumpUnderrun(kv.Key);
                        break;
                    }
                }

                // DIAG-INTERP resnap (TEMP, logging only): BEFORE we write, show the interpolator target pos
                // about to be placed vs the craft's CURRENT pos read back from the same native primitive that
                // NavigateRoutine writes (GeoBridge.RecordVehicleState → Surface.position). If these differ, this
                // Tick is re-snapping the host-owned craft back to a stale buffered sample (the RCA'd collision).
                // Also reports buffer depth + USE_TRANSFORM_STREAM. Throttled ~1/sec per identity (reuses the
                // RecordVehicleState reflection primitive used host-side; cheap at 1/sec). Gate line shows that
                // this Tick is NOT gated for this vehicle (willWrite=true — Tick only self-gates on !IsHost above).
                MaybeLogResnap(kv.Key, pos, e.Vehicle, e.Count);

                // POSITION: orient the icon pivot tangent to the globe at the interpolated position.
                GeoBridge.PlaceGlobeIconAt(e.Vehicle, pos);
                // HEADING (FIX B): point the NOSE along the INTERPOLATED TRAVEL DIRECTION (the delta between the two
                // bracket samples) with instant:true, so the client nose mirrors the host's instantaneous motion
                // vector smoothly — no per-frame slerp fighting the interpolated position (the old wobble). The
                // streamed Surface world quat does NOT encode the visible nose (Surface.localEulerAngles.z), so it is
                // unused for heading. PlaceGlobeIconAt zeroes the pivot Z (GeoActor.cs:76), so this owns the nose
                // without clashing; no movement routine runs — single-writer mirror intact. In Direct/Hold (single
                // sample, no motion vector) we fall back to the waypoint-aim heading toward DestinationSites[0].
                if (travelDir.sqrMagnitude > 1e-6f)
                    GeoBridge.UpdateVehicleHeadingAlong(e.Vehicle, pos, travelDir);
                else
                    GeoBridge.UpdateVehicleHeadingTowards(e.Vehicle);

                // DIAG TEMP: confirms in-game that the render clock now brackets samples (mode flips Direct→Interp)
                // and renderTime sits BETWEEN oldest/newest host-time, at the estimated host rate. Remove with the
                // rest of the DIAG.
                MaybeLogDiag(kv.Key, e.Count, b.Mode, renderTime,
                    n > 0 ? e.OrderedTimes[0] : 0.0, n > 0 ? e.OrderedTimes[n - 1] : 0.0,
                    window, e.Offset.EstimatedRate);
            }

            // Reap dead refs via Remove() so the per-identity DIAG dicts are cleared too (not just _tracked) —
            // otherwise a craft reaped here (rather than via the 0x36 Remove path) leaks its DIAG entries.
            for (int i = 0; i < _dead.Count; i++)
                Remove(_dead[i]);
        }

        // Drop a tracked vehicle (hooked from the 0x36 VehicleRemoved apply path). Safe if absent.
        public static void Remove((string, int) identity)
        {
            _tracked.Remove(identity);
            _diagNextLogTime.Remove(identity);
            _diagUnderruns.Remove(identity);
            _diagResnapNextLogTime.Remove(identity);
        }

        // Clear all tracked vehicles — session reset / teardown (NetworkEngine.Shutdown).
        public static void Reset()
        {
            _tracked.Clear();
            _dead.Clear();
            _diagNextLogTime.Clear();
            _diagUnderruns.Clear();
            _diagResnapNextLogTime.Clear();
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

        // DIAG/INC-C: count an underrun (renderTime overran the newest sample → held last). Reverted in INC-D.
        private static void BumpUnderrun((string, int) identity)
        {
            _diagUnderruns.TryGetValue(identity, out var c);
            _diagUnderruns[identity] = c + 1;
        }

        // DIAG TEMP: rate-limited (~1/sec per identity) render-clock trace. Confirms the render clock estimate
        // brackets the buffered samples (renderTime between oldest/newest host-time → mode=Interp) and exposes
        // the derived host rate (estRate ≈ Scale). Remove with the rest of the DIAG.
        private static void MaybeLogDiag((string, int) identity, int depth, ClientInterpolationCore.SampleMode mode,
            double renderTime, double oldestHostTime, double newestHostTime, double window, double estRate)
        {
            float now = Time.realtimeSinceStartup;
            _diagNextLogTime.TryGetValue(identity, out var nextLog);
            if (now < nextLog) return;
            _diagNextLogTime[identity] = now + 1f;
            _diagUnderruns.TryGetValue(identity, out var underruns);
            Debug.Log($"[Multipleer] DIAG TEMP interp {identity.Item1}#{identity.Item2} depth={depth} mode={mode} " +
                      $"renderTime={renderTime:F2} oldestHostTime={oldestHostTime:F2} newestHostTime={newestHostTime:F2} " +
                      $"window={window:F3} estRate={estRate:F1} underruns={underruns}");
        }

        // DIAG-INTERP (TEMP, logging only): rate-limited (~1/sec per identity) overwrite-proof trace. Reads the
        // craft's CURRENT pos via GeoBridge.RecordVehicleState (Surface.position — what the host-owned native
        // NavigateRoutine just set on the client) and compares it to the interpolator target about to be written.
        // interpTarget != routinePos == the re-snap collision. The gate line records that this Tick is NOT gated
        // for the vehicle (willWrite=true). Logs only; never changes logic. Remove with the rest of the DIAG.
        private static void MaybeLogResnap((string, int) identity, Vector3 interpTarget, object vehicle, int depth)
        {
            float now = Time.realtimeSinceStartup;
            _diagResnapNextLogTime.TryGetValue(identity, out var nextLog);
            if (now < nextLog) return;
            _diagResnapNextLogTime[identity] = now + 1f;

            var cur = GeoBridge.RecordVehicleState(vehicle); // current Surface.position set by NavigateRoutine
            Debug.Log($"[Multipleer] DIAG-INTERP gate veh={identity.Item1}#{identity.Item2} willWrite=true");
            Debug.Log($"[Multipleer] DIAG-INTERP resnap {identity.Item1}#{identity.Item2} " +
                      $"interpTarget=({interpTarget.x:F1},{interpTarget.y:F1},{interpTarget.z:F1}) " +
                      $"routinePos=({cur.PosX:F1},{cur.PosY:F1},{cur.PosZ:F1}) " +
                      $"depth={depth} USE_TRANSFORM_STREAM={NetworkEngine.USE_TRANSFORM_STREAM}");
        }
    }
}
