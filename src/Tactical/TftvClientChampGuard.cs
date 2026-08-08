using System;
using System.Reflection;
using HarmonyLib;
using PhoenixPoint.Tactical.Entities;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// A CLIENT NEVER MINTS ENEMY IDENTITY. TFTV's <c>TFTVHumanEnemies.GiveRankAndNameToHumaoidEnemy</c>
    /// (TFTVHumanEnemies.cs:1613, called from its own postfix on <c>TacticalLevelController.ActorEnteredPlay</c>
    /// — TFTVHarmonyTactical.cs:421) rolls a human enemy's RANK and NAME from
    /// <c>UnityEngine.Random.InitState((int)Stopwatch.GetTimestamp())</c> (:1631, :1651) the moment that actor
    /// enters play. Every peer runs it on its OWN copy of the actor, so every peer rolls a DIFFERENT champ with
    /// a DIFFERENT name — measured 2026-08-08 13:38:12 on a live 3-instance battle: host
    /// <c>Soldier_11 -&gt; Havara</c>, instance2 <c>Soldier_1 -&gt; Starbuck</c>, instance3
    /// <c>Soldier_1 -&gt; Dag</c>, one battlefield, three different elites. That is host-authoritative content
    /// being recomputed per peer, and it is the same class of defect as a locally-rolled reinforcement wave.
    ///
    /// IT ALSO THREW, WHICH IS HOW IT WAS FOUND. On the way through it calls <c>AdjustStatsAndSkills</c>:1655
    /// -&gt; <c>GetAdjustedLevel</c>:1174, whose first line is <c>tacticalActor.LevelProgression.Level</c>
    /// (:1178, IL 0x00008). A mirrored spawn has no <c>LevelProgression</c>: <c>ApplySpawn</c> hands
    /// <c>TacticalDeployZone.SpawnActor</c> a null instance-data template on purpose, and that field is only
    /// ever populated from instance data (<c>TacticalActor.ProcessInstanceData</c>:647-651). TFTV converts the
    /// NRE into <c>throw new InvalidOperationException()</c> (:1170) rather than a skip, which escapes through
    /// <c>AdjustStatsAndSkills</c>:1429 and PP surfaces as a modal error popup on both clients.
    ///
    /// THE NULL IS NOT THE BUG AND IS NOT FIXED HERE. Null <c>LevelProgression</c> is a shape the game itself
    /// expects — every native reader guards it (<c>TacticalFaction</c>:236 <c>where p.LevelProgression != null</c>,
    /// <c>UIModuleBattleSummary</c>:142, <c>SoldierResultElement</c>:128/177, <c>UIStateCharacterStatus</c>:118,
    /// <c>TacticalTelemetry</c>:109 all use <c>!= null</c> or <c>?.</c>) because it is the shape of any actor
    /// that does not progress. Synthesising one would hand a mirrored enemy XP/skill-point bookkeeping the host
    /// never gave it, purely to satisfy one unguarded foreign dereference. Suppression is the whole fix.
    ///
    /// WHY THIS ENTRY POINT AND ONLY THIS ONE. <c>AdjustStatsAndSkills</c> has exactly two callers in TFTV:
    /// here, and <c>GetGangerReady</c>:676 — which is reached only from <c>AssignHumanEnemiesTags</c> at turn 0,
    /// a callback a client's loaded battle never gets, and which is already stood down for the one window a
    /// client does run it in (<c>TftvMissionHints.SkipMutatingHalf</c>). So one prefix closes the whole path.
    /// The client's own contained spawns never reach it either: <c>ActorEnterPlaySeam</c> prefixes
    /// <c>DoEnterPlay</c>, so a refused spawn never runs <c>FinalizeEnterPlay</c> and never raises
    /// <c>ActorEnteredPlay</c> at all.
    ///
    /// HOST BEHAVIOUR IS UNTOUCHED — the gate is <c>IsHost</c>, and with no session <c>LiveEngine()</c> is null
    /// and TFTV runs exactly as it does today. No TFTV assembly reference: resolved by reflection and bound
    /// through <see cref="Multiplayer.Harmony.TftvLateBinder"/>, because TFTV loads AFTER us and a patch class
    /// whose <c>Prepare()</c> ran at PatchAll time would report false and die silently.
    ///
    /// WHAT CARRIES THE HOST'S ROLL INSTEAD: <see cref="TftvChampIdentity"/>. Suppressing the re-roll only
    /// stopped three peers disagreeing; it left the client with no name and no rank at all, because the 0x84
    /// spawn record carried key/defs/faction/transform/zone and nothing about identity. The rolled name and
    /// the rolled tags now ride that same record, and the client applies them directly — it never calls the
    /// roll this class suppresses.
    /// </summary>
    [HarmonyPatch]
    internal static class TftvClientChampGuard
    {
        private const string TftvHumanEnemiesTypeName = "TFTV.TFTVHumanEnemies";

        private static bool Prepare() => AccessTools.TypeByName(TftvHumanEnemiesTypeName) != null;

        // Name-only lookup on purpose: there is exactly one GiveRankAndNameToHumaoidEnemy, and an exact
        // Type[] match is a needless second way to resolve null and silently stop guarding (AccessTools
        // does no widening). A null here is reported by TftvLateBinder as "NO target".
        private static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName(TftvHumanEnemiesTypeName), "GiveRankAndNameToHumaoidEnemy");

        /// <summary>TFTV'S OWN CANDIDATE LINE, pure so law L325 can execute both directions of it.
        /// <c>GiveRankAndNameToHumaoidEnemy</c> (TFTVHumanEnemies.cs:1617) returns immediately unless
        /// <c>actor.BaseDef.name == "Soldier_ActorDef"</c>, so this is the exact set TFTV would roll: anything
        /// wider suppresses nothing and only makes noise, anything narrower lets a real champ be re-rolled on
        /// this client and the peers disagree about which elite is on the field.</summary>
        internal static bool TftvWouldRoll(string baseDefName) => baseDefName == "Soldier_ActorDef";

        private static bool Prefix(TacticalActorBase actor)
        {
            try
            {
                var engine = TacticalDamageSync.LiveEngine();
                if (engine == null || engine.IsHost) return true;
                // TFTV'S OWN CANDIDATE PREDICATE, not a parameter-type guess. GiveRankAndNameToHumaoidEnemy
                // (TFTVHumanEnemies.cs:1617) does nothing whatever unless `actor.BaseDef.name ==
                // "Soldier_ActorDef"` — every deploy crate, AI waypoint, dropped container, structural target
                // and scenery prop reaches this method and every one of them fails that line, so guarding them
                // suppressed nothing and cost 77 log lines per client (live 3-instance sweep 2026-08-08). This
                // is NARROWER, never weaker: a real champ candidate has to satisfy TFTV's own line to be rolled
                // at all, so it still lands here. It is also the recorded lesson — narrow by the marker the
                // owning code actually tests, not by the parameter type Harmony happens to hand you.
                var baseDef = actor == null ? null : actor.BaseDef;
                if (!TftvWouldRoll(baseDef == null ? null : baseDef.name)) return true;
                Debug.Log("[Multiplayer][tac] TFTV rank/name roll SUPPRESSED on this client for " +
                          (actor == null ? "<null>" : actor.name) + " — the champ, its rank and its name are the " +
                          "host's roll. Running it here would mint a different elite on this screen than the one " +
                          "fighting on the host's, and would throw on the mirrored actor's null LevelProgression.");
                return false;
            }
            catch (Exception e)
            {
                // Never let the guard itself decide a TFTV call by throwing — fall through to native.
                Debug.LogError("[Multiplayer][tac] TFTV champ guard faulted; letting TFTV run (this peer may now " +
                               "disagree with the host about which enemy is the champ): " + e);
                return true;
            }
        }
    }
}
