using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// gap-turret-crate-loot (audit D18) — the HOST inventory-view auto-open guard. The decision is pure
    /// (<see cref="TacticalInventoryViewGuard"/>, unit-tested); this file is the thin Harmony glue.
    ///
    /// HOST hijack (live co-op bug): a relayed CLIENT move that ends next to a crate runs the native auto-open
    /// ON THE HOST (<c>OpenCrateAbility.OnActorAbilityExecuted</c> fires from the host's real MoveAbility) whose
    /// coroutine ends with <c>GetAbility&lt;InventoryAbility&gt;().Activate()</c> → <c>ToInventoryViewState</c>
    /// (OpenCrateAbility.cs:63) — yanking the host player's screen into an inventory view.
    /// <see cref="InventoryAbilityHostViewGuardPatch"/> suppresses that Activate when the acting soldier's most
    /// recent move was a RELAYED client intent (origin tagged at <c>TacticalMoveSync.HostBroadcastMoveStart</c>,
    /// consumed here); the host's own crate walk (host-local move origin) is untouched. The earlier "is the actor
    /// the host's SELECTED actor" proxy was UNRELIABLE — the host player can have the client's soldier selected, so
    /// the relayed auto-open still hijacked the host (rca-inventory, 3-instance log 2026-07-14). Auto-register via PatchAll.
    ///
    /// The CLIENT loot view is NO LONGER suppressed — mid-mission looting now ships as the inventory-transfer
    /// intent (<see cref="Multiplayer.Sync.Tactical.TacticalInventorySync"/>, surfaces 0x9A/0x9B, patched at the
    /// <c>UIStateInventory.ApplyInventoryActions</c> commit funnel in <c>InventoryTransferPatches</c>).
    /// </summary>
    [HarmonyPatch]
    public static class InventoryAbilityHostViewGuardPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.InventoryAbility");
            if (t == null) return false;
            // public override void Activate(object parameter = null) — declared on InventoryAbility (exact bind).
            _target = AccessTools.Method(t, "Activate", new[] { typeof(object) });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(object __instance)
        {
            try
            {
                var engine = NetworkEngine.Instance;
                if (engine == null || !engine.IsActive) return true;   // single-player: native
                if (!engine.IsHost) return true;                        // only the HOST auto-runs relayed client moves
                object actor = GetProp(__instance, "TacticalActor");
                // Discriminator = the acting soldier's MOVE ORIGIN, not "is it selected" (the host can have the
                // client's soldier selected — that broke the old guard, live log 2026-07-14). A relayed client move
                // tagged the actor at HostBroadcastMoveStart; consume it so a later manual host open still works.
                int netId = TacticalDeploySync.NetIdForLiveActor(actor);
                bool lastMoveWasRelayed = TacticalMoveSync.ConsumeLastMoveWasRelayed(netId);
                if (TacticalInventoryViewGuard.ShouldSuppressHostAutoInventoryView(engine.IsHost, lastMoveWasRelayed))
                {
                    Debug.Log("[Multiplayer][tac] HOST suppressed InventoryAbility.Activate — crate auto-open from a " +
                              "RELAYED client move (netId=" + netId + ") — host view not hijacked");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] InventoryAbilityHostViewGuardPatch failed (fail-open): " + ex);
                return true;   // never wedge the native ability on an unexpected error
            }
        }

        private static object GetProp(object obj, string name)
            => obj == null ? null : AccessTools.Property(obj.GetType(), name)?.GetValue(obj, null);
    }
}
