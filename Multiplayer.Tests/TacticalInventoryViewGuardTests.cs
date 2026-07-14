using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE decision tests for the HOST inventory-view auto-open guard (gap-turret-crate-loot, audit D18).
/// The Harmony glue (InventoryViewGuardPatches: InventoryAbility.Activate host prefix) is in-game verified;
/// only the decision is unit-tested. The client view is no longer suppressed — mid-mission looting ships as the
/// inventory-transfer intent (TacticalInventoryTransferCodecTests covers its wire).
/// </summary>
public class TacticalInventoryViewGuardTests
{
    // ── HOST hijack guard: a relayed client move's crate auto-open must not yank the host's screen ──

    [Fact]
    public void Host_RelayedMove_IsSuppressed()
    {
        // The relayed-move auto-open: the acting soldier's most recent move was a relayed CLIENT intent.
        Assert.True(TacticalInventoryViewGuard.ShouldSuppressHostAutoInventoryView(isHost: true, lastMoveWasRelayed: true));
    }

    [Fact]
    public void Host_OwnMove_RunsNative()
    {
        // The host player's own crate walk — a host-local move origin → native view opens (must NOT be suppressed).
        Assert.False(TacticalInventoryViewGuard.ShouldSuppressHostAutoInventoryView(isHost: true, lastMoveWasRelayed: false));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void NonHost_NeverSuppressedByHostGuard(bool isHost, bool lastMoveWasRelayed)
    {
        // Single-player / client roles never take the HOST guard.
        Assert.False(TacticalInventoryViewGuard.ShouldSuppressHostAutoInventoryView(isHost, lastMoveWasRelayed));
    }
}
