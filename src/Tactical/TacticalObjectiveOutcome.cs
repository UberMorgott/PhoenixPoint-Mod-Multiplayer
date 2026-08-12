using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Multiplayer.Network.MessageLayer;
using PhoenixPoint.Common.Levels.Missions;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.Levels.FactionObjectives;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// THE LAST BOARD IS SCORED FROM THE HOST'S OBJECTIVES, not each peer's own recount.
    ///
    /// <c>FactionObjective.State</c> is NOT serialized (<c>FactionObjective</c>:70 — a private setter, no
    /// <c>[SerializeMember]</c>); every peer RECOMPUTES it from local actor state through
    /// <c>ObjectivesManager.Evaluate</c>:74-95, driven by <c>GameOverCondition.EvaluateObjectives</c>. That
    /// is CORRECT and stays: the evaluators are pure functions of state the rails already replicate, and
    /// <see cref="TacticalObjectiveSync"/> carries the one input that is not (the per-peer TRIGGER bool).
    ///
    /// WHAT IS LEFT IS THE MISSION THAT ENDS ON THE TURN ITS OBJECTIVE COMPLETES. The turn-edge sweep
    /// (<c>TacticalTurnSync.HostSweepTick</c>) is what normally makes the two boards agree, and a battle that
    /// ends inside a turn never reaches another edge. Live 2026-08-08 — 3 evacuated on the host, 2 on the
    /// client. Live escort/convince — the host got the objective, the reward and the XP; the client got
    /// none of it and its summary numbers differed.
    ///
    /// SO THIS IS A CORRECTION, NOT A SOURCE OF TRUTH. One host→all snapshot on the ordered 0x80 stream,
    /// AHEAD of <c>OpEnd</c>, applied before the client runs its own native <c>GameOver()</c>. A peer that
    /// already agrees applies a no-op and says nothing; a peer that never receives it still runs its own
    /// native game-over and finishes the mission alone. Nothing waits on anybody (no quorum: no peer's
    /// progress depends on another peer ACTING).
    ///
    /// NOTHING IS SUPPRESSED on the client — not <c>GameOverCondition.EvaluateObjectives</c>, not
    /// <c>GiveExperienceForObjectives</c>. <c>GameOverCondition</c> also assigns <c>TacFactionState</c>,
    /// which gates <c>RemoveMindControl</c>, <c>TacticalTelemetry</c> and <c>ModManager.OnTacticalEnd</c>
    /// (<c>TacticalLevelController.GameOver</c>:826-845); silencing it would diverge other mods' hooks and
    /// create a second truth that can disagree with the visible board. The stamp SURVIVES the client's own
    /// re-evaluation by the game's own rule: <c>Evaluate</c>:86-89 returns early for any objective that is
    /// no longer <c>InProgress</c> unless it declares <c>CanRegress</c>.
    ///
    /// AND THE REWARD IS MIRRORED TOO, because the state alone does not fix the numbers. Six subclasses
    /// override <c>GetCompletion()</c> with a LIVE RATIO — <c>RescueSoldiersFactionObjective</c>:92-99
    /// (rescued/candidates), <c>RecruitSoldiers</c>:112-118, <c>ProtectActor</c>:63-69, <c>PickItems</c>,
    /// <c>DestroyKeyStructures</c>, <c>ProtectKeyStructures</c> — over counters that only
    /// <c>EvaluateObjective</c> refreshes, and the stamp is exactly what stops it running again. A partly
    /// achieved escort would keep the client's own count forever. The mirror is applied at the ONE
    /// non-virtual funnel every consumer already goes through — <c>GetActualExperienceReward</c>:132-135 and
    /// <c>GetActualSkillPointsReward</c>:137-140 — which covers all three readers in the game:
    /// <c>TacticalFaction.GiveExperienceForObjectives</c>:220 (the XP/SP), <c>ObjectiveResultElement</c>:22
    /// (the battle summary line) and <c>ObjectiveResult</c>:18 (the geoscape mission result). Patching
    /// <c>GetCompletion</c> instead would mean patching seven separate override bodies for the same effect.
    ///
    /// THE ADDRESS IS THE OBJECTIVE'S CONTENT, never its index (the lesson of <c>TacticalActorKey</c>):
    /// <c>ObjectivesManager.Evaluate</c>:87-88 APPENDS <c>NextOnSuccess</c> on the peer where the change
    /// happened, so the two lists are exactly the wrong length whenever this correction is needed at all.
    ///
    /// REACTIVITY (postulate 1): this lands microseconds before the client's own native game-over tears the
    /// tactical UI down, so the screen that shows it is the NATIVE battle summary built afterwards from
    /// these very values (<c>ObjectiveResultElement</c>:22). No open panel needs repainting — the panel does
    /// not exist yet when the correction arrives, and the top-left objective list repaints through
    /// <see cref="TacticalObjectiveSync"/>'s own 0x84 seam during the battle.
    /// </summary>
    internal static class TacticalObjectiveOutcome
    {
        /// <summary>op 6 on <see cref="Multiplayer.Network.Sync.SurfaceIds.TacTurn"/> — 1/2/3/5 are
        /// turn/end/leave/restart and 4 is the ready tally.</summary>
        internal const byte OpOutcome = 6;

        /// <summary>CLIENT: the host's reward for an objective this peer holds. Reference-keyed and
        /// per-battle; <see cref="Reset"/> drops it, so a stale entry can never pay out in the next
        /// mission.</summary>
        private static readonly Dictionary<FactionObjective, int> Exp = new Dictionary<FactionObjective, int>();
        private static readonly Dictionary<FactionObjective, int> Sp = new Dictionary<FactionObjective, int>();

        internal static void Reset()
        {
            Exp.Clear();
            Sp.Clear();
        }

        /// <summary>Content identity: the concrete evaluator plus the two localization keys the def gave it.
        /// Stable across peers because every peer builds its objectives from the same defs
        /// (<c>TacMissionTypeDef</c>:174-180, <c>FactionObjectiveDef.Process</c>:85-94), and — unlike a list
        /// index — unmoved by a <c>NextOnSuccess</c> that only one peer has appended.</summary>
        private static string KeyOf(FactionObjective o) =>
            o.GetType().Name + "|" + (o.Description?.LocalizationKey ?? "") + "|" + (o.Summary?.LocalizationKey ?? "");

        private static TacticalFaction Player(TacticalLevelController tlc) =>
            tlc?.Factions?.FirstOrDefault(f => f.ParticipantKind == TacMissionParticipant.Player);

        /// <summary>HOST: the board as the host just scored it. Called from <c>HostBroadcastEnd</c>, which is
        /// a POSTFIX on <c>TacticalLevelController.GameOver</c> — so <c>GiveExperienceForObjectives</c> has
        /// already run here and these are literally the numbers the host paid out.</summary>
        internal static void HostBroadcast(TacticalFaction player)
        {
            if (player == null) return;
            var list = player.Objectives?.ToList();
            if (list == null || list.Count == 0) return;
            TacticalTurnSync.Send(Multiplayer.Network.Sync.SurfaceIds.TacTurn, OpOutcome,
                "mission-end OBJECTIVE BOARD (" + list.Count + ")",
                w =>
                {
                    w.Write(list.Count);
                    foreach (var o in list)
                    {
                        w.Write(KeyOf(o));
                        w.Write((byte)o.State);
                        w.Write(o.GetActualExperienceReward());
                        w.Write(o.GetActualSkillPointsReward());
                    }
                });
        }

        /// <summary>CLIENT: correct what disagrees, touch nothing that does not.</summary>
        internal static void ApplyOutcome(BinaryReader r)
        {
            int count = r.ReadInt32();
            var tlc = TacticalDamageSync.Tlc();
            var player = Player(tlc);
            // Read the whole body regardless of what we can do with it: this rides a shared seq stream and a
            // half-consumed message desynchronises nothing here, but leaving bytes unread is how a future op
            // on this surface would break.
            var mine = player?.Objectives?.ToList() ?? new List<FactionObjective>();
            var used = new HashSet<FactionObjective>();
            int corrected = 0, missing = 0;
            for (int i = 0; i < count; i++)
            {
                string key = WireString.ReadKey(r);
                var state = (FactionObjectiveState)r.ReadByte();
                int exp = r.ReadInt32();
                int sp = r.ReadInt32();
                // Duplicate keys (two objectives off the same def) match positionally AMONG THEMSELVES, in
                // the order both peers built them — the only ordering that is still shared once one peer has
                // appended a NextOnSuccess the other has not.
                var o = mine.FirstOrDefault(m => !used.Contains(m) && KeyOf(m) == key);
                if (o == null) { missing++; continue; }
                used.Add(o);
                bool changed = o.State != state;
                if (changed) SetState(o, state);
                if (exp != o.GetActualExperienceReward() || sp != o.GetActualSkillPointsReward())
                {
                    Exp[o] = exp;
                    Sp[o] = sp;
                    changed = true;
                }
                if (changed)
                {
                    corrected++;
                    Debug.Log("[Multiplayer][tac] mission-end objective CORRECTED from the host: " + key +
                              " state=" + state + " exp=" + exp + " sp=" + sp + ".");
                }
            }
            if (player == null)
                Debug.LogError("[Multiplayer][tac] the host's mission-end objective board arrived with no " +
                               "Player-participant faction on this peer — this peer will score its own board " +
                               "and its XP and summary may differ from the host's.");
            else if (missing > 0)
                Debug.LogWarning("[Multiplayer][tac] " + missing + " of the host's " + count + " mission-end " +
                                 "objectives do not exist on this peer (a NextOnSuccess chain the host " +
                                 "advanced and this peer did not) — their reward cannot be paid here.");
            if (corrected > 0)
                Debug.Log("[Multiplayer][tac] mission-end objective board: " + corrected + " of " + count +
                          " corrected to the host's before this peer's own GameOver runs.");
        }

        /// <summary><c>State</c> is an auto-property with a private setter (<c>FactionObjective</c>:70) and no
        /// public writer exists — the game only ever reaches it through <c>Evaluate</c>, which is precisely
        /// the local recount being corrected.</summary>
        private static void SetState(FactionObjective o, FactionObjectiveState state)
        {
            var setter = AccessTools.PropertySetter(typeof(FactionObjective), nameof(FactionObjective.State));
            if (setter == null)
            {
                Debug.LogError("[Multiplayer][tac] FactionObjective.State has no setter to reflect on — this " +
                               "peer's objectives stay as it counted them and its mission XP will differ.");
                return;
            }
            setter.Invoke(o, new object[] { state });
        }

        [HarmonyPatch(typeof(FactionObjective), nameof(FactionObjective.GetActualExperienceReward))]
        internal static class ExperienceSeam
        {
            private static void Postfix(FactionObjective __instance, ref int __result)
            {
                int v;
                if (__instance != null && Exp.TryGetValue(__instance, out v)) __result = v;
            }
        }

        [HarmonyPatch(typeof(FactionObjective), nameof(FactionObjective.GetActualSkillPointsReward))]
        internal static class SkillPointsSeam
        {
            private static void Postfix(FactionObjective __instance, ref int __result)
            {
                int v;
                if (__instance != null && Sp.TryGetValue(__instance, out v)) __result = v;
            }
        }
    }
}
