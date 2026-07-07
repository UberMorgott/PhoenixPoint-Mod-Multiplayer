using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// TS2 GENERIC (non shoot/melee) ability-intent relay patch. Mirrors <see cref="AbilityActivateRelayPatch"/>
    /// but binds <c>Activate(object)</c> on the GENERIC allowlist types
    /// (<see cref="TacticalAbilityRelay.RelayableGenericAbilityTypeNames"/> — Heal / RecoverWill / Rally /
    /// PsychicScream / Reload / Interact / ExitMission / EvacuateMounted + the gap-turret-crate-loot set:
    /// DeployTurret / ThrowTurret / DeployShield / RetrieveDeployedItem / RetrieveShield / OpenCrate / DropItem),
    /// routing them to <see cref="TacticalCombatSync.ClientInterceptGenericAbility"/>:
    ///   • CLIENT (mirroring): send ONE <c>tac.intent.generic</c> (0x8E) and SUPPRESS the local activation — the
    ///     host runs it authoritatively; the outcome rides 0x8F (AP/WP/Health/status) + tac.damage + TS1 spawn.
    ///   • HOST / single-player: <c>ClientInterceptGenericAbility</c> returns true → the native ability runs
    ///     unchanged (the host is never patched to relay — it is the authority).
    /// Binding: every allowlisted type EXCEPT DeployTurret/ThrowTurret DECLARES its own <c>Activate(object)</c>
    /// override, so each binds EXACTLY. DeployTurret/ThrowTurret INHERIT <c>SpawnActorAbility.Activate</c>
    /// (SpawnActorAbility.cs:41) — <c>AccessTools.Method</c> resolves both to that ONE base method, the
    /// <c>seen</c> set dedupes it to a single bind, and the prefix therefore ALSO fires for the off-list sibling
    /// <c>MorphIntoActorAbility</c>: the runtime-type allowlist gate inside
    /// <c>ClientInterceptGenericAbility</c> returns true for it → the native morph runs unchanged (host-side
    /// enemy path; client AI is suppressed anyway). The 0x87 shoot/melee relay
    /// (<see cref="AbilityActivateRelayPatch"/>) is UNTOUCHED and the two sets are disjoint, so no ability is
    /// double-prefixed. Auto-register via PatchAll; reflection targets.
    /// </summary>
    [HarmonyPatch]
    public static class GenericAbilityRelayPatch
    {
        // One prefix bound to Activate(object) on EACH generic-relayable TacticalAbility subclass. Deduped so a
        // type that (in a future edit) shared an inherited base Activate can't yield the same MethodBase twice.
        public static IEnumerable<MethodBase> TargetMethods()
        {
            const string ns = "PhoenixPoint.Tactical.Entities.Abilities.";
            var seen = new HashSet<MethodBase>();
            foreach (var name in TacticalAbilityRelay.RelayableGenericAbilityTypeNames)
            {
                var t = AccessTools.TypeByName(ns + name);
                if (t == null) { Debug.LogError("[Multiplayer][tac] generic relay: ability type not found: " + ns + name); continue; }
                // public override void Activate(object parameter) — EXACT (object) param match per subclass.
                var m = AccessTools.Method(t, "Activate", new[] { typeof(object) });
                if (m == null) { Debug.LogError("[Multiplayer][tac] generic relay: Activate(object) not found on " + name); continue; }
                if (seen.Add(m)) yield return m;
            }
        }

        // Returns false to SUPPRESS the local activation on a mirroring client (intent already sent to host),
        // true otherwise (host / single-player / off-list runtime type run the real ability).
        public static bool Prefix(object __instance, object parameter)
        {
            try { return TacticalCombatSync.ClientInterceptGenericAbility(__instance, parameter); }
            catch (System.Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] GenericAbilityRelayPatch.Prefix failed: " + ex);
                return true;   // fail-open: never wedge the native ability on an unexpected error
            }
        }
    }
}
