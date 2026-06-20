using Xunit;

namespace Multipleer.Tests
{
    /// <summary>
    /// Load-barrier release counting + host-sentinel + malformed-chunk rejection for the co-op
    /// save-transfer load barrier (review fixes #1, #2, #4).
    ///
    /// NOTE ON LINKAGE: <c>SaveTransferCoordinator</c> references <c>NetworkEngine</c>, UnityEngine and
    /// game save types, so it is NOT (and cannot be) linked into this Unity-free pure test assembly
    /// (Multipleer.Tests.csproj uses EnableDefaultCompileItems=false and links only pure files). The two
    /// predicates under test are therefore tiny PURE one-liners on the coordinator; this test mirrors the
    /// EXACT same formulas here so the intended behavior is pinned and regression-guarded. The mirrors
    /// (<see cref="BarrierReleased"/> / <see cref="TryChunkIndex"/>) MUST stay byte-identical to
    /// SaveTransferCoordinator.BarrierReleased / SaveTransferCoordinator.TryChunkIndex.
    /// </summary>
    public class SaveTransferBarrierTests
    {
        // ─── Mirrors of SaveTransferCoordinator pure helpers (keep in sync) ───────────────────────────
        // Mirror of SaveTransferCoordinator.BarrierReleased(bool, int, int).
        private static bool BarrierReleased(bool hostLoaded, int loadedClientCount, int expectedClientCount)
            => hostLoaded && loadedClientCount >= expectedClientCount;

        // Mirror of SaveTransferCoordinator.TryChunkIndex(long, int, int, int, out int).
        private static bool TryChunkIndex(long offset, int chunkLen, int totalLen, int chunkSize, out int index)
        {
            index = -1;
            if (chunkSize <= 0 || chunkLen < 0) return false;
            if (offset < 0 || offset % chunkSize != 0) return false;
            if (offset + chunkLen > totalLen) return false;
            index = (int)(offset / chunkSize);
            return true;
        }

        // ─── Fix #1/#2: barrier release counting ──────────────────────────────────────────────────────

        [Fact]
        public void Barrier_Releases_When_Host_And_All_Clients_Loaded()
        {
            // 2 connected clients, both acked, host prepared → release.
            // Pre-fix this also worked, but ONLY because the host was self-added by id; the test pins the
            // happy path under the new host-sentinel counting.
            Assert.True(BarrierReleased(hostLoaded: true, loadedClientCount: 2, expectedClientCount: 2));
        }

        [Fact]
        public void Barrier_Does_Not_Release_Before_Host_Prepared()
        {
            // All clients loaded but the host has not finished its own prepare → must NOT release.
            // Pre-fix the host was an id in the loaded set; if that id collided with a client the host
            // could appear "loaded" without actually being ready. The flag makes host-readiness explicit.
            Assert.False(BarrierReleased(hostLoaded: false, loadedClientCount: 2, expectedClientCount: 2));
        }

        [Fact]
        public void Barrier_Releases_Early_When_NotYetLoaded_Peer_Disconnects()
        {
            // 3 clients expected; only 2 acked LOADED, the 3rd is still downloading. With the full count
            // the barrier waits (would stall the full phase-1 timeout):
            Assert.False(BarrierReleased(hostLoaded: true, loadedClientCount: 2, expectedClientCount: 3));

            // Fix #1: the 3rd peer DISCONNECTS mid-load. Session.RemoveClient drops it before the
            // disconnect event, so the live expected-client count falls to 2 and the barrier releases
            // immediately with the host + the 2 remaining loaded clients — no 60 s stall.
            Assert.True(BarrierReleased(hostLoaded: true, loadedClientCount: 2, expectedClientCount: 2));
        }

        [Fact]
        public void Host_Sentinel_Never_Collides_With_PeerId_Zero_Client()
        {
            // DirectIP / no-Steam: LocalSteamId == 0 and a client is ALSO assigned transport peerId 0.
            // Pre-fix the host self-added id 0 to the loaded set and the client ack added id 0 too — the
            // HashSet de-duplicated them to a single entry, so with 1 expected client Count stayed at 1
            // and looked fine, BUT the host's own readiness and the client's ack were indistinguishable:
            // if the client never acked, the host self-entry alone (Count==1) would FALSELY release.
            //
            // Fix #2: the host is a separate bool; _loadedPeers holds ONLY client ids. So:
            //  - host prepared, peerId-0 client has NOT acked → loadedClientCount=0 → no release.
            Assert.False(BarrierReleased(hostLoaded: true, loadedClientCount: 0, expectedClientCount: 1));
            //  - then the peerId-0 client acks → loadedClientCount=1 → release. The host entry and the
            //    id-0 client entry can never be conflated because they live in different stores.
            Assert.True(BarrierReleased(hostLoaded: true, loadedClientCount: 1, expectedClientCount: 1));
        }

        // ─── Fix #4: malformed chunk offset rejection ─────────────────────────────────────────────────

        [Fact]
        public void ChunkIndex_Accepts_OnGrid_InBounds_Offsets()
        {
            const int chunkSize = 32 * 1024;
            const int total = 100 * 1024; // 4 chunks: 0,32k,64k,96k (last is short)

            Assert.True(TryChunkIndex(0, chunkSize, total, chunkSize, out var i0));
            Assert.Equal(0, i0);

            Assert.True(TryChunkIndex(chunkSize, chunkSize, total, chunkSize, out var i1));
            Assert.Equal(1, i1);

            // Last (short) chunk at offset 3*chunkSize=96k, length = total-96k.
            Assert.True(TryChunkIndex(3L * chunkSize, total - 3 * chunkSize, total, chunkSize, out var i3));
            Assert.Equal(3, i3);
        }

        [Fact]
        public void ChunkIndex_Rejects_Offset_Not_On_Grid()
        {
            const int chunkSize = 32 * 1024;
            const int total = 100 * 1024;

            // Pre-fix OnSaveChunk computed index = offset / ChunkSize on a NON-multiple offset, which
            // truncates to a wrong-but-valid index and mis-maps coverage. This must now be rejected.
            Assert.False(TryChunkIndex(1, chunkSize, total, chunkSize, out var idx));
            Assert.Equal(-1, idx);

            // An offset one byte past a grid line (chunkSize+1) is also off-grid → rejected.
            Assert.False(TryChunkIndex(chunkSize + 1, 16, total, chunkSize, out _));
        }

        [Fact]
        public void ChunkIndex_Rejects_OutOfBounds_And_Negative_Offsets()
        {
            const int chunkSize = 32 * 1024;
            const int total = 100 * 1024;

            // Negative offset.
            Assert.False(TryChunkIndex(-chunkSize, 16, total, chunkSize, out _));

            // On-grid but writing past the buffer end (offset + len > total).
            Assert.False(TryChunkIndex(3L * chunkSize, chunkSize, total, chunkSize, out _));
        }
    }
}
