namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE decision core for the HOST inventory-view auto-open guard (gap-turret-crate-loot, audit D18).
    /// Engine glue lives in <c>InventoryViewGuardPatches</c>; only the decision is here (repo pure-core +
    /// thin-glue pattern, cf. <see cref="TacticalActorLifecycleGate"/>).
    ///
    /// WHY (grounded vs the decompile):
    ///   • HOST hijack — when a relayed CLIENT move finishes on the host, the native crate auto-open fires there
    ///     (<c>OpenCrateAbility.OnActorAbilityExecuted</c> → <c>Activate(crateComponent)</c> → the OpenCrate
    ///     coroutine ends with <c>GetAbility&lt;InventoryAbility&gt;().Activate()</c> → <c>ToInventoryViewState</c>,
    ///     OpenCrateAbility.cs:63) — yanking the HOST's screen into an inventory view for a soldier the host
    ///     player never selected (and <c>UIStateInventory.PrimaryActor</c> reads <c>View.SelectedActor</c>, so it
    ///     would even show the WRONG soldier). The guard suppresses <c>InventoryAbility.Activate</c> on the host
    ///     when the acting soldier is NOT the host's currently selected actor — the host player's own crate walk
    ///     (actor selected) is untouched.
    ///
    /// The CLIENT view is NO LONGER suppressed: mid-mission looting now ships as the inventory-transfer intent
    /// (surfaces 0x9A/0x9B, <see cref="TacticalInventorySync"/>) — the client opens the loot UI and its committed
    /// moves are relayed host-authoritatively, so there is no silent desync to guard against.
    /// </summary>
    public static class TacticalInventoryViewGuard
    {
        /// <summary>HOST: suppress an <c>InventoryAbility.Activate</c> whose acting soldier is not the host's
        /// currently SELECTED actor (that is the relayed-move auto-open — a host player driving a soldier always
        /// has it selected). False off-host (single-player callers pass isHost=false and never suppress).</summary>
        public static bool ShouldSuppressHostAutoInventoryView(bool isHost, bool actorIsSelected)
            => isHost && !actorIsSelected;
    }
}
