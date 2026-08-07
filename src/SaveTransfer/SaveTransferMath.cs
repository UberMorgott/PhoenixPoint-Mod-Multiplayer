namespace Multiplayer.Network
{
    /// <summary>
    /// Pure, Unity-free save-transfer load-barrier + chunk-grid predicates. Extracted from the
    /// game-bound <c>SaveTransferCoordinator</c> (which binds <c>NetworkEngine</c> / UnityEngine /
    /// game save types) so the intended behaviour is the SINGLE source of truth and is directly
    /// unit-testable here without any game DLL. <c>SaveTransferCoordinator</c> forwards to these.
    /// </summary>
    public static class SaveTransferMath
    {
        /// <summary>
        /// Barrier-release predicate. NO QUORUM (N=50 mandate, 2026-08-05): the session begins the moment
        /// the HOST has prepared its own entry. It used to also require <c>loadedClientCount &gt;=
        /// expectedClientCount</c> — a peer-count decision, and the reason a single slow download made
        /// fifty players sit behind a loading screen for up to three minutes and then watch someone get
        /// kicked. Every client that has not finished converges on its own clock through the ordinary
        /// per-peer join path; SessionBegin is reliable and ordered, so one that is still downloading
        /// simply enters later. Nobody waits for anybody, and there is no straggler to kick.
        /// </summary>
        public static bool BarrierReleased(bool hostLoaded) => hostLoaded;

        /// <summary>
        /// Chunk-grid validator (fix #4): a well-formed chunk sits exactly on the
        /// <paramref name="chunkSize"/> grid (offset a non-negative multiple of chunkSize) and lies fully
        /// within [0, <paramref name="totalLen"/>). Returns the grid index (offset/chunkSize) only when all
        /// hold; rejects (false, index=-1) a malformed/out-of-range offset instead of mis-mapping it.
        /// </summary>
        public static bool TryChunkIndex(long offset, int chunkLen, int totalLen, int chunkSize, out int index)
        {
            index = -1;
            if (chunkSize <= 0 || chunkLen < 0) return false;
            if (offset < 0 || offset % chunkSize != 0) return false;        // off the grid
            if (offset + chunkLen > totalLen) return false;                 // out of bounds
            index = (int)(offset / chunkSize);
            return true;
        }

        /// <summary>
        /// Curtain-lift hold predicate (CS-style all-loaded barrier): during a live, started co-op
        /// session, EVERY native curtain lift is parked until the synchronized reveal (RevealAll →
        /// Revealed). HOLD ⇔ engine active AND session started AND not yet revealed. Any teardown
        /// (engine inactive) or the reveal itself opens the gate, so a parked lift can never hang
        /// forever: RevealAll fires on roster all-done, the roster SHRINKS when a peer drops
        /// (peer-left → live AllDone re-check), and the host deadline / per-peer self-reveal are the
        /// bounded belts. Evaluated LIVE each frame by the gate coroutine.
        /// </summary>
        public static bool HoldCurtain(bool engineActive, bool sessionStarted, bool revealed)
            => engineActive && sessionStarted && !revealed;

        /// <summary>
        /// THE HOLD'S ARM — which windows a co-op load boundary owns this peer's screen for. It is NOT
        /// simply "the session has begun", and that gap is the 2026-08-06 report: on the host's NEW
        /// CAMPAIGN the native flow reaches a playable, INTERACTIVE geoscape long before any barrier
        /// exists. <c>_begun</c> is set by <c>Begin()</c>, which runs inside <c>LaunchTransfer</c>, which
        /// runs only once the campaign has been created AND autosaved — measured live at 20:36:40.948
        /// against a curtain that had already lifted UNHELD at 20:36:37.633 ("engineActive=True
        /// sessionStarted=False revealed=False", multiplayer-2.log:40). For those seconds the host had a
        /// live geoscape — its DiffEngine was already shipping 233 changed fields at 20:36:38.778 — while
        /// two clients still sat in the lobby without a byte of the save. That is the desync the barrier
        /// exists to prevent, arriving through the ARM instead of through the release.
        ///
        /// So the arm starts at the ARMED BOOTSTRAP, not at BEGIN. The two windows were said to "abut with
        /// no gap by construction: <c>LaunchTransfer</c> returns only after <c>Begin()</c> has set the
        /// session started". THAT PREMISE IS FALSE, and it is the 2026-08-07 "the host loads twice on a new
        /// game" report. <c>LaunchTransfer</c>'s last two lines are <c>timing.Start(HostSerializeAndSendCrt)</c>
        /// and <c>return true</c>: it STARTS the coroutine and returns on the same frame, while
        /// <c>Begin()</c> is several yields away — behind <c>ReadSavegameBinary</c>, <c>SendBlob</c> and the
        /// host's own <c>PrepareEntryFromBlobCrt</c>, i.e. behind a whole level load.
        /// <c>ConcludeNewCampaignBootstrap</c> runs the instant <c>LaunchTransfer</c> returns, so for that
        /// entire window BOTH inputs are false: the freshly created geoscape is revealed and interactive,
        /// and then the blob re-entry drops a SECOND loading screen on top of it. One boundary, two
        /// loading screens with a live world flashing between them.
        ///
        /// THE THIRD INPUT IS THE ANNOUNCEMENT ITSELF, which is the honest expression of the rule and not a
        /// patch for one seam: <c>_loadBoundaryAnnounced</c> is set by <c>BroadcastLoadBoundaryBegin</c> —
        /// the packet that tells every OTHER peer to curtain — and cleared by <c>PerformDeferredLift</c>
        /// (the shared reveal) or <c>BroadcastLoadBoundaryAbort</c>. So the host holds its own screen for
        /// exactly as long as the screen it imposed on everyone else, at EVERY announced boundary, and the
        /// gap cannot reopen at the lobby PLAY press either.
        ///
        /// THE BOUND IS THE OUTCOME, NOT A CLOCK (L94 e2 unchanged). This adds no deadline to anybody's
        /// wait: the pending flag is cleared by <c>NewCampaignBootstrap.Conclude</c>, which is reached on
        /// the launch AND on every failure path AND on the latch's own liveness watchdog — and every one of
        /// those failure paths now also ABORTS the announced boundary, which is what clears the third input
        /// and un-curtains the clients with it. A bootstrap that dies therefore RELEASES every peer's
        /// screen — loudly, on the same call that logs the ERROR — instead of stranding them.
        /// </summary>
        public static bool CurtainHoldArmed(bool sessionStarted, bool newCampaignPending,
                                            bool loadBoundaryAnnounced)
            => sessionStarted || newCampaignPending || loadBoundaryAnnounced;

        /// <summary>
        /// BEGIN'S SINGLE-FIRE GUARD — true means "do NOT broadcast SessionBegin". A BARRIER THAT WAS
        /// OPENED MUST ALWAYS PRODUCE ITS BEGIN, so <paramref name="barrierOpen"/> vetoes the suppression
        /// outright: it is the only one of the three flags that belongs to the transfer currently in
        /// flight, and it is cleared by Begin itself on the very next line.
        ///
        /// Live 2026-08-05, the row this predicate exists for: (begun, !hostEntryHold, barrierOpen). The
        /// host is already in its tactical level so <c>_begun</c> stays true across a tac-entry transfer,
        /// and <c>_hostEntryHold</c> is NOT the entry's to hold — <c>PerformDeferredLift</c> clears it, and
        /// the PREVIOUS load's AllDone fired during the 1151 ms mid-tactical save write. The old guard
        /// (<c>begun &amp;&amp; !hostEntryHold</c>) therefore suppressed the BEGIN of a barrier that had
        /// just opened; both clients had already reset <c>_begun=false</c> on the new transfer's first
        /// chunk and parked for the whole battle, so <c>SessionStarted</c> stayed false and every client
        /// tactical command was dropped, silently, at <c>TacticalCommandSync.LiveEngine()</c>.
        /// </summary>
        public static bool BeginSuppressed(bool begun, bool hostEntryHold, bool barrierOpen)
            => begun && !hostEntryHold && !barrierOpen;
    }
}
