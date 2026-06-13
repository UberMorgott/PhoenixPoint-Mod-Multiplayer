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
    //   • InterpDelaySeconds = 0.2f ≈ 3× the host send interval (0.066s, INC-B) + jitter headroom. Tune DOWN in
    //     INC-D, never up.
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
        // bracket renderTime. ≈3× INC-B send interval (0.066s) + jitter. INC-D tunes DOWN, not up.
        private const float InterpDelaySeconds = 0.2f;

        // Ring-buffer capacity per identity. At 15Hz, 12 samples ≈ 0.8s of history — comfortably more than the
        // 0.2s render delay needs, with slack for a brief send-rate spike. Oldest is overwritten when full.
        private const int RingCapacity = 12;

        private sealed class Entry
        {
            public object Vehicle;                    // resolved live GeoVehicle ref (refreshed each SetTarget)

            // Parallel ring buffers (no per-frame heap alloc; written in place). Ordered oldest→newest in
            // [0..Count) after compaction in Tick. We append at Head and keep Count<=RingCapacity.
            public readonly float[] Times = new float[RingCapacity];
            public readonly Vector3[] Pos = new Vector3[RingCapacity];
            public readonly Quaternion[] Rot = new Quaternion[RingCapacity];
            public int Count;                         // number of valid samples
            public int Head;                          // index of the NEXT write slot (circular)

            // Scratch ascending views reused each Tick so Select() sees a contiguous oldest→newest array with no
            // allocation. Sized to RingCapacity once.
            public readonly float[] OrderedTimes = new float[RingCapacity];
            public readonly int[] OrderedIdx = new int[RingCapacity];

            public void Push(float t, Vector3 p, Quaternion r)
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

        // Push the latest host-confirmed transform sample for a vehicle into its ring buffer. Called from the
        // 0x35 apply path on every SurfacePos record (first mirror + every push). NO placement happens here —
        // Tick is the sole writer and renders the buffered samples at now − InterpDelay. (INC-A placed raw here;
        // that reintroduced jerk and a second write per frame — removed in INC-C.)
        public static void SetTarget(object vehicle, (string, int) identity, Vector3 worldPos, Quaternion worldRot)
        {
            if (vehicle == null) return;

            var e = GetOrCreate(identity);
            e.Vehicle = vehicle;                              // refresh the ref
            e.Push(Time.realtimeSinceStartup, worldPos, worldRot);
        }

        // Per-frame client tick: render every tracked vehicle's icon at renderTime = now − InterpDelaySeconds by
        // lerp(position)/slerp(rotation) between the two bracketing samples. Self-gates to client-only. NO
        // extrapolation — underrun holds the newest sample.
        public static void Tick(float dt)
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActive || engine.IsHost) return; // client-only
            if (_tracked.Count == 0) return;

            float renderTime = Time.realtimeSinceStartup - InterpDelaySeconds;

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

                int n = e.BuildOrdered();
                var b = ClientInterpolationCore.Select(e.OrderedTimes, n, renderTime);

                Vector3 pos;
                Quaternion rot;
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

                // POSITION: orient the icon pivot tangent to the globe at the interpolated position.
                GeoBridge.PlaceGlobeIconAt(e.Vehicle, pos);
                // HEADING (P4): the slerp'd wire quaternion is the SOLE/LAST orientation writer — set AFTER
                // placement. PlaceGlobeIconAt writes PivotTransform.localRotation (globe tangent), a DIFFERENT
                // transform than Surface.rotation, so this never clobbers it; setting it here keeps heading smooth.
                GeoBridge.SetSurfaceRotation(e.Vehicle, rot);

                MaybeLogDiag(kv.Key, e.Count, b.Mode);
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
        }

        // Clear all tracked vehicles — session reset / teardown (NetworkEngine.Shutdown).
        public static void Reset()
        {
            _tracked.Clear();
            _dead.Clear();
            _diagNextLogTime.Clear();
            _diagUnderruns.Clear();
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

        // DIAG/INC-C: rate-limited (~1/sec per identity) buffer-depth + underrun log. Reverted in INC-D.
        private static void MaybeLogDiag((string, int) identity, int depth, ClientInterpolationCore.SampleMode mode)
        {
            float now = Time.realtimeSinceStartup;
            _diagNextLogTime.TryGetValue(identity, out var nextLog);
            if (now < nextLog) return;
            _diagNextLogTime[identity] = now + 1f;
            _diagUnderruns.TryGetValue(identity, out var underruns);
            Debug.Log($"[Multipleer] DIAG/INC-C interp {identity.Item1}#{identity.Item2} depth={depth} mode={mode} underruns={underruns}");
        }
    }
}
