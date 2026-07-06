using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using Multiplayer.Network.Sync.Actions;
using Multiplayer.Sync.Geoscape;
using UnityEngine;

namespace Multiplayer.Harmony.Sync
{
    /// <summary>
    /// Generic relay interceptor for the sim-mutating GEOSCAPE abilities: <c>GeoAbility.Activate(GeoAbilityTarget)</c>
    /// (GeoAbility.cs:130) — the ONE entry chokepoint every ability activation flows through (the seven relayed
    /// subclasses override only <c>ActivateInternal</c>, never <c>Activate</c>, so binding the base method covers
    /// them all). Scoped by <see cref="GeoAbilityRelay.IsRelayable"/>: an off-list ability (e.g.
    /// <c>MoveVehicleAbility</c>, whose travel is relayed by its own <c>StartTravel</c> patch) passes straight
    /// through — no ability is double-handled.
    ///
    /// On a co-op CLIENT the sim is frozen, so a local activation neither advances (its scheduled effect never
    /// ticks) nor reaches the host → the order silently dies. So: CLIENT → relay the intent
    /// (<see cref="GeoAbilityActivateAction"/>) + SUPPRESS the local activation (reporting success to the
    /// bool-ignoring UI caller); HOST → run the authoritative activation + broadcast. The result mirrors back on
    /// the existing geoscape state channels — the client never simulates.
    ///
    /// Game types are NEVER hard-referenced: the target resolves via AccessTools reflection; Prepare() returns
    /// false (Harmony skips the patch) when the geoscape types are absent. Mirrors <c>MoveVehiclePatch</c>.
    /// </summary>
    [HarmonyPatch]
    public static class GeoAbilityActivatePatch
    {
        private static MethodBase _target;

        public static bool Prepare()
        {
            var abilityT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Abilities.GeoAbility");
            var targetT = AccessTools.TypeByName("PhoenixPoint.Geoscape.Entities.Abilities.GeoAbilityTarget");
            if (abilityT == null || targetT == null) return false;
            // Exact param match pins Activate(GeoAbilityTarget) over the obsolete Activate(object) overload.
            _target = AccessTools.Method(abilityT, "Activate", new[] { targetT });
            return _target != null;
        }

        public static MethodBase TargetMethod() => _target;

        // __instance = the GeoAbility; __args[0] = the boxed GeoAbilityTarget struct. __result = the bool return
        // (every caller ignores it — verified in the decompile — so reporting true on suppress is safe).
        // __state carries the host action to broadcast AFTER the original succeeds (Postfix).
        public static bool Prefix(object __instance, object[] __args, ref bool __result, out ISyncedAction __state)
        {
            __state = null;
            if (SyncApplyScope.IsApplying) return true;   // engine-driven host apply/replay → run the real activation
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return true;   // single-player / no session

            try
            {
                string typeName = __instance?.GetType().Name;
                if (!GeoAbilityRelay.IsRelayable(typeName)) return true;   // off-list ability → native path

                if (!PermissionGate.Check(ActionCategory.GeoAbility))
                {
                    PermissionGate.Notify(ActionCategory.GeoAbility);
                    __result = false;
                    return false;
                }

                object target = (__args != null && __args.Length > 0) ? __args[0] : null;
                var action = GeoAbilityRelayReflection.TryBuildIntent(__instance, target);
                if (action == null) return true;   // unresolvable → run native locally (no-op on frozen client, no wedge)

                // Host: defer the broadcast to the Postfix so a throwing original suppresses it (no desync).
                if (engine.IsHost) { __state = action; return true; }
                engine.Sync.SendActionRequest(action);
                __result = true;   // client: report success; block the local (frozen) activation — host mirrors back
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] GeoAbilityActivatePatch failed: " + ex.Message);
                return true;
            }
        }

        // Host-only (via __state) and only on a normal return of the original → broadcast the confirmed activation.
        public static void Postfix(ISyncedAction __state)
        {
            if (__state == null) return;
            try { NetworkEngine.Instance?.Sync?.BroadcastHostAction(__state); }
            catch (Exception ex) { Debug.LogError("[Multiplayer] GeoAbilityActivatePatch postfix broadcast failed: " + ex.Message); }
        }
    }
}
