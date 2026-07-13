namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE decision core for the per-gesture tactical-inventory rail (<c>TacticalInventorySync.OnTacticalGesture</c>).
    /// Engine glue stays in the mod project (repo pure-core + thin-glue pattern, cf. <see cref="TacticalInventoryViewGuard"/>).
    ///
    /// WHY: <c>ReBaseline</c> resets every <c>UIInventoryList._initialItems</c> — the EXACT diff the native
    /// deferred close-commit (<c>UIStateInventory.ExitState → AttemptMoveItems → GetRemovedItems/GetAddedItems</c>)
    /// and the mod's own close-rail <c>CaptureMoves</c> consume. Re-baselining after an EMPTY gesture diff therefore
    /// destroys a move the diff missed: it is neither relayed nor natively committed — items revert at screen close
    /// on host AND clients. Re-baseline only when the gesture diff actually carried something (relayed moves, or
    /// unsynced drags that get an immediate revert-repaint).
    /// </summary>
    public static class TacticalInventoryGestureDecision
    {
        /// <summary>TRUE when the gesture diff carried anything (relayed moves or unsynced drags) — only then may
        /// the UI lists be re-baselined (double-relay prevention). FALSE on a fully-empty diff: the native
        /// <c>_initialItems</c> baseline must survive so the native close-commit / close-rail fallback still see the
        /// move. Doubles as the "un-relayed local diff pending" detector for the inbound-apply repaint skip.</summary>
        public static bool ShouldReBaseline(int moveCount, int unsyncedCount)
            => moveCount > 0 || unsyncedCount > 0;
    }
}
