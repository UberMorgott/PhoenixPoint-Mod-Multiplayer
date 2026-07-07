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
    /// (OpenCrateAbility.cs:63) — yanking the host player's screen into an inventory view (and
    /// <c>UIStateInventory.PrimaryActor</c> reads <c>View.SelectedActor</c>, so it would show the WRONG soldier).
    /// <see cref="InventoryAbilityHostViewGuardPatch"/> suppresses that Activate when the acting soldier is not
    /// the host's SELECTED actor; the host's own crate walk (soldier selected) is untouched. Auto-register via PatchAll.
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
                object actor = GetProp(__instance, "TacticalActor");
                object view = GetProp(GetProp(actor, "TacticalLevel"), "View");
                object selected = GetProp(view, "SelectedActor");
                bool actorIsSelected = actor != null && ReferenceEquals(selected, actor);
                if (TacticalInventoryViewGuard.ShouldSuppressHostAutoInventoryView(engine.IsHost, actorIsSelected))
                {
                    Debug.Log("[Multiplayer][tac] HOST suppressed InventoryAbility.Activate for an UNSELECTED " +
                              "soldier (relayed-move crate auto-open) — host view not hijacked");
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
