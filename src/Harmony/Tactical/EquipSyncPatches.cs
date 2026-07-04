using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// LIVE host-authoritative WEAPON / EQUIPMENT-SWAP replication patch (Inc Equip). ONE Harmony patch on the
    /// single universal swap choke point <c>EquipmentComponent.SetSelectedEquipment(Equipment)</c> — the sink ALL
    /// selection paths funnel through (the player weapon-wheel UI, the AI, reload, drop, deploy, death cascade).
    /// Mirrors the move/shoot model with BOTH a prefix and a postfix on the same target:
    ///   • PREFIX (client mirror): send <c>tac.intent.equip</c> and SUPPRESS the local swap (return false). On the
    ///     host / single-player, or while re-applying a host outcome, it passes through (return true).
    ///   • POSTFIX (host): after the real swap ran on the host, broadcast the now-selected equipment as
    ///     <c>tac.equip</c>. <see cref="TacticalEquipSync.OnHostEquipChanged"/> internally gates IsHost + the
    ///     re-entrancy flag, so binding the postfix on the client is a harmless no-op.
    /// Both delegate to <see cref="TacticalEquipSync"/>. Auto-registers via PatchAll; reflection target so an
    /// engine rename never PatchAll-bombs (Prepare returns false → the class is skipped).
    /// </summary>
    [HarmonyPatch]
    public static class SetSelectedEquipmentPatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var ec = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Equipments.EquipmentComponent");
            if (ec == null) return false;
            var equipment = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Equipments.Equipment");
            if (equipment == null) return false;
            // public void SetSelectedEquipment(Equipment equipment) — EXACT param match (the Equipment type).
            _target = AccessTools.Method(ec, "SetSelectedEquipment", new[] { equipment });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // Returns false to SUPPRESS the local swap on a mirroring client (intent already sent to host),
        // true otherwise (host / single-player run the real swap; the postfix then broadcasts on the host).
        // __0 is the Equipment argument (may be null — the engine supports clearing the selection).
        public static bool Prefix(object __instance, object __0)
        {
            try { return TacticalEquipSync.ClientInterceptEquip(__instance, __0); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] SetSelectedEquipmentPatch.Prefix failed: " + ex);
                return true;   // fail-open: never wedge the native swap on an unexpected error
            }
        }

        // Postfix so it reads the FINAL SelectedEquipment after the engine committed it. Host-gated inside.
        public static void Postfix(object __instance)
        {
            try { TacticalEquipSync.OnHostEquipChanged(__instance); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] SetSelectedEquipmentPatch.Postfix failed: " + ex);
            }
        }
    }
}
