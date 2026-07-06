using System.Collections.Generic;

namespace Multiplayer.Network.Sync.State
{
    /// <summary>
    /// PURE (Unity-free) client-side ORIGIN TAG for blocking report modals: which blocking ModalTypes are
    /// currently shown BY THE MIRROR (host-originated, <c>GeoModalDisplay.Show</c>) on this client.
    ///
    /// WHY (soak 2026-07-05, client resource-site brief with a dead Cancel): the view-lock patches
    /// (<c>BlockingModalClientLockPatches</c>) keyed on modal TYPE alone — ANY client-shown blocking-type window
    /// got its FinishDialog/OnCancel swallowed and its buttons greyed, regardless of who opened it. Two failure
    /// modes:
    ///   1. ORIGIN: a client-NATIVE blocking-type window (any opener that bypasses the suppressed
    ///      GeoscapeView.OpenModal chokepoints — e.g. a TFTV direct state push) became unclosable: no host
    ///      ReportModalHide will ever arrive for a window the host never opened.
    ///   2. RACE (hide-before-show): the mirrored Show is a QUEUED state switch; a fast host cancel lands
    ///      ReportModalHide while the modal is still queued → CloseBlocking found "no matching modal current"
    ///      and no-oped → the modal then entered LOCKED with no future hide → permanently dead Cancel.
    /// One mechanism fixes both: <c>GeoModalDisplay.Show</c> marks the type mirror-shown at queue time;
    /// <c>CloseBlocking</c> ALWAYS clears it (even when nothing is current yet — the race case); the lock
    /// decisions (<see cref="Multiplayer.Network.Sync.BlockingModalLockDecision"/>) require the tag. A window
    /// whose tag is absent — native origin, or already released by the host — keeps its NATIVE buttons
    /// (Cancel closes locally; the mirrored DialogCallback is null so a close never mutates host state).
    ///
    /// Reset at every save-transfer/reload boundary (<c>SyncEngine.ResetEventMirror</c>) — a stale tag from a
    /// dead geoscape must never lock a later native window of the same type. Session end is implicitly safe:
    /// the lock decisions also require an active session (fail-open), and Esc → OnCancel then closes natively.
    /// </summary>
    public static class BlockingModalMirrorRegistry
    {
        private static readonly HashSet<int> _mirrorShown = new HashSet<int>();

        /// <summary>Client: tag <paramref name="modalType"/> as mirror-shown (called when the mirrored
        /// blocking modal is queued). Idempotent.</summary>
        public static void MarkMirrorShown(int modalType)
        {
            lock (_mirrorShown) _mirrorShown.Add(modalType);
        }

        /// <summary>True iff a mirrored blocking modal of this type is pending/shown (lock decisions key on this).</summary>
        public static bool IsMirrorShown(int modalType)
        {
            lock (_mirrorShown) return _mirrorShown.Contains(modalType);
        }

        /// <summary>Client: drop the tag — the host resolved (ReportModalHide) or the mirror closed. ALWAYS
        /// called on a hide, even when no matching window is current yet (hide-before-show race: the queued
        /// window then enters UNLOCKED and the user can close it natively).</summary>
        public static void ClearMirrorShown(int modalType)
        {
            lock (_mirrorShown) _mirrorShown.Remove(modalType);
        }

        /// <summary>Boundary belt (save-transfer/reload/session end): never inherit a stale mirror tag.</summary>
        public static void Reset()
        {
            lock (_mirrorShown) _mirrorShown.Clear();
        }
    }
}
