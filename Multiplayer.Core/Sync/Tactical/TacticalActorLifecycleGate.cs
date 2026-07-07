using System.Collections.Generic;

namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE decision core for the mid-battle actor-lifecycle mirror (spec TS1): the host spawn-broadcast gate and
    /// the host despawn sweep. Engine-free so both decisions unit-test without UnityEngine or the game assembly
    /// (the reflection glue lives in <see cref="TacticalActorLifecycleSync"/>), matching the repo's pure-core +
    /// thin-engine-glue pattern (cf. <c>TacticalActorStateDiff</c>, <c>NullFactionEnterPlayGate</c>).
    /// </summary>
    public static class TacticalActorLifecycleGate
    {
        /// <summary>
        /// AUDITED spawn-funnel coverage (scripted mission events part 2, audit D11/D12/D26): every native
        /// mid-battle spawn path verified (2026-07-07, game decompile) to reach the ONE 0x92 chokepoint —
        /// <c>TacticalLevelController.ActorEnteredPlay</c>, invoked unconditionally from
        /// <c>TacticalActorBase.FinalizeEnterPlay</c> (TacticalActorBase.cs:550) for EVERY actor entering play
        /// (all spawned entities are <c>TacticalActorBase</c> subclasses: TacticalActor, ItemContainer,
        /// StructuralTarget, OffMapActor). <c>ActorSpawner.SpawnActor</c> defaults
        /// <c>callEnterPlayOnActor:true</c> (ActorSpawner.cs:12-26); the only <c>false</c> caller in tactical
        /// (StructuralTargetDeployment.cs:94) calls <c>DoEnterPlay()</c> explicitly at :101. No bypasses exist —
        /// this list documents the audited sources and is PINNED by a test so a silent edit trips review:
        ///   • SpawnActorAbility          — ActorSpawner.SpawnActor (SpawnActorAbility.cs:131)
        ///   • MorphIntoActorAbility      — SpawnActorAbility subclass (egg hatch); consumed body → despawn sweep
        ///   • MassHatchAbility           — delegates to MorphIntoActorAbility per egg (MassHatchAbility.cs:35)
        ///   • CallReinforcementsAbility  — TacParticipantSpawn.DeployForTurn → DoSpawnActor →
        ///                                  TacticalDeployZone.SpawnActor → ActorSpawner (TacParticipantSpawn.cs:591)
        ///   • DeathBelcherAbility        — ActorSpawner.SpawnActor (DeathBelcherAbility.cs:84)
        ///   • ResurrectAbility           — ActorSpawner.SpawnActor (ResurrectAbility.cs:156)
        ///   • SpawnChildActorStatus      — ActorSpawner.SpawnActor (SpawnChildActorStatus.cs:98)
        ///   • EnterPlayAbility           — ANIMATION-only (no spawn); its actor was funneled by its creator
        ///   • SpawnActorEffect           — damage-type effect spawn (SpawnActorEffect.cs:62)
        ///   • OffMapActorDeployment      — off-map arrival actors (OffMapActorDeployment.cs:36)
        ///   • StructuralTargetDeployment — explicit DoEnterPlay (StructuralTargetDeployment.cs:94-101)
        ///   • HulkDieAbility             — ItemContainer corpse (HulkDieAbility.cs:61)
        ///   • TacticalItem               — dropped-item ground container (TacticalItem.cs:691)
        ///   • UIStateInventory           — drop-down ground container (UIStateInventory.cs:521)
        /// </summary>
        public static readonly string[] AuditedSpawnFunnelSources =
        {
            "SpawnActorAbility",
            "MorphIntoActorAbility",
            "MassHatchAbility",
            "CallReinforcementsAbility",
            "DeathBelcherAbility",
            "ResurrectAbility",
            "SpawnChildActorStatus",
            "EnterPlayAbility",
            "SpawnActorEffect",
            "OffMapActorDeployment",
            "StructuralTargetDeployment",
            "HulkDieAbility",
            "TacticalItem",
            "UIStateInventory",
        };

        /// <summary>HOST: broadcast a mid-battle spawn for an actor entering play? True iff the turn-0 deploy
        /// snapshot has already been captured (so deploy-time actors — which ride the 0x80 snapshot — are excluded)
        /// AND the actor is not already registered (a deploy actor / an already-mirrored spawn) AND we are not inside
        /// a remote apply (the client's own materialize must never re-emit). The session/host/side checks are engine
        /// concerns handled by the caller.</summary>
        public static bool ShouldBroadcastSpawn(bool deployCaptured, bool alreadyRegistered, bool applyingRemote)
            => deployCaptured && !alreadyRegistered && !applyingRemote;

        /// <summary>HOST despawn sweep (pure set arithmetic): given the registered (netId → actor-key) pairs and the
        /// set of CURRENTLY-LIVE actor keys, return the netIds whose actor is no longer live — a non-damage despawn
        /// (evac / morph-consume / off-map-depart / expiry). Order is preserved (registry enumeration order). A null
        /// actor-key is skipped; a null live set treats everything as despawned (defensive).</summary>
        public static List<int> ComputeDespawnedNetIds<TKey>(
            IEnumerable<KeyValuePair<int, TKey>> registered, ISet<TKey> liveKeys) where TKey : class
        {
            var result = new List<int>();
            if (registered == null) return result;
            foreach (var kv in registered)
            {
                if (kv.Value == null) continue;
                if (liveKeys == null || !liveKeys.Contains(kv.Value)) result.Add(kv.Key);
            }
            return result;
        }
    }
}
