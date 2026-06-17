using Multipleer.Network;
using Xunit;

namespace Multipleer.Tests
{
    // Mirrors exactly how MultiplayerUI.OnLobbyPlay / HostStartSession will consult the controller at
    // press time: build the live facts, UpdateLobby, then CommitStart. Proves the press-time guard
    // rejects the host-alone and not-ready races (Bug B H1/H2).
    public class LobbyControllerStartGateTests
    {
        private static LobbyController HostLobbyWith(int clients, bool ready, bool save)
        {
            var c = new LobbyController();
            c.BeginHost();
            c.UpdateLobby(clients, ready, save);
            return c;
        }

        [Fact]
        public void PressStart_HostAlone_Rejected()
        {
            var c = HostLobbyWith(clients: 0, ready: true, save: true);
            Assert.False(c.CommitStart());
            Assert.Equal(LobbyState.HostLobby, c.State);
        }

        [Fact]
        public void PressStart_ClientNotReady_Rejected()
        {
            var c = HostLobbyWith(clients: 1, ready: false, save: true);
            Assert.False(c.CommitStart());
        }

        [Fact]
        public void PressStart_NoSave_Rejected()
        {
            var c = HostLobbyWith(clients: 1, ready: true, save: false);
            Assert.False(c.CommitStart());
        }

        [Fact]
        public void PressStart_AllConditionsMet_CommitsAndLocks()
        {
            var c = HostLobbyWith(clients: 1, ready: true, save: true);
            Assert.True(c.CommitStart());
            Assert.True(c.IsLocked);
            Assert.Equal(LobbyState.Starting, c.State);
        }

        [Fact]
        public void PressStart_StaleFrame_ClientLeftAfterButtonLit_Rejected()
        {
            // Button lit one frame ago (1 ready client), then the client left BEFORE the press.
            var c = HostLobbyWith(clients: 1, ready: true, save: true);
            Assert.True(c.CanStart);                 // button was lit
            c.UpdateLobby(0, true, true);            // client left this frame
            Assert.False(c.CommitStart());           // press-time re-validation rejects it (H2)
        }
    }
}
