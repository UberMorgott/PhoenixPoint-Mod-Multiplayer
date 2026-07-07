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
    /// DeployTurret / ThrowTurret / DeployShield / RetrieveDeployedItem / RetrieveShield / OpenCrate / DropItem
    /// + the gap-ability-allowlist-wave2 set: EnterVehicle / ExitVehicle / MindControl / InstilFrenzy / JetJump /
    /// CaterpillarMove / Reposition / Ram), routing them to
    /// <see cref="TacticalCombatSync.ClientInterceptGenericAbility"/>:
    ///   • CLIENT (mirroring): send ONE <c>tac.intent.generic</c> (0x8E) and SUPPRESS the local activation — the
    ///     host runs it authoritatively; the outcome rides 0x8F (AP/WP/Health/status) + tac.damage + TS1 spawn.
    ///   • HOST / single-player: <c>ClientInterceptGenericAbility</c> returns true → the native ability runs
    ///     unchanged (the host is never patched to relay — it is the authority).
    /// Binding: every allowlisted type EXCEPT DeployTurret/ThrowTurret/Reposition DECLARES its own
    /// <c>Activate(object)</c> override, so each binds EXACTLY (CaterpillarMove declares one too —
    /// CaterpillarMoveAbility.cs:35 — so it never re-binds the dedicated-patched MoveAbility.Activate).
    /// DeployTurret/ThrowTurret INHERIT <c>SpawnActorAbility.Activate</c> (SpawnActorAbility.cs:41) and
    /// Reposition INHERITS the sealed <c>TacticalHurtReactionAbility.Activate</c>
    /// (TacticalHurtReactionAbility.cs:43) — <c>AccessTools.Method</c> resolves each subclass to its ONE base
    /// method, the <c>seen</c> set dedupes it to a single bind, and the prefix therefore ALSO fires for the
    /// off-list siblings (<c>MorphIntoActorAbility</c>; <c>SpawnMistAbility</c>/<c>StartPreparingAbility</c>/
    /// <c>YuggothShieldsAbility</c>): the runtime-type allowlist gate inside
    /// <c>ClientInterceptGenericAbility</c> returns true for them → they run native (host-side enemy paths;
    /// client AI is suppressed anyway, and mirror-local hurt reactions stay no-op'd by the coexisting
    /// <c>HurtReactionActivateSuppressPatch</c> prefix on that same base method — all prefixes run, any false
    /// skips the original). The 0x87 shoot/melee relay
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
