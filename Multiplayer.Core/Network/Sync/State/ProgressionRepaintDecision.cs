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

        /// <summary>Reconcile the panel's faction-SP buffer to the authoritative shared pool for a PartialRepaint
        /// (pure; the reflective caller is <c>GeoUiRefresh.PartialRepaintProgression</c>). Under per-click stat relay
        /// every applied +/- click is ALREADY committed to the live pool AT THE CLICK (HOST: SpendStatPoints hits
        /// <c>GeoPhoenixFaction.Skillpoints</c> synchronously; CLIENT: the #9 echo moves the mirror pool), so the
        /// buffer draw (<paramref name="startingFactionPoints"/> − <paramref name="currentFactionPoints"/>) is SPENT,
        /// not pending — the panel's remaining faction SP == <paramref name="liveSkillpoints"/> outright, and BOTH
        /// <c>_starting/_currentFactionPoints</c> shift to it (an applied draw reserves nothing). The pre-per-click
        /// formula subtracted that draw a SECOND time (<c>live − draw</c>), double-debiting the pool it was already
        /// spent from → the host saw its own SP vanish and the commit seam wrote the under-counted value back to the
        /// authoritative pool (per-click applied-draw accounting 2026-07-10). Returns the live pool (always ≥ 0: an
        /// authoritative pool never goes negative — so no over-commit escalation exists any more).
        /// <paramref name="startingFactionPoints"/>/<paramref name="currentFactionPoints"/> are taken only so a test
        /// can pin that the reconcile IGNORES the buffer draw. The per-stat minus gate is untouched (it keys on
        /// <c>_starting*Stat</c>, not the pool).</summary>
        public static int ReconcileFactionSpToLive(int liveSkillpoints, int startingFactionPoints,
            int currentFactionPoints) => liveSkillpoints;

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
