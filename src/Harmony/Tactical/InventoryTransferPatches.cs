using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// Mid-mission INVENTORY-TRANSFER commit intercept (tactical loot UI re-enable). Thin Harmony glue over
    /// <see cref="TacticalInventorySync"/>; all decisions + wire work live there.
    ///
    /// Target = the ONE native commit funnel <c>UIStateInventory.ApplyInventoryActions</c> (private; called once,
    /// from <c>ExitState</c>, after <c>AttemptMoveItems</c> — it invokes <c>InventoryQuery.SyncItems</c> which is
    /// the sole tactical path into <c>InventoryComponent.RemoveItem</c>/<c>AddItem</c>). One [HarmonyPatch] class
    /// with BOTH a prefix and a postfix on that method:
    ///   • PREFIX — capture the committed batch of cross-inventory moves. On a mirroring CLIENT: relay the intent
    ///     (0x9A) and RETURN false so the native SyncItems is SKIPPED (host-authoritative; the outcome mirrors
    ///     back). On the HOST co-op session: return true (native runs) and stash the batch for the postfix. SP /
    ///     no session: return true (native runs, nothing captured).
    ///   • POSTFIX — HOST only: broadcast the stashed batch (0x9B) so every client mirrors the host's own looting.
    ///     No-op on a client (the prefix suppressed) or single-player. Postfixes run even when a prefix skipped the
    ///     original, so the client path is a clean no-op here.
    /// Fail-open: any error runs/keeps native behaviour (a mirroring client still suppresses — it must never commit
    /// a local loot move that reaches no host). Auto-register via PatchAll.
    /// </summary>
    [HarmonyPatch]
    public static class InventoryCommitRelayPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var t = AccessTools.TypeByName("PhoenixPoint.Tactical.View.ViewStates.UIStateInventory");
            if (t == null) return false;
            // private void ApplyInventoryActions() — single no-arg method on UIStateInventory (name-only is exact).
            _target = AccessTools.Method(t, "ApplyInventoryActions");
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        public static bool Prefix(object __instance)
        {
            try
            {
                // OnApplyInventoryActions returns true when the local commit must be SKIPPED (client suppress);
                // a Harmony prefix returns false to skip the original.
                return !TacticalInventorySync.OnApplyInventoryActions(__instance);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] InventoryCommitRelayPatch prefix failed (fail-open native): " + ex);
                return true;   // run native SyncItems on an unexpected error
            }
        }

        public static void Postfix()
        {
            try { TacticalInventorySync.OnApplyInventoryActionsPost(); }
            catch (Exception ex) { Debug.LogError("[Multiplayer][tac] InventoryCommitRelayPatch postfix failed: " + ex); }
        }
    }
}
