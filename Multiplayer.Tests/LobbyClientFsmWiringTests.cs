using Multiplayer.Network;
using Xunit;

namespace Multiplayer.Tests
{
    // Fixes #4 & #5 encode the SEQUENCE the MultiplayerUI wiring must produce on the LobbyController.
    // The wiring itself lives in a Unity MonoBehaviour (MultiplayerUI) and is not unit-isolatable
    // headless, so these tests lock in the pure-FSM contract the wiring relies on:
    //   #4  OnLobbyJoin does Reset()+BeginJoin() (Idle→Joining); the first host PEER_LIST drives
    //       JoinConfirmed() (Joining→ClientLobby) — so a joined client is modelled, not stuck in
    //       HostLobby with Joining/ClientLobby dead.
    //   #5  every teardown path (LEAVE / connect-cancel / OnConnectionFailed) routes through a single
    //       hook that Reset()s the FSM, so no stale HostLobby/Starting state survives into the next
    //       host or join.
    public class LobbyClientFsmWiringTests
    {
        [Fact]
        public void OnLobbyJoin_FromAutoHostLobby_ResetsThenBeginsJoin()
        {
            // The menu auto-hosts → FSM is in HostLobby when the user pastes a join target. The fix
            // must Reset() back to Idle BEFORE BeginJoin() (BeginJoin only fires from Idle), otherwise
            // the client stays latched in HostLobby (the production bug: Joining was unreachable).
            var c = new LobbyController();
            c.BeginHost();
            Assert.Equal(LobbyState.HostLobby, c.State);

            c.Reset();                 // OnLobbyJoin step 1
            Assert.True(c.BeginJoin()); // OnLobbyJoin step 2
            Assert.Equal(LobbyState.Joining, c.State);
        }

        [Fact]
        public void FirstPeerList_ConfirmsJoin_IntoClientLobby()
        {
            var c = new LobbyController();
            c.Reset();
            c.BeginJoin();
            // Update()'s client-join confirmation gate (first PEER_LIST populated) calls JoinConfirmed.
            Assert.True(c.JoinConfirmed());
            Assert.Equal(LobbyState.ClientLobby, c.State);
        }

        [Fact]
        public void JoinConfirmed_IsIdempotentAcrossExtraFrames()
        {
            // Update() can re-enter the gate on a later frame; JoinConfirmed must no-op once past Joining
            // so a repeated call cannot corrupt the ClientLobby state.
            var c = new LobbyController();
            c.Reset();
            c.BeginJoin();
            Assert.True(c.JoinConfirmed());
            Assert.False(c.JoinConfirmed());            // second frame: already ClientLobby
            Assert.Equal(LobbyState.ClientLobby, c.State);
        }

        [Fact]
        public void Teardown_FromClientLobby_ReturnsToIdle_Reopenable()
        {
            // Fix #5: LEAVE / connect-cancel / connection-failure all Reset() the FSM. After teardown a
            // fresh BeginHost (re-host) must succeed — proving no stale state was carried across.
            var c = new LobbyController();
            c.Reset();
            c.BeginJoin();
            c.JoinConfirmed();

            c.Reset();                  // the single TeardownLobbyState hook
            Assert.Equal(LobbyState.Idle, c.State);
            Assert.True(c.BeginHost()); // re-host from a clean slate
            Assert.Equal(LobbyState.HostLobby, c.State);
        }

        [Fact]
        public void Teardown_FromStarting_ReturnsToIdle()
        {
            // OnConnectionFailed can fire while a host lobby is mid-start (Starting+locked). The teardown
            // Reset must clear the lock and state, not leave the FSM dead-locked in Starting.
            var c = new LobbyController();
            c.BeginHost();
            c.UpdateLobby(connectedClientCount: 1, allConnectedClientsReady: true, saveChosen: true);
            c.CommitStart();
            Assert.Equal(LobbyState.Starting, c.State);

            c.Reset();
            Assert.Equal(LobbyState.Idle, c.State);
            Assert.False(c.IsLocked);
        }
    }
}
