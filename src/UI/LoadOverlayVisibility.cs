namespace Multipleer.UI
{
    /// <summary>
    /// Pure, Unity-free decision for whether the co-op load overlay should be visible THIS frame.
    ///
    /// The overlay used to be shown by a single fire-once Harmony postfix on the curtain Loading event,
    /// gated by a volatile (TransferActive || InPhase2) read that a frame-race could flip false right at
    /// the show instant → overlay never appeared ("через раз"). LoadOverlayController now calls this every
    /// frame from its existing per-frame Refresh and Show()/Hide()s idempotently, so visibility self-heals
    /// regardless of which transition path or frame-race happened.
    ///
    /// VISIBLE  ⇔  (loadStarted || inPhase2)   // the NATIVE loading curtain has actually started the load
    ///             &amp;&amp; !allPeersDone         // …and not every participating peer has reported done
    ///
    /// The gate keys on loadStarted (curtain entered "Loading" = SaveTransferCoordinator.LoadPhaseStarted),
    /// NOT on the command-time TransferActive. TransferActive goes true the instant the host presses PLAY
    /// (barrier open) while still in the LOBBY, which used to pop the overlay too early; loadStarted is true
    /// only once the real mission-loading curtain has dropped, so the overlay now appears exactly at load.
    ///
    /// Completion is read from the AUTHORITATIVE per-peer tracker done-set (donePeers/expectedPeers), NOT
    /// the volatile _begun/_loadCompleteSent, so the overlay can't get stuck visible. When neither the load
    /// curtain nor phase-2 is active there is no genuine co-op load happening → FALSE, so leftover/stale
    /// flags never keep it on screen.
    /// </summary>
    public static class LoadOverlayVisibility
    {
        /// <param name="transferActive">SaveTransferCoordinator.TransferActive — host barrier open, this
        ///   client mid-download, or a prepared save awaiting BEGIN.</param>
        /// <param name="inPhase2">SaveTransferCoordinator.InPhase2 — this peer is in phase-2 native world-load.</param>
        /// <param name="expectedPeers">Count of participating roster slots (RosterProgressTracker done is
        ///   tracked per slot). 0 means no participants → treated as "all done" (nothing to wait on).</param>
        /// <param name="donePeers">How many of those slots have reported LoadComplete (tracker.IsDone).</param>
        /// <param name="loadStarted">SaveTransferCoordinator.LoadPhaseStarted — the native loading
        ///   curtain has actually entered "Loading" (mission load-start). This is the gate that keeps the
        ///   overlay HIDDEN in the lobby after PLAY: it is FALSE while only the barrier/download is open
        ///   (that earlier signal is TransferActive, which goes true at command time, still in the lobby).</param>
        /// <param name="inPhase2">SaveTransferCoordinator.InPhase2 — this peer is in phase-2 native world-load.</param>
        /// <param name="expectedPeers">Count of participating roster slots (RosterProgressTracker done is
        ///   tracked per slot). 0 means no participants → treated as "all done" (nothing to wait on).</param>
        /// <param name="donePeers">How many of those slots have reported LoadComplete (tracker.IsDone).</param>
        public static bool ShouldShow(bool loadStarted, bool inPhase2, int expectedPeers, int donePeers)
        {
            // No genuine load window open → never show. Gate on the ACTUAL load-start (curtain Loading)
            // or phase-2 world-load, NOT on the command-time TransferActive: that prevents the overlay
            // from appearing in the lobby the instant the host presses PLAY (the bug). The overlay now
            // appears exactly when the native loading curtain drops for the mission.
            if (!loadStarted && !inPhase2) return false;

            // A load window is open: show until every participating peer has reported done.
            bool allPeersDone = expectedPeers <= 0 || donePeers >= expectedPeers;
            return !allPeersDone;
        }
    }
}
