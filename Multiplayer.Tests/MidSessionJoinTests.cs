using System;
using System.IO;
using Multiplayer.Network;
using Multiplayer.Network.MessageLayer;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>
    /// P1 mid-session on-demand joiner (audit fix #2): a peer that connects AFTER the co-op session
    /// started is onboarded via a PER-PEER unicast save transfer + re-seed, without disturbing the
    /// already-connected peers, and is REJECTED when a tactical battle is live (turn-0 deploy limit).
    ///
    /// These pin the pure, Unity-free decision surfaces that back the live wiring:
    ///   - SessionLifecycle.MidSessionJoinGuard    (host kicks a per-peer transfer)
    ///   - SessionLifecycle.ShouldRejectMidSessionJoin (battle-live boundary)
    ///   - MessageSerializer SaveDone onDemandJoin flag (the joiner's "enter now" signal on the wire)
    /// The engine glue (SaveTransferCoordinator.HostOnDemandJoin / SendBlobTo / OnJoinReady,
    /// SessionManager.HandleConnectionRequest) binds NetworkEngine + game save types and is verified
    /// in-game, mirroring the rest of the save-transfer barrier (SaveTransferBarrierTests).
    /// </summary>
    public class MidSessionJoinTests
    {
        // ─── Per-peer transfer TRIGGER: MidSessionJoinGuard truth table ────────────────────────────────

        [Fact]
        public void JoinGuard_Fires_For_Host_InGeoscape_MidSession_NoTransfer()
        {
            // The single onboarding-allowed state: host, session already started, on the geoscape, and no
            // full transfer already in flight → kick the per-peer on-demand transfer.
            Assert.True(SessionLifecycle.MidSessionJoinGuard(
                isHost: true, sessionStarted: true, geoscapeActive: true, transferActive: false));
        }

        [Fact]
        public void JoinGuard_Blocks_NonHost()
        {
            // Only the authority captures + ships current state; a client never onboards a joiner.
            Assert.False(SessionLifecycle.MidSessionJoinGuard(
                isHost: false, sessionStarted: true, geoscapeActive: true, transferActive: false));
        }

        [Fact]
        public void JoinGuard_Blocks_Before_Session_Start()
        {
            // EXISTING-CLIENT-UNTOUCHED / no-double-fire: pre-start (lobby) the normal lobby start path
            // transfers everyone at once. The on-demand per-peer path must NOT also fire, or a lobby peer
            // would get two transfers.
            Assert.False(SessionLifecycle.MidSessionJoinGuard(
                isHost: true, sessionStarted: false, geoscapeActive: true, transferActive: false));
        }

        [Fact]
        public void JoinGuard_Blocks_When_Not_On_Geoscape()
        {
            // Host in a tactical battle / mid-load: no geoscape blob to reproduce → reject, do not transfer.
            Assert.False(SessionLifecycle.MidSessionJoinGuard(
                isHost: true, sessionStarted: true, geoscapeActive: false, transferActive: false));
        }

        [Fact]
        public void JoinGuard_Blocks_While_A_Full_Transfer_Is_In_Flight()
        {
            // EXISTING-CLIENT-UNTOUCHED: a global F2 re-transfer is already reseeding EVERY peer. The
            // per-peer join must not overlap it (that transfer will onboard the new peer too, and firing
            // both would race two SaveDone streams at the joiner). Wait until it settles.
            Assert.False(SessionLifecycle.MidSessionJoinGuard(
                isHost: true, sessionStarted: true, geoscapeActive: true, transferActive: true));
        }

        // ─── Battle-live REJECTION boundary: ShouldRejectMidSessionJoin ─────────────────────────────────

        [Fact]
        public void Reject_When_MidSession_And_Not_On_Geoscape()
        {
            // Session started but the host is NOT on the geoscape → a battle is live (or mid-load); the
            // turn-0 deploy snapshot cannot reproduce an in-progress mission, so the join is bounced.
            Assert.True(SessionLifecycle.ShouldRejectMidSessionJoin(sessionStarted: true, geoscapeActive: false));
        }

        [Fact]
        public void Reject_Is_False_When_Host_On_Geoscape()
        {
            // Geoscape mid-session join is SUPPORTED → do not reject (the on-demand transfer onboards it).
            Assert.False(SessionLifecycle.ShouldRejectMidSessionJoin(sessionStarted: true, geoscapeActive: true));
        }

        [Fact]
        public void Reject_Is_False_Before_Session_Start()
        {
            // Pre-start (lobby) join is owned by the lobby path regardless of geoscape/tactical — never
            // rejected here (a client can sit in the lobby while the host is still picking a save).
            Assert.False(SessionLifecycle.ShouldRejectMidSessionJoin(sessionStarted: false, geoscapeActive: false));
            Assert.False(SessionLifecycle.ShouldRejectMidSessionJoin(sessionStarted: false, geoscapeActive: true));
        }

        [Fact]
        public void Reject_And_JoinGuard_Partition_The_MidSession_Geoscape_Axis()
        {
            // For a mid-session join, exactly ONE of {reject, onboard} applies, split on geoscape liveness:
            //   on geoscape  → onboard (guard true, reject false)
            //   off geoscape → reject  (guard false, reject true)
            foreach (var geo in new[] { true, false })
            {
                bool onboard = SessionLifecycle.MidSessionJoinGuard(
                    isHost: true, sessionStarted: true, geoscapeActive: geo, transferActive: false);
                bool reject = SessionLifecycle.ShouldRejectMidSessionJoin(sessionStarted: true, geoscapeActive: geo);
                Assert.NotEqual(onboard, reject);
            }
        }

        // ─── Joiner "enter now" signal on the wire: SaveDone onDemandJoin flag ──────────────────────────

        [Fact]
        public void SaveDone_RoundTrips_The_OnDemandJoin_Flag()
        {
            var id = Guid.NewGuid();
            const long total = 123_456;
            const string ext = ".zsav";
            const uint crc = 0xDEADBEEF;

            // A join transfer tags SaveDone true → the joiner enters immediately + reveals natively.
            var joinBytes = MessageSerializer.SerializeSaveDone(id, total, ext, crc, onDemandJoin: true);
            var join = MessageSerializer.DeserializeSaveDone(joinBytes);
            Assert.Equal(id, join.transferId);
            Assert.Equal(total, join.totalBytes);
            Assert.Equal(ext, join.fileExtension);
            Assert.Equal(crc, join.crc32);
            Assert.True(join.onDemandJoin);

            // The lobby / F2 start path leaves it false → the receiver waits for the BEGIN barrier.
            var lobby = MessageSerializer.DeserializeSaveDone(
                MessageSerializer.SerializeSaveDone(id, total, ext, crc, onDemandJoin: false));
            Assert.False(lobby.onDemandJoin);

            // Default overload (existing SendBlob call site) is the lobby path → false.
            var dflt = MessageSerializer.DeserializeSaveDone(
                MessageSerializer.SerializeSaveDone(id, total, ext, crc));
            Assert.False(dflt.onDemandJoin);
        }

        [Fact]
        public void SaveDone_Legacy_FourField_Payload_Parses_As_Not_OnDemandJoin()
        {
            // Wire-versioning: a pre-flag SaveDone (guid+long+string+uint, no trailing bool) must still
            // deserialize — the flag read is skipped when no byte remains → onDemandJoin=false.
            var id = Guid.NewGuid();
            byte[] legacy;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(id.ToByteArray());
                bw.Write(777L);
                bw.Write(".zsav");
                bw.Write((uint)0x12345678);
                legacy = ms.ToArray();
            }

            var parsed = MessageSerializer.DeserializeSaveDone(legacy);
            Assert.Equal(id, parsed.transferId);
            Assert.Equal(777L, parsed.totalBytes);
            Assert.Equal(".zsav", parsed.fileExtension);
            Assert.Equal((uint)0x12345678, parsed.crc32);
            Assert.False(parsed.onDemandJoin);
        }
    }
}
