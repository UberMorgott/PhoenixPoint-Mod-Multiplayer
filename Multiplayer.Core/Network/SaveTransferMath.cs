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
        /// Barrier-release predicate (fix #1/#2): the LOADED barrier releases iff the host has prepared
        /// AND every currently-expected client has acked. The host is counted via a dedicated flag, never
        /// an id in <paramref name="loadedClientCount"/>, so a peerId-0 client can never masquerade as the
        /// host. When a not-yet-loaded peer drops, the caller passes the reduced live
        /// <paramref name="expectedClientCount"/>, so the barrier releases early with the rest.
        /// </summary>
        public static bool BarrierReleased(bool hostLoaded, int loadedClientCount, int expectedClientCount)
            => hostLoaded && loadedClientCount >= expectedClientCount;

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
    }
}
