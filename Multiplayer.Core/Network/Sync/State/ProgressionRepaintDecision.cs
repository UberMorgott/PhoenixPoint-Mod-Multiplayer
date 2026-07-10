using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Pure decision for the progression-panel (stats / SP / abilities) mirror repaint. Reactivity mandate:
    /// a relevant remote apply is NEVER deferred forever (a watching, non-committing peer used to see nothing
    /// until it committed or switched soldier — stat-sync RCA 2026-07-10). The panel holds an uncommitted
    /// local +/- / ability buffer and a FULL re-drive (<c>SetCharacterProgression</c>) re-snapshots that buffer
    /// from the model, so once an edit is pending the decision splits three ways:
    ///   • no pending edit → <see cref="Outcome.Repaint"/> (full re-drive; nothing to lose);
    ///   • pending edit, apply touched a DIFFERENT soldier but moved the shared faction-SP pool (or the caller
    ///     cannot say what changed) → <see cref="Outcome.PartialRepaint"/> — refresh the shared pool label,
    ///     KEEP the edit buffer (never SetCharacterProgression);
    ///   • pending edit, apply POSITIVELY stamped the OPEN soldier → <see cref="Outcome.ConflictRepaint"/> —
    ///     full re-drive, remote wins, the local uncommitted clicks are discarded.
    /// Defer is GONE: no code path returns it.
    /// </summary>
    public static class ProgressionRepaintDecision
    {
        public enum Outcome
        {
            /// <summary>Full re-drive from the model now (no pending edit, or a relevant idle apply).</summary>
            Repaint,
            /// <summary>Pending edit + different soldier: refresh the shared faction-SP pool label only, keep the buffer.</summary>
            PartialRepaint,
            /// <summary>Pending edit + the OPEN soldier changed remotely: full re-drive, remote wins, local clicks discarded.</summary>
            ConflictRepaint,
            /// <summary>Nothing shown on the panel changed — keep the buffer, do nothing.</summary>
            Skip,
        }

        /// <summary>Decide the panel action for one authoritative apply / fan-out kick.
        /// <paramref name="stampedUnitIds"/> null = caller cannot say what changed → conservative (a missed
        /// repaint is a stale mirror forever, but an unknown apply must never DISCARD a pending edit — that is
        /// a PartialRepaint, not a Conflict); empty = nothing stamped. <paramref name="factionSpChanged"/> =
        /// the shared faction-SP pool value (displayed on the panel for every soldier) changed.</summary>
        public static Outcome Decide(bool hasPendingLocalEdit, long openUnitId,
                                     IReadOnlyList<long> stampedUnitIds, bool factionSpChanged)
        {
            if (!hasPendingLocalEdit)
            {
                bool relevant = factionSpChanged || AugmentRepaintDecision.ShouldRepaint(openUnitId, stampedUnitIds);
                return relevant ? Outcome.Repaint : Outcome.Skip;
            }

            // Pending local edit: only a POSITIVE hit on the open soldier discards the buffer (ConflictRepaint).
            // A null/unknown stamp is NOT a positive hit — it must not eat the user's uncommitted clicks.
            if (OpenUnitStamped(openUnitId, stampedUnitIds)) return Outcome.ConflictRepaint;
            if (factionSpChanged || stampedUnitIds == null) return Outcome.PartialRepaint;
            return Outcome.Skip;
        }

        /// <summary>FIX 1 over-committed-pool guard (pure arithmetic; the reflective caller is
        /// <c>GeoUiRefresh.PartialRepaintProgression</c>). A PartialRepaint shifts the panel's faction-SP
        /// baseline (<paramref name="startingFactionPoints"/>) to the live shared pool
        /// (<paramref name="liveSkillpoints"/>) while preserving the local pending draw (starting − current,
        /// always ≥ 0: native clamps current ≤ starting). If the live pool can no longer cover that pending draw
        /// the shift would set <c>_currentFactionPoints</c> negative (an over-spend the host will reject) — the
        /// caller must instead do a full ConflictRepaint (remote wins, discard the buffer). Returns
        /// <c>true</c> = the partial shift is safe and <paramref name="shiftedCurrentFactionPoints"/> holds the
        /// new current (guaranteed ≥ 0); <c>false</c> = over-committed.</summary>
        public static bool CanPartialShiftFactionSp(int liveSkillpoints, int startingFactionPoints,
            int currentFactionPoints, out int shiftedCurrentFactionPoints)
        {
            int pendingDraw = startingFactionPoints - currentFactionPoints;
            if (pendingDraw < 0) pendingDraw = 0;   // defensive: never amplify a (nonexistent) negative draw
            shiftedCurrentFactionPoints = liveSkillpoints - pendingDraw;
            return shiftedCurrentFactionPoints >= 0;
        }

        /// <summary>True only when the apply POSITIVELY stamped the open soldier — false for a null/unknown
        /// stamp or an unresolved open id (0), both of which must not discard a pending edit buffer.</summary>
        private static bool OpenUnitStamped(long openUnitId, IReadOnlyList<long> stampedUnitIds)
        {
            if (stampedUnitIds == null || openUnitId == 0) return false;
            for (int i = 0; i < stampedUnitIds.Count; i++)
                if (stampedUnitIds[i] == openUnitId) return true;
            return false;
        }
    }
}
