using System.Collections.Generic;
using Multipleer.Network;
using Xunit;

// Fix #2 coverage: client ready is now a real TOGGLE. Ready adds the client (roster Ready=true →
// AllClientsReady true → host PLAY arms when a save is chosen); un-ready removes it (roster
// Ready=false → AllClientsReady false → PLAY de-arms). The host start gate reads the authoritative
// roster Ready flags through LobbyController (RefreshGateFacts feeds UpdateLobby with the
// LobbyController.AllClientsReady projection of those flags), so the toggle's gate effect is what we
// assert here.
//
// NOTE: the host-side SetClientReady(steamId,false) wire-through (HashSet remove + IsReady=false +
// re-broadcast) is bound to NetworkEngine.Transport and Unity subsystems (SaveTransferCoordinator/
// TimeSync/Sync are all constructed by Initialize), which are not unit-isolatable in this headless
// xUnit process — so the toggle is validated at the gate-recompute layer (the pure FSM + roster rule)
// that the fix actually unblocks, mirroring AllReadyGateTests / LobbyControllerStartGateTests.
public class ClientUnreadyToggleTests
{
    // Roster transition: one non-host client readies, then un-readies.
    private static List<bool> Ready() => new List<bool> { true };
    private static List<bool> Unready() => new List<bool> { false };

    [Fact]
    public void Ready_MakesRosterAllReady()
    {
        Assert.True(LobbyController.AllClientsReady(Ready()));
    }

    [Fact]
    public void Unready_MakesRosterNotAllReady()
    {
        // The removal that SetClientReady(steamId,false) performs flips the broadcast roster flag to
        // false; the gate must then read NOT-all-ready.
        Assert.False(LobbyController.AllClientsReady(Unready()));
    }

    [Fact]
    public void Toggle_ArmsThenDeArmsHostStartGate()
    {
        var fsm = new LobbyController();
        Assert.True(fsm.BeginHost());

        // Save chosen + the one client readied → gate ARMS (PLAY lights).
        fsm.UpdateLobby(
            connectedClientCount: 1,
            allConnectedClientsReady: LobbyController.AllClientsReady(Ready()),
            saveChosen: true);
        Assert.True(fsm.CanStart);

        // Client un-readies → roster flag false → gate must DE-ARM (PLAY goes dark), even though the
        // save is still chosen and the client is still connected. Pre-fix the client could never
        // un-ready (latched in the host's HashSet), so this transition was unreachable.
        fsm.UpdateLobby(
            connectedClientCount: 1,
            allConnectedClientsReady: LobbyController.AllClientsReady(Unready()),
            saveChosen: true);
        Assert.False(fsm.CanStart);
    }

    [Fact]
    public void Toggle_CanReArmAfterReReady()
    {
        var fsm = new LobbyController();
        fsm.BeginHost();

        fsm.UpdateLobby(1, LobbyController.AllClientsReady(Ready()), true);
        Assert.True(fsm.CanStart);
        fsm.UpdateLobby(1, LobbyController.AllClientsReady(Unready()), true);
        Assert.False(fsm.CanStart);
        // Re-ready re-arms — the toggle is fully reversible, not a one-shot latch.
        fsm.UpdateLobby(1, LobbyController.AllClientsReady(Ready()), true);
        Assert.True(fsm.CanStart);
    }
}
