using Multiplayer.Sync.Tactical;
using Xunit;

// Pure-logic tests for the CLIENT mirror's TurnIsPlaying marker gate. The native tactical view dispatcher
// (UIStateInitial.InitialStateUpdateCrt:34-95) gates ALL turn presentation behind
// IsWaitingForActiveAndQueuedAbilitiesAndMapUpdate() (UIStateInitial.cs:49 → TacticalView.cs:972-979), which
// returns true (→ UIStateWaiting: no HUD/control, camera stuck) whenever !LevelController.TurnIsPlaying
// (TacticalView.cs:965). TacticalLevelController.TurnIsPlaying => _nextTurnUpdateable != null (TLC.cs:251).
// Native sets _nextTurnUpdateable ONCE per mission (TLC.cs:680) and it drives EVERY faction turn (cleared only
// at teardown TLC.cs:769) — so it is mission-scoped, NOT per-faction. On the client NextTurnCrt is suppressed,
// so the marker must be re-asserted by the mirror while a turn is active. This gate pins WHEN.
public class TurnPlayingMirrorGateTests
{
    // ── ShouldMarkTurnPlaying ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Marks_WhenClientMirroringAndTurnActive()
    {
        // Client mirror inside any active faction turn → must mark TurnIsPlaying true so the view leaves
        // UIStateWaiting and reaches control (player) / enemy presentation.
        Assert.True(TurnPlayingMirrorGate.ShouldMarkTurnPlaying(isClientMirroring: true, turnActive: true));
    }

    [Fact]
    public void DoesNotMark_WhenNotMirroring()
    {
        // Host / single-player drives _nextTurnUpdateable natively (TLC.cs:680) — the mirror must never touch it.
        Assert.False(TurnPlayingMirrorGate.ShouldMarkTurnPlaying(isClientMirroring: false, turnActive: true));
    }

    [Fact]
    public void DoesNotMark_WhenNoTurnActive()
    {
        // No turn in progress (e.g. between missions) → nothing to mark.
        Assert.False(TurnPlayingMirrorGate.ShouldMarkTurnPlaying(isClientMirroring: true, turnActive: false));
    }

    [Fact]
    public void DoesNotMark_WhenNeither()
    {
        Assert.False(TurnPlayingMirrorGate.ShouldMarkTurnPlaying(isClientMirroring: false, turnActive: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Marking_IsFactionAgnostic(bool ownPlayerTurn)
    {
        // FACTION-AGNOSTIC: the UIStateInitial.cs:49 gate runs BEFORE both the player branch (:66) and the enemy
        // branch (:58); player-vs-enemy routing is decided at :58 vs :66, NOT by _nextTurnUpdateable. So the
        // marker must be set for BOTH a player turn and an enemy turn — the gate takes only mirroring + turnActive
        // (whether THIS turn is the client's own player turn is irrelevant to the marker decision).
        Assert.Equal(
            ownPlayerTurn,                                                  // arbitrary value, unused by the gate
            TurnPlayingMirrorGate.ShouldMarkTurnPlaying(isClientMirroring: true, turnActive: true) && ownPlayerTurn);
        // Direct invariant: regardless of faction kind, a mirroring client with an active turn marks playing.
        Assert.True(TurnPlayingMirrorGate.ShouldMarkTurnPlaying(isClientMirroring: true, turnActive: true));
    }

    // ── ShouldClearTurnPlaying ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Clears_OnlyAtMissionEnd_WhenMirroring()
    {
        // Native nulls _nextTurnUpdateable only at teardown (TLC.cs:769) — the mirror clears it only when the
        // mission is ending.
        Assert.True(TurnPlayingMirrorGate.ShouldClearTurnPlaying(isClientMirroring: true, missionEnding: true));
    }

    [Fact]
    public void DoesNotClear_MidMissionHandoff()
    {
        // Mid-mission (faction handoff, NOT ending) → never clear. Clearing here would land BOTH the player and
        // enemy view branches in UIStateWaiting (the :49 gate is faction-agnostic) and break enemy-turn
        // presentation. This is the explicit anti-regression contract.
        Assert.False(TurnPlayingMirrorGate.ShouldClearTurnPlaying(isClientMirroring: true, missionEnding: false));
    }

    [Fact]
    public void DoesNotClear_WhenNotMirroring()
    {
        // Host / single-player owns the field natively — never cleared by the mirror.
        Assert.False(TurnPlayingMirrorGate.ShouldClearTurnPlaying(isClientMirroring: false, missionEnding: true));
    }
}
