namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE decision core for the two tactical INVENTORY-VIEW guards (gap-turret-crate-loot, audit D18).
    /// Engine glue lives in <c>InventoryViewGuardPatches</c>; only the decisions are here (repo pure-core +
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
    ///   • CLIENT silent desync — the tactical inventory view mutates inventories LOCALLY on exit
    ///     (<c>UIStateInventory.ExitState</c> → <c>AttemptMoveItems</c>/<c>ApplyInventoryActions</c> →
    ///     <c>InventoryQuery.SyncItems</c>): on a frozen mirror those item moves reach NO host — items "looted"
    ///     by the client would silently not exist post-mission (host computes results from HOST state). Per the
    ///     sync canon ("degrade-to-notify, never silent desync") the mirror suppresses ALL entries into the
    ///     inventory view until the inventory-transfer intent surface ships. Precedent: ChoseSecondSpecialization
    ///     is likewise suppressed-without-relay on clients.
    /// </summary>
    public static class TacticalInventoryViewGuard
    {
        /// <summary>HOST: suppress an <c>InventoryAbility.Activate</c> whose acting soldier is not the host's
        /// currently SELECTED actor (that is the relayed-move auto-open — a host player driving a soldier always
        /// has it selected). False off-host (client handling is the view-level guard below; single-player callers
        /// pass isHost=false and never suppress).</summary>
        public static bool ShouldSuppressHostAutoInventoryView(bool isHost, bool actorIsSelected)
            => isHost && !actorIsSelected;

        /// <summary>CLIENT: suppress EVERY entry into the tactical inventory view while mirroring (all three
        /// native entry points funnel through <c>TacticalView.ToInventoryViewState</c>). Degrade-to-notify —
        /// mid-mission loot/inventory edits are not yet relayed, so a local edit would silently desync.</summary>
        public static bool ShouldSuppressClientInventoryView(bool isClientMirroring)
            => isClientMirroring;
    }
}
