using Multiplayer.Sync.Tactical;
using Xunit;

/// <summary>
/// PURE decision tests for the two tactical inventory-view guards (gap-turret-crate-loot, audit D18).
/// The Harmony glue (InventoryViewGuardPatches: InventoryAbility.Activate host prefix +
/// TacticalView.ToInventoryViewState client prefix) is in-game verified; only the decisions are unit-tested.
/// </summary>
public class TacticalInventoryViewGuardTests
{
    // ── HOST hijack guard: a relayed client move's crate auto-open must not yank the host's screen ──

    [Fact]
    public void Host_UnselectedActor_IsSuppressed()
    {
        // The relayed-move auto-open: the acting soldier (the CLIENT's) is not the host's selected actor.
        Assert.True(TacticalInventoryViewGuard.ShouldSuppressHostAutoInventoryView(isHost: true, actorIsSelected: false));
    }

    [Fact]
    public void Host_SelectedActor_RunsNative()
    {
        // The host player's own crate walk / backpack click — its soldier is selected → native view opens.
        Assert.False(TacticalInventoryViewGuard.ShouldSuppressHostAutoInventoryView(isHost: true, actorIsSelected: true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void NonHost_NeverSuppressedByHostGuard(bool isHost, bool actorIsSelected)
    {
        // Single-player / client roles never take the HOST guard (the client has its own view-level guard).
        Assert.False(TacticalInventoryViewGuard.ShouldSuppressHostAutoInventoryView(isHost, actorIsSelected));
    }

    // ── CLIENT view guard: a frozen mirror never enters the inventory view (unsynced local item moves) ──

    [Fact]
    public void ClientMirror_InventoryView_IsSuppressed()
    {
        Assert.True(TacticalInventoryViewGuard.ShouldSuppressClientInventoryView(isClientMirroring: true));
    }

    [Fact]
    public void NonMirror_InventoryView_RunsNative()
    {
        // Host / single-player: the native inventory view is untouched.
        Assert.False(TacticalInventoryViewGuard.ShouldSuppressClientInventoryView(isClientMirroring: false));
    }
}
