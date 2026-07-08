using Multiplayer.Network.Sync;
using Xunit;

// Fix #1A (personnel-screen fix wave 2026-07-08): the client SetItems equip relay content-dedup that kills
// the EditSoldier per-frame re-flush storm. Pure signature + per-unit last-relayed suppression contract.
public class LoadoutRelayDedupTests
{
    // ─── signature ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Signature_IsDeterministic_ForSameLoadout()
    {
        var a = LoadoutRelayDedup.Signature(new[] { "arm" }, new[] { "rifle", "grenade" }, new[] { "medkit" });
        var b = LoadoutRelayDedup.Signature(new[] { "arm" }, new[] { "rifle", "grenade" }, new[] { "medkit" });
        Assert.Equal(a, b);
    }

    [Fact]
    public void Signature_IsOrderSensitive_WithinASlot()
    {
        // A slot reorder IS a real edit — the signatures must differ so it relays.
        var a = LoadoutRelayDedup.Signature(null, new[] { "rifle", "pistol" }, null);
        var b = LoadoutRelayDedup.Signature(null, new[] { "pistol", "rifle" }, null);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Signature_DistinguishesSlotBoundaries()
    {
        // Same guids, different slots (armour vs equipment) → different loadout → different signature.
        var a = LoadoutRelayDedup.Signature(new[] { "x" }, new string[0], new string[0]);
        var b = LoadoutRelayDedup.Signature(new string[0], new[] { "x" }, new string[0]);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Signature_NullAndEmptySlot_AreEquivalent()
    {
        // A null arg the caller pre-fills from an empty current list must hash the same as an empty array.
        Assert.Equal(LoadoutRelayDedup.Signature(null, null, null),
                     LoadoutRelayDedup.Signature(new string[0], new string[0], new string[0]));
    }

    [Fact]
    public void Signature_SameDefInstanceSwapWithinSlot_EqualByDesign()
    {
        // GRANULARITY INVARIANT pin (review 2026-07-08): swapping two SAME-DEF item instances yields
        // [x,x] → [x,x] — an equal signature, so the re-flush is suppressed. This is CORRECT, not a
        // false-suppress: the EquipSoldierAction wire is def-level (guid lists only), so the re-relayed
        // intent would be byte-identical and the host apply (fresh new GeoItem(def), freeReload) produces
        // the identical result. Per-instance state (ammo/charges) never rides this intent; it converges
        // via the authoritative #9 blob.
        var before = LoadoutRelayDedup.Signature(null, new[] { "x", "x" }, null);
        var after = LoadoutRelayDedup.Signature(null, new[] { "x", "x" }, null);   // instances swapped — same defs, same order of guids
        Assert.Equal(before, after);
    }

    [Fact]
    public void Signature_SameDefMovedBetweenSlots_Differs()
    {
        // The flip side of the invariant: a same-def item MOVING between slots (equipment → inventory)
        // changes the def-level loadout, so it must relay — the slot boundary keeps the signatures apart.
        var before = LoadoutRelayDedup.Signature(null, new[] { "x", "x" }, new string[0]);
        var after = LoadoutRelayDedup.Signature(null, new[] { "x" }, new[] { "x" });
        Assert.NotEqual(before, after);
    }

    // ─── per-unit dedup ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ShouldRelay_FirstSightOfUnit_Relays()
    {
        var d = new LoadoutRelayDedup();
        Assert.True(d.ShouldRelay(1, "sigA"));
    }

    [Fact]
    public void ShouldRelay_IdenticalReflush_Suppressed()
    {
        var d = new LoadoutRelayDedup();
        Assert.True(d.ShouldRelay(1, "sigA"));    // genuine edit
        Assert.False(d.ShouldRelay(1, "sigA"));   // per-frame re-flush → suppressed (the storm)
        Assert.False(d.ShouldRelay(1, "sigA"));
    }

    [Fact]
    public void ShouldRelay_ChangedLoadout_RelaysImmediately()
    {
        var d = new LoadoutRelayDedup();
        Assert.True(d.ShouldRelay(1, "sigA"));
        Assert.False(d.ShouldRelay(1, "sigA"));
        Assert.True(d.ShouldRelay(1, "sigB"));    // genuine change → relays at once
        Assert.False(d.ShouldRelay(1, "sigB"));   // then dedups the new one
    }

    [Fact]
    public void ShouldRelay_IsPerUnit_NoCrossMasking()
    {
        var d = new LoadoutRelayDedup();
        Assert.True(d.ShouldRelay(1, "sig"));     // unit 1 first sight
        Assert.True(d.ShouldRelay(2, "sig"));     // unit 2 first sight — same signature, DIFFERENT unit → relays
        Assert.False(d.ShouldRelay(1, "sig"));
        Assert.False(d.ShouldRelay(2, "sig"));
    }

    [Fact]
    public void ShouldRelay_RevertToPriorLoadout_RelaysAgain()
    {
        // A → B → A: reverting is a real edit vs the LAST relay (B), so A relays again.
        var d = new LoadoutRelayDedup();
        Assert.True(d.ShouldRelay(1, "A"));
        Assert.True(d.ShouldRelay(1, "B"));
        Assert.True(d.ShouldRelay(1, "A"));
    }

    [Fact]
    public void Reset_DropsAllUnitState()
    {
        var d = new LoadoutRelayDedup();
        Assert.True(d.ShouldRelay(1, "A"));
        Assert.False(d.ShouldRelay(1, "A"));
        d.Reset();
        Assert.True(d.ShouldRelay(1, "A"));   // forgotten → first sight again
    }
}
