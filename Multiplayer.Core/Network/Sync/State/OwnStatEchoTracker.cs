using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Per-peer count of OWN stat-point clicks (per-click stat relay 2026-07-10) whose authoritative #9 echo has
    /// not yet been reconciled onto the local progression panel. Each +/- click a peer OWNS is registered here;
    /// when the resulting authoritative apply stamps that same open soldier the panel would normally
    /// <c>ConflictRepaint</c> (SetCharacterProgression → RefreshStats resets <c>_starting*Stat</c> → live), which
    /// closes the native minus gate (<c>ChangeCharacterStat</c> decompile :907 needs current−1 ≥ starting) sub-second
    /// after every click and forbids undoing a mis-click. So a self-echo is downgraded to a buffer-preserving
    /// <c>PartialRepaint</c>: the open panel keeps its optimistic buffer and <c>_starting</c> stays at the panel-open
    /// baseline, so the minus gate stays open down to session-start (the HOST <see cref="StatRefundTracker"/> bounds
    /// the actual refund). Only a FOREIGN change to the open soldier (count 0) still ConflictRepaints.
    ///
    /// Symmetric across peers: a co-op CLIENT registers on relay, the HOST registers on its own self-apply — each
    /// consumes only ITS OWN echoes (the other peer's count is 0). Keyed on unitId (the #9 blob is a full-soldier
    /// snapshot, one echo per unit). Host/client each single-threaded on the Unity main thread → plain static dict,
    /// no locking. Reset on session start/end, on unit dismissal, and on the commit seam (panel switch/exit) so a
    /// coalesced-echo residue can never outlive the current soldier's edit session.
    /// ponytail: decrement-by-1 per imminent conflict; if the host coalesces several clicks into one #9 the count can
    /// drift high, delaying a FOREIGN non-stat repaint of the SAME owned soldier until panel teardown — bounded, self-
    /// heals on switch, and a foreign STAT edit of an owned soldier can't happen (per-soldier ownership gate). Upgrade
    /// to value-diff self-echo detection only if that ever bites.
    /// </summary>
    public static class OwnStatEchoTracker
    {
        // unitId → outstanding own stat-click echoes not yet reconciled onto the open panel (≥ 0).
        private static readonly Dictionary<long, int> _outstanding = new Dictionary<long, int>();

        /// <summary>Register one OWN stat +/- click on <paramref name="unitId"/> (client relay / host self-apply):
        /// its authoritative #9 echo, when it stamps this soldier, is a self-echo — not a foreign conflict.</summary>
        public static void RegisterOwnClick(long unitId)
        {
            _outstanding.TryGetValue(unitId, out int n);
            _outstanding[unitId] = n + 1;
        }

        /// <summary>If <paramref name="unitId"/> has an outstanding own click, consume one and return true (this
        /// imminent-conflict apply is a self-echo → keep the buffer, do NOT reset the baseline). False = no own
        /// echo pending → a genuine foreign change, let the ConflictRepaint proceed.</summary>
        public static bool TryConsumeEcho(long unitId)
        {
            if (!_outstanding.TryGetValue(unitId, out int n) || n <= 0) return false;
            if (n == 1) _outstanding.Remove(unitId); else _outstanding[unitId] = n - 1;
            return true;
        }

        /// <summary>Outstanding own-echo count for a unit (diagnostics / tests).</summary>
        public static int Outstanding(long unitId) => _outstanding.TryGetValue(unitId, out int n) ? n : 0;

        /// <summary>Drop a unit's outstanding echoes (commit seam = panel switch/exit, or dismissal).</summary>
        public static void ResetUnit(long unitId) => _outstanding.Remove(unitId);

        /// <summary>Clear all outstanding echoes (co-op session start/end).</summary>
        public static void ResetSession() => _outstanding.Clear();
    }
}
