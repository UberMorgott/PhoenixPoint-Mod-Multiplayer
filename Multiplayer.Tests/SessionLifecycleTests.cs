using Multiplayer.Network;
using Xunit;

namespace Multiplayer.Tests
{
    // Pure-logic coverage for the three session-lifecycle features (F1/F2/F3). The live wiring
    // (SessionNotifier, HostLeaveHandler, the in-game load intercept) binds Unity/game types and is
    // in-game verified; these tests pin the EXTRACTED pure decisions: the F1 notice formatting, the
    // F2 host-load guard predicate, and the F3 one-shot idempotency latch.
    public class SessionLifecycleTests
    {
        // ─── F1: peer-event message formatting ──────────────────────────────
        [Fact]
        public void FormatPeerEvent_Connected_WithName()
        {
            Assert.Equal("— Alice joined —", SessionLifecycle.FormatPeerEvent(connected: true, "Alice"));
        }

        [Fact]
        public void FormatPeerEvent_Disconnected_WithName()
        {
            Assert.Equal("— Bob left —", SessionLifecycle.FormatPeerEvent(connected: false, "Bob"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void FormatPeerEvent_NullOrEmptyName_FallsBackToPlaceholder(string name)
        {
            // A raw id must never surface — an unknown name resolves to the generic placeholder.
            Assert.Equal("— a player left —", SessionLifecycle.FormatPeerEvent(connected: false, name));
            Assert.Equal("— a player joined —", SessionLifecycle.FormatPeerEvent(connected: true, name));
        }

        // ─── F2: host mid-session load guard ────────────────────────────────
        [Fact]
        public void HostLoadGuard_AllConditionsMet_Open()
        {
            Assert.True(SessionLifecycle.HostLoadGuard(
                isHost: true, isActiveSession: true, sessionStarted: true,
                connectedClientCount: 1, transferActive: false));
        }

        [Fact]
        public void HostLoadGuard_NotHost_Closed()
        {
            Assert.False(SessionLifecycle.HostLoadGuard(
                isHost: false, isActiveSession: true, sessionStarted: true,
                connectedClientCount: 1, transferActive: false));
        }

        [Fact]
        public void HostLoadGuard_NoSessionActive_Closed()
        {
            Assert.False(SessionLifecycle.HostLoadGuard(
                isHost: true, isActiveSession: false, sessionStarted: true,
                connectedClientCount: 1, transferActive: false));
        }

        [Fact]
        public void HostLoadGuard_NotStartedYet_Closed()
        {
            // Lobby (not yet in-game): F2 is an IN-GAME load only; the lobby Play path owns the first start.
            Assert.False(SessionLifecycle.HostLoadGuard(
                isHost: true, isActiveSession: true, sessionStarted: false,
                connectedClientCount: 1, transferActive: false));
        }

        [Fact]
        public void HostLoadGuard_NoClients_Closed()
        {
            // Host alone → no one to pull into the save.
            Assert.False(SessionLifecycle.HostLoadGuard(
                isHost: true, isActiveSession: true, sessionStarted: true,
                connectedClientCount: 0, transferActive: false));
        }

        [Fact]
        public void HostLoadGuard_TransferAlreadyInFlight_Closed()
        {
            // Re-entry guard: a transfer is already running (locked) → reject a second concurrent load.
            Assert.False(SessionLifecycle.HostLoadGuard(
                isHost: true, isActiveSession: true, sessionStarted: true,
                connectedClientCount: 1, transferActive: true));
        }

        // ─── F3: host-leave idempotency latch ───────────────────────────────
        [Fact]
        public void HostLeaveLatch_FirstCall_Handles()
        {
            var latch = new HostLeaveLatch();
            Assert.False(latch.Handled);
            Assert.True(latch.TryHandle());
            Assert.True(latch.Handled);
        }

        [Fact]
        public void HostLeaveLatch_SecondCall_DoesNotDoubleFire()
        {
            // Graceful HostDisconnected packet THEN the transport drop of the same host → menu once.
            var latch = new HostLeaveLatch();
            Assert.True(latch.TryHandle());   // graceful packet
            Assert.False(latch.TryHandle());  // subsequent transport drop → suppressed
            Assert.False(latch.TryHandle());  // and again (heartbeat timeout) → still suppressed
        }

        [Fact]
        public void HostLeaveLatch_Reset_ReArmsForNextSession()
        {
            var latch = new HostLeaveLatch();
            Assert.True(latch.TryHandle());
            Assert.False(latch.TryHandle());

            latch.Reset(); // new host/join session
            Assert.False(latch.Handled);
            Assert.True(latch.TryHandle()); // fires once again for the fresh session
        }
    }
}
