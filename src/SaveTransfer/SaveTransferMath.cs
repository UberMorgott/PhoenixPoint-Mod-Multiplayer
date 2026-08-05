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
        /// PER-PEER REVEAL-BARRIER HOLD (developer ruling, 2026-08-05): "without options EVERYONE must load,
        /// in order to start. And if someone hard-crashes — the process died, the connection broke — we wait
        /// for them; if nothing is happening, then they get dropped and we load on without them."
        ///
        /// So the release criterion is LIVENESS AND PROGRESS, never a wall clock. One roster slot holds the
        /// synchronized reveal for as long as it is (a) not loaded, (b) still MOVING — its download percent or
        /// native-load percent advanced within the grace window — and (c) still AUDIBLE — some packet arrived
        /// from it within the same window. A slow disk or a lossy link costs nobody their transition: both
        /// clocks keep being bumped, so the wait is unbounded BY DESIGN. Only the absence of BOTH signals ends
        /// it, and ending it drops the peer from this BARRIER only — never from the session (law L84: it keeps
        /// its row, slot, permissions and guid binding, and re-converges through the on-demand join).
        /// </summary>
        public static bool HoldsBarrier(bool loadComplete, long msSinceProgress, long msSinceHeard, long graceMs)
            => !loadComplete && msSinceProgress <= graceMs && msSinceHeard <= graceMs;

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
