using Multiplayer.Network;
using Xunit;

namespace Multiplayer.Tests
{
    // Pure-logic coverage for the CLIENT half-open Steam-P2P recovery decision. The live wiring
    // (SessionManager's heartbeat-ack detector → NetworkEngine.RepairHostLink → SteamTransport.ResetPeer)
    // binds Unity/Steam and is 2-PC in-game verified; this pins the EXTRACTED classifier: only the
    // half-open signature (inbound alive + outbound/ack dead) repairs, exactly once, then fails.
    public class HalfOpenRepairTests
    {
        private const long T = 20000; // HeartbeatTimeoutMs

        [Fact]
        public void HealthyLink_BothClocksFresh_None()
        {
            // now=25000; inbound and ack both refreshed at 20000 → 5s ago, well inside the window.
            Assert.Equal(HalfOpenAction.None,
                HalfOpenRepair.Decide(now: 25000, lastInboundMs: 20000, lastAckMs: 20000, T, repairAttempted: false));
        }

        [Fact]
        public void FullDrop_InboundAlsoStale_None()
        {
            // Nothing from the host at all (inbound stale too) is NOT half-open — that is the host-leave
            // path, which the detector handles separately. Never repair here.
            Assert.Equal(HalfOpenAction.None,
                HalfOpenRepair.Decide(now: 50000, lastInboundMs: 20000, lastAckMs: 20000, T, repairAttempted: false));
        }

        [Fact]
        public void HalfOpen_NotYetRepaired_Repair()
        {
            // Host heartbeats still arriving (inbound fresh at 45000) but no ack since 20000 (>20s) →
            // client→host is dead → one-shot repair.
            Assert.Equal(HalfOpenAction.Repair,
                HalfOpenRepair.Decide(now: 45000, lastInboundMs: 45000, lastAckMs: 20000, T, repairAttempted: false));
        }

        [Fact]
        public void HalfOpen_AlreadyRepaired_Fail()
        {
            // Same signature, but the one-shot repair already ran this connection → declare the link dead.
            Assert.Equal(HalfOpenAction.Fail,
                HalfOpenRepair.Decide(now: 45000, lastInboundMs: 45000, lastAckMs: 20000, T, repairAttempted: true));
        }

        [Fact]
        public void AckExactlyAtTimeout_NotYetDead_None()
        {
            // Boundary: ack age == timeout is NOT yet "> timeout", so still alive → no action.
            Assert.Equal(HalfOpenAction.None,
                HalfOpenRepair.Decide(now: 40000, lastInboundMs: 40000, lastAckMs: 20000, T, repairAttempted: false));
        }
    }
}
