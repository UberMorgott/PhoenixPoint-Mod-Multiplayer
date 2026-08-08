namespace Multiplayer.UI
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
        /// <param name="downloading">SaveTransferCoordinator.IsDownloading — this CLIENT is actively
        ///   receiving the save blob (mid-download, before the curtain/world-load). FIX-3: over WAN the
        ///   ~1 MB save download takes seconds→minutes and precedes BOTH loadStarted and inPhase2, so
        ///   without this signal the download was a blank screen. The host is never downloading and a
        ///   client is only downloading once chunks arrive, so this never re-introduces the lobby popup.</param>
        /// <param name="waitingOnPeers">SaveTransferCoordinator.WaitingOnPeers — THIS peer is behind a co-op
        ///   load boundary (its curtain is down for an announced boundary, the barrier is open, or a reveal
        ///   hold is armed and unreleased). The only STABLE term here: the other three are local load
        ///   signals that go false the moment this peer's own bytes are in while the barrier still holds it,
        ///   and loadStarted additionally drops on every curtain Loading→Playing hand-off — which is what
        ///   blinked the panel in and out on a new-campaign start. Peer-agnostic since 2026-08-08 (it was
        ///   host-only, which is why a client saw NOTHING on a geoscape→mission load); still gated on
        ///   SessionStarted inside the coordinator for its barrier arms, so no lobby popup.</param>
        public static bool ShouldShow(bool loadStarted, bool inPhase2, bool downloading, int expectedPeers, int donePeers,
                                      bool waitingOnPeers = false)
        {
            // No genuine load window open → never show. Gate on the ACTUAL load-start (curtain Loading),
            // phase-2 world-load, this client's active download (FIX-3), OR this peer's own wait behind the
            // co-op load boundary — NOT on the command-time TransferActive: that prevents the overlay from
            // appearing in the lobby the instant the host presses PLAY (the bug). The overlay appears when
            // the download starts, the native loading curtain drops for the mission, or the peer is held at
            // the boundary while the OTHERS are still downloading/loading.
            if (!loadStarted && !inPhase2 && !downloading && !waitingOnPeers) return false;

            // A load window is open: show until every participating peer has reported done.
            bool allPeersDone = expectedPeers <= 0 || donePeers >= expectedPeers;
            return !allPeersDone;
        }
    }
}
