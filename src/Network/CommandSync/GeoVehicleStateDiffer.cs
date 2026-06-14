using System;
using System.Collections.Generic;

namespace Multipleer.Network.CommandSync
{
    /// <summary>
    /// SD-AIDR INC-3a — pure host-side diff+seq core for the 0x35 GeoStateDiff vehicle mirror.
    /// Holds the last-SENT snapshot and a monotonic sequence per stable identity
    /// (FactionGuid = GeoFaction.Def.Guid, VehicleID = GeoVehicle.VehicleID), and turns a fresh
    /// authoritative snapshot into a wire record whose <c>ChangedMask</c> flags ONLY the fields that
    /// actually moved — epsilon-thresholded on the CONTINUOUS pos/rot/range fields (kills float churn),
    /// exact-compared on the DISCRETE travelling/site/dest/hp transitions (must be precise). Engine
    /// snapshotting stays in GeoBridge.RecordVehicleState; this class is Unity-free and unit-tested.
    ///
    /// Cadence/seq contract:
    ///  - The FIRST snapshot of an identity emits the FULL mask at seq 1 (client does a heavy first mirror).
    ///  - An unchanged re-submit returns ChangedMask==0 and burns NO seq (no packet is sent, so no seq
    ///    is consumed — keeps the client's seq-guard tight).
    ///  - A changed snapshot increments that identity's seq by one and returns only the changed bits.
    ///  - Diff is per-identity independent: same VehicleID under two factions never share a seq line.
    ///  - Sub-epsilon drift is compared against the last SENT value (not last seen) so accumulated drift
    ///    eventually crosses the threshold and fires (no permanent staleness).
    ///
    /// Channel split: ContinuousBits/DiscreteBits partition a mask so the broadcaster can send the
    /// continuous pos/rot/range bits UNRELIABLE (loss-tolerant, seq-guarded) and the discrete
    /// arrival/departure/HP bits RELIABLE (must not be lost).
    /// </summary>
    public sealed class GeoVehicleStateDiffer
    {
        /// <summary>Threshold below which a continuous float field (pos/rot/range) is treated as unchanged.</summary>
        public const float Epsilon = 0.01f;

        /// <summary>All vehicle field bits OR'd — sent on the FIRST snapshot of an identity.</summary>
        public const int FullMask =
            GeoStateMask.SurfacePos | GeoStateMask.SurfaceRot | GeoStateMask.RangeRemaining
            | GeoStateMask.Travelling | GeoStateMask.CurrentSite | GeoStateMask.DestinationSites
            | GeoStateMask.HitPoints | GeoStateMask.HostSendTime;

        /// <summary>CONTINUOUS channel bits: pos/rot/range (+ HostSendTime, paired with pos) — ride the UNRELIABLE stream.</summary>
        public const int ContinuousMask =
            GeoStateMask.SurfacePos | GeoStateMask.SurfaceRot | GeoStateMask.RangeRemaining
            | GeoStateMask.HostSendTime;

        /// <summary>DISCRETE channel bits: travelling/currentsite/dest/hp — ride the RELIABLE stream.</summary>
        public const int DiscreteMask =
            GeoStateMask.Travelling | GeoStateMask.CurrentSite | GeoStateMask.DestinationSites
            | GeoStateMask.HitPoints;

        private readonly Dictionary<(string, int), GeoVehicleStateRecord> _lastSent
            = new Dictionary<(string, int), GeoVehicleStateRecord>();
        private readonly Dictionary<(string, int), ulong> _seqByIdentity
            = new Dictionary<(string, int), ulong>();

        /// <summary>
        /// Diff a fresh authoritative snapshot against the last sent for its identity. Returns a record
        /// carrying the snapshot's field VALUES with <c>ChangedMask</c> set to the changed bits and a
        /// freshly assigned <c>Seq</c>. ChangedMask==0 means nothing changed — caller emits nothing.
        /// </summary>
        public GeoVehicleStateRecord Diff(GeoVehicleStateRecord current)
        {
            var key = (current.FactionGuid ?? "", current.VehicleID);

            // FIRST snapshot of this identity: full mask, seq 1, heavy first mirror on the client.
            if (!_lastSent.TryGetValue(key, out var last))
            {
                const ulong firstSeq = 1UL;
                _seqByIdentity[key] = firstSeq;
                _lastSent[key] = current;
                current.ChangedMask = FullMask;
                current.Seq = firstSeq;
                return current;
            }

            int mask = ComputeChangedMask(last, current);

            // Nothing moved: emit nothing, do NOT advance the seq or the last-sent baseline (so sub-epsilon
            // drift keeps accumulating against the value we actually SENT, not the last value we saw).
            if (mask == 0)
            {
                current.ChangedMask = 0;
                current.Seq = _seqByIdentity.TryGetValue(key, out var s) ? s : 0UL;
                return current;
            }

            ulong newSeq = (_seqByIdentity.TryGetValue(key, out var prev) ? prev : 0UL) + 1UL;
            _seqByIdentity[key] = newSeq;
            _lastSent[key] = current; // new baseline = the snapshot we are about to send
            current.ChangedMask = mask;
            current.Seq = newSeq;
            return current;
        }

        /// <summary>Drop all per-identity state so a fresh co-op session starts clean (no stale seq/baseline).</summary>
        public void Reset()
        {
            _lastSent.Clear();
            _seqByIdentity.Clear();
        }

        /// <summary>The CONTINUOUS subset of a mask (pos/rot/range) — for the UNRELIABLE channel.</summary>
        public static int ContinuousBits(int mask) => mask & ContinuousMask;

        /// <summary>The DISCRETE subset of a mask (travelling/site/dest/hp) — for the RELIABLE channel.</summary>
        public static int DiscreteBits(int mask) => mask & DiscreteMask;

        // Build the changed-field mask: epsilon on continuous pos/rot/range, exact on discrete fields.
        private static int ComputeChangedMask(GeoVehicleStateRecord last, GeoVehicleStateRecord cur)
        {
            int mask = 0;

            if (Differs(cur.PosX, last.PosX) || Differs(cur.PosY, last.PosY) || Differs(cur.PosZ, last.PosZ))
                mask |= GeoStateMask.SurfacePos | GeoStateMask.HostSendTime; // HostSendTime always rides with a pos move

            if (Differs(cur.RotX, last.RotX) || Differs(cur.RotY, last.RotY)
                || Differs(cur.RotZ, last.RotZ) || Differs(cur.RotW, last.RotW))
                mask |= GeoStateMask.SurfaceRot;

            if (Differs(cur.RangeRemaining, last.RangeRemaining))
                mask |= GeoStateMask.RangeRemaining;

            if (cur.Travelling != last.Travelling)
                mask |= GeoStateMask.Travelling;

            if (cur.CurrentSiteId != last.CurrentSiteId)
                mask |= GeoStateMask.CurrentSite;

            if (!IntArraysEqual(cur.DestinationSiteIds, last.DestinationSiteIds))
                mask |= GeoStateMask.DestinationSites;

            // HitPoints is integral in the engine (filled from int Stats.HitPoints) → exact compare, no epsilon.
            if (cur.HitPoints != last.HitPoints)
                mask |= GeoStateMask.HitPoints;

            return mask;
        }

        // Continuous-field change test: above the epsilon threshold counts as a real move.
        private static bool Differs(float a, float b) => Math.Abs(a - b) > Epsilon;

        // Ordered int[] equality, treating null and empty as equal (no false DestinationSites change).
        private static bool IntArraysEqual(int[] a, int[] b)
        {
            int la = a?.Length ?? 0;
            int lb = b?.Length ?? 0;
            if (la != lb) return false;
            for (int i = 0; i < la; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }
    }
}
