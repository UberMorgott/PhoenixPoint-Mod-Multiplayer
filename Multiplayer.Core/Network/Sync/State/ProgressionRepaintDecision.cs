using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// Pure decision for the progression-panel (stats / SP / abilities) mirror repaint — the
    /// <see cref="AugmentRepaintDecision"/> sibling with the panel's extra dimensions: the shared
    /// faction-SP pool is DISPLAYED on the panel (a pool-only apply must still repaint), and the panel
    /// holds an uncommitted local edit buffer (a relevant apply mid-edit is DEFERRED and owed a drain
    /// on the commit/clear seam, never skipped forever — reactivity mandate). Repainting re-snapshots
    /// the buffer from the model, so an unrelated apply must never reach it while an edit is pending.
    /// </summary>
    public static class ProgressionRepaintDecision
    {
        public enum Outcome
        {
            /// <summary>Re-drive the panel from the model now (also consumes an owed deferred repaint).</summary>
            Repaint,
            /// <summary>Relevant apply behind a pending local allocation — arm the owed flag, drain later.</summary>
            Defer,
            /// <summary>Nothing shown on the panel changed — keep the buffer, do nothing.</summary>
            Skip,
        }

        /// <summary>Decide the panel action for one authoritative apply / fan-out kick.
        /// <paramref name="stampedUnitIds"/> null = caller cannot say what changed → conservative
        /// relevant (a missed repaint is a stale mirror forever); empty = nothing stamped.
        /// <paramref name="factionSpChanged"/> = the shared faction-SP pool value changed (visible on
        /// the panel for every soldier). <paramref name="repaintOwed"/> = a prior relevant apply was
        /// deferred behind a pending edit and has not drained yet.</summary>
        public static Outcome Decide(bool hasPendingLocalEdit, bool repaintOwed, long openUnitId,
                                     IReadOnlyList<long> stampedUnitIds, bool factionSpChanged)
        {
            bool relevant = factionSpChanged || AugmentRepaintDecision.ShouldRepaint(openUnitId, stampedUnitIds);
            if (hasPendingLocalEdit) return relevant ? Outcome.Defer : Outcome.Skip;
            return relevant || repaintOwed ? Outcome.Repaint : Outcome.Skip;
        }
    }
}
