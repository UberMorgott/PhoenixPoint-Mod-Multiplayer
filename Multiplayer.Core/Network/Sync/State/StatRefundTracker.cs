using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// HOST anti-farm ledger for per-click stat-point REFUNDS (per-click stat relay 2026-07-10). A negative
    /// <c>SpendStatPointsAction</c> (the minus button) may return AT MOST the net positive points a peer has
    /// applied to that (unit, stat) THIS geoscape session — so a refund can never bank free SP nor drop a stat
    /// below its session-start value. The host records every APPLIED per-click point (spend +, refund −) here
    /// and caps each refund at the running net (floored at 0). Symmetric pricing (the refund credits the same
    /// per-point cost the spend charged) makes the round-trip SP-neutral; this ledger only bounds the COUNT.
    ///
    /// Keyed on (unitId, statId) — NOT (peer, unit, stat): the per-soldier ownership gate
    /// (<c>PersonnelEditReflection.OwnsSoldier</c>) already scopes each soldier to one editing peer, so a second
    /// key adds nothing. ponytail: (unit, stat) key; upgrade to (peer, unit, stat) only if shared-soldier
    /// co-editing ever ships. Host-only, single-threaded (the sync apply pump is on the Unity main thread), so a
    /// plain static dictionary needs no locking. Reset on session start/end and on unit dismissal so a reused
    /// GeoUnitId from a prior session can never inherit a stale net.
    /// </summary>
    public static class StatRefundTracker
    {
        // unitId → [Strength, Will, Speed] net applied points (each ≥ 0). CharacterBaseAttribute order.
        private static readonly Dictionary<long, int[]> _net = new Dictionary<long, int[]>();

        /// <summary>Pure cap: a refund of <paramref name="requestedPoints"/> may return at most the net applied
        /// so far (floored at 0). Never negative.</summary>
        public static int ClampRefund(int netApplied, int requestedPoints)
        {
            if (requestedPoints <= 0) return 0;
            int avail = netApplied < 0 ? 0 : netApplied;
            return requestedPoints < avail ? requestedPoints : avail;
        }

        /// <summary>Net positive points applied to (unit, stat) this session — the refund ceiling.</summary>
        public static int RefundableCap(long unitId, int statId)
        {
            if (statId < 0 || statId > 2) return 0;
            return _net.TryGetValue(unitId, out int[] arr) ? arr[statId] : 0;
        }

        /// <summary>Record <paramref name="points"/> (≥ 0) authoritatively SPENT on (unit, stat): raises the net,
        /// lifting the refund ceiling.</summary>
        public static void RecordSpend(long unitId, int statId, int points)
        {
            if (statId < 0 || statId > 2 || points <= 0) return;
            if (!_net.TryGetValue(unitId, out int[] arr)) { arr = new int[3]; _net[unitId] = arr; }
            arr[statId] += points;
        }

        /// <summary>Record <paramref name="points"/> (≥ 0) authoritatively REFUNDED on (unit, stat): lowers the
        /// net (floored at 0). Callers pass only the count actually applied (already clamped via
        /// <see cref="ClampRefund"/>).</summary>
        public static void RecordRefund(long unitId, int statId, int points)
        {
            if (statId < 0 || statId > 2 || points <= 0) return;
            if (!_net.TryGetValue(unitId, out int[] arr)) return;
            arr[statId] = arr[statId] > points ? arr[statId] - points : 0;
        }

        /// <summary>Drop a unit's ledger (dismissal — a reused GeoUnitId must not inherit its net).</summary>
        public static void ResetUnit(long unitId) => _net.Remove(unitId);

        /// <summary>Clear the whole ledger (co-op session start/end).</summary>
        public static void ResetSession() => _net.Clear();
    }
}
