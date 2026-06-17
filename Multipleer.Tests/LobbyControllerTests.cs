using Multipleer.Network;
using Xunit;

namespace Multipleer.Tests
{
    public class LobbyControllerTests
    {
        // ─── Host create → host lobby ───────────────────────────────────
        [Fact]
        public void NewController_StartsIdle()
        {
            var c = new LobbyController();
            Assert.Equal(LobbyState.Idle, c.State);
        }

        [Fact]
        public void Host_FromIdle_EntersHostLobby()
        {
            var c = new LobbyController();
            Assert.True(c.BeginHost());
            Assert.Equal(LobbyState.HostLobby, c.State);
        }

        [Fact]
        public void Join_FromIdle_EntersJoining_ThenClientLobby()
        {
            var c = new LobbyController();
            Assert.True(c.BeginJoin());
            Assert.Equal(LobbyState.Joining, c.State);
            Assert.True(c.JoinConfirmed());
            Assert.Equal(LobbyState.ClientLobby, c.State);
        }

        // ─── Illegal transitions rejected ───────────────────────────────
        [Fact]
        public void Host_WhenNotIdle_IsRejected()
        {
            var c = new LobbyController();
            c.BeginHost();
            Assert.False(c.BeginHost()); // already HostLobby
            Assert.Equal(LobbyState.HostLobby, c.State);
        }

        [Fact]
        public void JoinConfirmed_WhenNotJoining_IsRejected()
        {
            var c = new LobbyController();
            c.BeginHost();
            Assert.False(c.JoinConfirmed());
            Assert.Equal(LobbyState.HostLobby, c.State);
        }

        [Fact]
        public void CommitStart_WhenNotHostLobby_IsRejected()
        {
            var c = new LobbyController();
            Assert.False(c.CommitStart()); // Idle
        }

        // ─── Start gate (kills Bug B) ───────────────────────────────────
        [Fact]
        public void StartGate_TrueOnly_WhenClientReadyAndSaveChosen()
        {
            var c = new LobbyController();
            c.BeginHost();
            c.UpdateLobby(connectedClientCount: 1, allConnectedClientsReady: true, saveChosen: true);
            Assert.True(c.CanStart);
        }

        [Fact]
        public void StartGate_False_WhenNoClient()
        {
            var c = new LobbyController();
            c.BeginHost();
            c.UpdateLobby(connectedClientCount: 0, allConnectedClientsReady: true, saveChosen: true);
            Assert.False(c.CanStart); // host-alone never starts
        }

        [Fact]
        public void StartGate_False_WhenClientNotReady()
        {
            var c = new LobbyController();
            c.BeginHost();
            c.UpdateLobby(connectedClientCount: 1, allConnectedClientsReady: false, saveChosen: true);
            Assert.False(c.CanStart);
        }

        [Fact]
        public void StartGate_False_WhenNoSave()
        {
            var c = new LobbyController();
            c.BeginHost();
            c.UpdateLobby(connectedClientCount: 1, allConnectedClientsReady: true, saveChosen: false);
            Assert.False(c.CanStart);
        }

        [Fact]
        public void StartGate_False_WhenNotHostLobby()
        {
            var c = new LobbyController();
            // never BeginHost: stays Idle
            c.UpdateLobby(connectedClientCount: 1, allConnectedClientsReady: true, saveChosen: true);
            Assert.False(c.CanStart);
        }

        // ─── Lock on start commit ───────────────────────────────────────
        [Fact]
        public void CommitStart_WhenGateOpen_LocksAndEntersStarting()
        {
            var c = new LobbyController();
            c.BeginHost();
            c.UpdateLobby(connectedClientCount: 1, allConnectedClientsReady: true, saveChosen: true);
            Assert.True(c.CommitStart());
            Assert.Equal(LobbyState.Starting, c.State);
            Assert.True(c.IsLocked);
            Assert.False(c.CanStart); // gate closes once locked / no longer HostLobby
        }

        [Fact]
        public void CommitStart_WhenGateClosed_IsRejected()
        {
            var c = new LobbyController();
            c.BeginHost();
            c.UpdateLobby(connectedClientCount: 0, allConnectedClientsReady: true, saveChosen: true);
            Assert.False(c.CommitStart());
            Assert.Equal(LobbyState.HostLobby, c.State);
            Assert.False(c.IsLocked);
        }

        [Fact]
        public void Locked_RejectsFurtherUpdates()
        {
            var c = new LobbyController();
            c.BeginHost();
            c.UpdateLobby(connectedClientCount: 1, allConnectedClientsReady: true, saveChosen: true);
            c.CommitStart();
            // A late ready/un-ready flip must not reopen the gate after the lock.
            c.UpdateLobby(connectedClientCount: 2, allConnectedClientsReady: false, saveChosen: true);
            Assert.True(c.IsLocked);
            Assert.False(c.CanStart);
        }

        // ─── Ready resets on save change ────────────────────────────────
        [Fact]
        public void SaveChanged_ResetsReadyFlag()
        {
            var c = new LobbyController();
            c.BeginHost();
            c.UpdateLobby(connectedClientCount: 1, allConnectedClientsReady: true, saveChosen: true);
            Assert.True(c.CanStart);
            Assert.True(c.SaveChangedShouldResetReady()); // host swapped the save
            // After a save swap the controller must report clients no longer ready until re-readied.
            c.UpdateLobby(connectedClientCount: 1, allConnectedClientsReady: false, saveChosen: true);
            Assert.False(c.CanStart);
        }

        // ─── Teardown returns to Idle (reopenable) ──────────────────────
        [Fact]
        public void Reset_FromAnyState_ReturnsToIdle_Unlocked()
        {
            var c = new LobbyController();
            c.BeginHost();
            c.UpdateLobby(connectedClientCount: 1, allConnectedClientsReady: true, saveChosen: true);
            c.CommitStart();
            c.Reset();
            Assert.Equal(LobbyState.Idle, c.State);
            Assert.False(c.IsLocked);
            Assert.False(c.CanStart);
        }
    }
}
