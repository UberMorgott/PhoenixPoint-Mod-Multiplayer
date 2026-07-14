namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE decision core for the HOST inventory-view auto-open guard (gap-turret-crate-loot, audit D18).
    /// Engine glue lives in <c>InventoryViewGuardPatches</c>; only the decision is here (repo pure-core +
    /// thin-glue pattern, cf. <see cref="TacticalActorLifecycleGate"/>).
    ///
    /// WHY (grounded vs the decompile + the 3-instance live log, 2026-07-14):
    ///   • HOST hijack — when a relayed CLIENT move finishes on the host, the native crate auto-open fires there
    ///     (<c>OpenCrateAbility.OnActorAbilityExecuted</c> → <c>Activate(crateComponent)</c> → the OpenCrate
    ///     coroutine ends with <c>GetAbility&lt;InventoryAbility&gt;().Activate()</c> → <c>ToInventoryViewState</c>,
    ///     OpenCrateAbility.cs:63) — yanking the HOST's screen into an inventory view for a soldier whose move the
    ///     host player never made. The guard suppresses <c>InventoryAbility.Activate</c> on the host when the acting
    ///     soldier's most recent move was a RELAYED client intent — the host player's own crate walk is untouched.
    ///
    ///   • DISCRIMINATOR (rca-inventory 2026-07-14): the earlier proxy "acting soldier is NOT the host's SELECTED
    ///     actor" is UNRELIABLE — the host player can have the CLIENT's soldier selected (watching it), so a relayed
    ///     move's auto-open passed the `!selected` test and still hijacked the host (confirmed in the host Player.log:
    ///     `HOST@relay direct SetAbilities actorNet=1` proves View.SelectedActor == the relayed actor, yet the loot
    ///     UI opened). The real signal is the MOVE ORIGIN: was this soldier's most recent move host-locally initiated
    ///     or a relayed client intent (HostOnMoveIntent)? Recorded at the single host move-start chokepoint
    ///     (<c>TacticalMoveSync.HostBroadcastMoveStart</c> via the host-apply window) and consumed here.
    ///
    /// The CLIENT view is NO LONGER suppressed: mid-mission looting ships as the inventory-transfer intent
    /// (surfaces 0x9A/0x9B, <see cref="TacticalInventorySync"/>) — the client opens the loot UI and its committed
    /// moves are relayed host-authoritatively, so there is no silent desync to guard against.
    /// </summary>
    public static class TacticalInventoryViewGuard
    {
        /// <summary>HOST: suppress an <c>InventoryAbility.Activate</c> whose acting soldier's most recent move was a
        /// RELAYED client intent (that is the client-move crate auto-open running on the host). The host player's own
        /// crate walk records a host-local origin → not suppressed. False off-host (single-player / client callers
        /// pass isHost=false and never suppress).</summary>
        public static bool ShouldSuppressHostAutoInventoryView(bool isHost, bool lastMoveWasRelayed)
            => isHost && lastMoveWasRelayed;
    }
}
