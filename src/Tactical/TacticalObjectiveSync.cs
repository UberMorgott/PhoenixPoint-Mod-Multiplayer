using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Tactical.Levels.Objectives;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// ONE BOOL PER OBJECTIVE, AND IT IS THE ONE THE GAME ALREADY SERIALIZES.
    /// <c>TacticalObjective.IsCompleted</c> (<c>:13-14</c>) is a <c>[SerializeMember]</c> with exactly one
    /// writer, <c>SetIsCompleted</c>:36-46, reached from <c>TriggerObjective</c>:57-71 — and from
    /// <c>TacticalZoneObjective</c>:54-71, which fires per tile off <c>ActorMovedInNewTileEvent</c>.
    ///
    /// WHY IT NEEDS A RAIL AT ALL, given that every peer creates the same objectives from the same defs and
    /// every evaluator is a pure function of replicated actor state: the TRIGGER is not. A zone objective is
    /// completed by a tile event on the peer whose actor actually walked, an interaction objective by the
    /// ability that ran, and a client's own reaction is blocked by design (L83) — so the bool is a per-peer
    /// conclusion drawn from per-peer events, and the peers that did not observe the event keep an objective
    /// open that the host has closed. <c>TacticalFaction.GiveExperienceForObjectives</c> is then computed
    /// locally against two different answers, which is the missionwide XP mismatch this repo has chased twice.
    ///
    /// THE ADDRESS IS THIS RAIL'S ACTOR KEY, no new identity: a <c>TacticalObjective</c> IS a
    /// <c>TacticalActorBase</c> (<c>:9</c>) and it is PLACED IN THE SCENE, so since the battle-start key
    /// became a content hash it carries its own <c>SceneObjectId</c> guid on every peer
    /// (<see cref="TacticalActorKey"/>). Under the ordinal scheme this rail could not have existed: an
    /// objective's key moved whenever the roster count did.
    ///
    /// NO NEW SURFACE — op <see cref="TacticalDamageSync.OpObjective"/> on 0x84, for the reason A4 gave for
    /// spawn and death: one seq stream, so the objective cannot land before the actor whose move completed it.
    ///
    /// REACTIVITY (postulate 1) IS THE 0x84 PATH'S OWN, and deliberately not the game's event.
    /// <c>ObjectivesManager.OnObjectivesChanged</c> is raised only for a def that sets
    /// <c>UpdateStateOnObjectiveChanged</c> (<c>ObjectivesManager</c>:89-92,
    /// <c>FactionObjectiveDef</c>:37), so a rail-applied change cannot rely on it.
    /// <c>TacticalDamageSync.HandleInbound</c> marks <see cref="TacticalUiRepaint"/> dirty after EVERY op on
    /// this surface, and its Exit+Enter of <c>UIStateCharacterSelected</c> runs that state's own
    /// <c>EnterState</c>:398 — <c>_objectivesModule.Init(base.Context)</c>, the very call
    /// <c>UpdateObjectives</c>:419-422 makes when the native event does fire. So an open objectives panel
    /// repaints on the frame the bool lands, through the game's own initializer.
    /// </summary>
    internal static class TacticalObjectiveSync
    {
        /// <summary>HOST: the objective closed here, so it closes everywhere. A postfix and not a prefix —
        /// <c>SetIsCompleted</c> refuses the write outright when <c>CanBeCompleted</c> is false (:38), and
        /// what ships must be what the model actually holds. Re-sending an unchanged value costs one message
        /// on a rare event and the receiver's own <c>SetIsCompleted</c> is a no-op for it (:38 again), so no
        /// change-detection state is kept here.</summary>
        [HarmonyPatch(typeof(TacticalObjective), nameof(TacticalObjective.SetIsCompleted))]
        internal static class SetIsCompletedSeam
        {
            private static void Postfix(TacticalObjective __instance)
            {
                var engine = TacticalDamageSync.LiveEngine();
                if (engine == null || !engine.IsHost) return;
                if (SyncApplyScope.Active) return;              // law 8: a mirrored write never echoes back
                if (ReferenceEquals(__instance, null)) return;
                int key = TacticalActorKey.Of(__instance);
                if (key == 0)
                {
                    Debug.LogWarning("[Multiplayer][tac] an objective completed on the host before this battle's " +
                                     "key map was built, so no peer can be told WHICH objective it was. The other " +
                                     "peers keep it open until they complete it themselves, and their " +
                                     "GiveExperienceForObjectives will disagree with this one's.");
                    return;
                }
                bool done = __instance.IsCompleted;
                TacticalDamageSync.Send(TacticalDamageSync.OpObjective,
                    "objective key=" + key + " completed=" + done,
                    w => { w.Write(key); w.Write(done); });
            }
        }

        /// <summary>CLIENT: take the host's answer through the game's own writer, so the objective's
        /// <c>ObjectiveEffectOnComplete</c> (:43) is applied here by the engine rather than re-implemented.</summary>
        internal static void ApplyObjective(BinaryReader r)
        {
            int key = r.ReadInt32();
            bool done = r.ReadBoolean();
            string why;
            var found = TacticalActorKey.Resolve(TacticalDamageSync.Tlc(), key, out why);
            var objective = found as TacticalObjective;
            if (objective == null)
            {
                Debug.LogError("[Multiplayer][tac] the host completed objective " + key + " and " +
                               (found == null
                                   ? why
                                   : "that key names a " + found.GetType().Name + " here, not an objective") +
                               " — this peer's objective stays as it was, so its mission outcome and its " +
                               "objective experience will differ from the host's.");
                return;
            }
            if (objective.IsCompleted == done) return;
            using (SyncApplyScope.Enter())
                objective.SetIsCompleted(done);
            Debug.Log("[Multiplayer][tac] objective " + key + " set completed=" + done + " from the host.");
        }

        /// <summary>DIAGNOSTIC ONLY — this changes no behaviour and is here to end a question two source
        /// investigations could not answer.
        ///
        /// The top-left objective list did not appear on the client on 2026-08-08. The obvious suspect was
        /// <c>UIModuleObjectives.Init</c>:22-35 reading <c>LevelController.CurrentFaction.Objectives</c>
        /// rather than the VIEWER's (the geoscape twin reads <c>Context.ViewerFaction.Objectives</c>,
        /// <c>UIStateNothingSelected</c>:104) — but that peer's own log disproves it as a cause of NEVER:
        /// the objectives WERE created there ("Adding objective of type ActivateConsoleFactionObjective ...
        /// for faction Phoenix"), <c>Init</c> ran 227 times after that through
        /// <c>UIStateCharacterSelected.EnterState</c>:397, and no exception was thrown on that path. A
        /// wrong faction at one Init would have repaired itself on the next.
        ///
        /// So the missing value is the one nothing prints: WHICH faction each of those 227 inits read, and
        /// whether the element set it built was actually empty. This prints exactly that, and only when the
        /// answer changes — no fix is guessed on top of it. Delete it once a playtest has answered.</summary>
        [HarmonyPatch(typeof(PhoenixPoint.Tactical.View.ViewModules.UIModuleObjectives),
                      nameof(PhoenixPoint.Tactical.View.ViewModules.UIModuleObjectives.Init))]
        internal static class ObjectivesPanelProbe
        {
            private static string _last;

            private static void Postfix(PhoenixPoint.Tactical.View.ViewModules.UIModuleObjectives __instance,
                                        PhoenixPoint.Tactical.View.TacticalViewContext Context)
            {
                var engine = TacticalDamageSync.LiveEngine();
                if (engine == null || engine.IsHost) return;
                if (!__instance.UseMissionObjectives) return;
                var faction = Context?.LevelController?.CurrentFaction;
                var objectives = faction?.Objectives;
                int total = objectives?.Count ?? -1;
                int shown = objectives == null ? -1 : objectives.Count(o => !o.IsUiHidden);
                string now = (faction?.TacticalFactionDef?.name ?? "<null>") + " total=" + total + " shown=" + shown;
                if (now == _last) return;
                _last = now;
                Debug.Log("[Multiplayer][tac] objectives panel built from CurrentFaction " + now +
                          " (viewer is " + (Context?.View?.ViewerFaction?.TacticalFactionDef?.name ?? "<null>") +
                          ") — shown=0 with a non-empty viewer list is the empty-panel bug.");
            }
        }
    }
}
