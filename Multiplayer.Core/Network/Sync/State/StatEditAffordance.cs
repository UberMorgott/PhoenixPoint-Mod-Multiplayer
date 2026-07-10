using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Per-peer OPTIMISTIC local click counter for the thin-client stat editor (minus-button affordance,
    /// 2026-07-11). Under thin-client, every +/- click is a pure input: the module's edit buffer is pinned
    /// <c>_current*Stat == _starting*Stat == model</c> (RebaseModuleToLiveModel), so the NATIVE minus-button gate
    /// <c>SetStatButtonInteractabilty</c> (<c>_current > _starting</c>) is permanently false — the minus button
    /// can never light up, so a refund click can never be issued. This tracks, per (unit, stat), the NET local
    /// plus-clicks made in THIS geoscape session that have not yet been refunded — a Harmony postfix forces the
    /// minus button interactable whenever the net is &gt; 0.
    ///
    /// UI-AFFORDANCE ONLY — authority stays the host's <see cref="StatRefundTracker"/> (a forced-enabled minus
    /// still routes through the host ledger cap; an over-enabled button just yields a no-op refund). Increment is
    /// OPTIMISTIC (on the local +1 click); decrement is CONFIRMED — <see cref="ObserveLiveStat"/> lowers the net
    /// only when the authoritative live stat is observed to DROP (a refund landed), so a host-DENIED click never
    /// decrements. One-directional: a live RISE is a confirmed spend already counted at click time. Pure static
    /// (BCL only, no engine glue) so it links into the pure test build. Host-single-threaded (Unity main thread),
    /// so a plain dictionary needs no locking. Reset on session start/end, unit dismissal, and the mid-session
    /// reload boundary — a reused GeoUnitId must never inherit a prior session's net.
    /// </summary>
    public static class StatEditAffordance
    {
        // unitId → [0..2] net optimistic plus-clicks (≥ 0, CharacterBaseAttribute order Strength/Will/Speed);
        // [3..5] last observed authoritative live stat (−1 = not yet observed). One row = one lifetime + reset.
        private static readonly Dictionary<long, int[]> _rows = new Dictionary<long, int[]>();

        private static int[] Row(long unitId)
        {
            if (!_rows.TryGetValue(unitId, out int[] r)) { r = new[] { 0, 0, 0, -1, -1, -1 }; _rows[unitId] = r; }
            return r;
        }

        /// <summary>Net optimistic local plus-clicks on (unit, stat) not yet refunded — the minus affordance
        /// gate (&gt; 0 → force the minus button interactable).</summary>
        public static int Net(long unitId, int statId)
        {
            if (statId < 0 || statId > 2) return 0;
            return _rows.TryGetValue(unitId, out int[] r) ? r[statId] : 0;
        }

        /// <summary>Count one LOCAL +1 click optimistically (before host confirmation) — lifts the minus affordance
        /// immediately so the user can undo without waiting for the round-trip.</summary>
        public static void RecordPlus(long unitId, int statId)
        {
            if (statId < 0 || statId > 2) return;
            Row(unitId)[statId]++;
        }

        /// <summary>Feed the AUTHORITATIVE live stat value seen on a panel re-drive. A DROP below the last observed
        /// value = a confirmed refund → lower the net by the drop (floored at 0) and return the amount decremented;
        /// a rise (confirmed spend, already counted at click) or an unchanged value returns 0. Respects the
        /// no-decrement-on-deny rule: a denied click leaves the live stat unchanged → no drop → no decrement.</summary>
        public static int ObserveLiveStat(long unitId, int statId, int liveStat)
        {
            if (statId < 0 || statId > 2) return 0;
            int[] r = Row(unitId);
            int prev = r[3 + statId];
            int dec = 0;
            if (prev >= 0 && liveStat < prev)
            {
                dec = prev - liveStat;
                r[statId] = r[statId] > dec ? r[statId] - dec : 0;
            }
            r[3 + statId] = liveStat;
            return dec;
        }

        /// <summary>Drop a unit's counter (dismissal — a reused GeoUnitId must not inherit its net).</summary>
        public static void ResetUnit(long unitId) => _rows.Remove(unitId);

        /// <summary>Clear the whole counter (co-op session start/end, mid-session reload boundary).</summary>
        public static void ResetSession() => _rows.Clear();
    }
}
