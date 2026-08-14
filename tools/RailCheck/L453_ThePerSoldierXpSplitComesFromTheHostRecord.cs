using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L453 — THE PER-SOLDIER POST-MISSION XP IS THE HOST'S RECORD, NOT EACH PEER'S OWN ARITHMETIC.
    ///
    /// THE REPORT (live): the battle summary showed the same mission with one soldier 1-2 XP apart between
    /// peers. L427 already mirrors the POOL (<c>GetActualExperienceReward</c>), and that is not enough:
    /// <c>TacticalFaction.GiveExperienceForObjectives</c>:207-295 SPLITS the pool locally on every peer. It
    /// orders the actors <c>orderby p.Contribution.Contribution descending</c> (:235-238) — LINQ's sort is
    /// stable over <c>Map.GetActors</c>, whose order is peer-dependent, so TIES order differently per peer —
    /// floors each share with <c>Mathf.FloorToInt</c> (:270-271) and then hands the floor remainder out as +1
    /// XP to the FIRST N actors of that order (:291-294). <c>TacticalContribution._contribution</c> is purely
    /// local and rides no rail (<c>AddForTakingDamage</c>:881 clamps to LOCAL health), so the inputs diverge
    /// too. The summary reads local <c>LevelProgression.Experience/ExperienceEarned/ExperienceReference</c>
    /// (<c>UIModuleBattleSummary</c>:284/294/339), the last two of which are not <c>[SerializeMember]</c>.
    ///
    /// SO THE OUTPUT IS MIRRORED, NOT THE INPUTS. Contribution stays unsynced — it feeds nothing else but the
    /// split and <c>MinContributionObjective</c>. The host's own award has already run when its board goes out
    /// (<c>HostBroadcastEnd</c> is a <c>GameOver</c> POSTFIX), so it ships the numbers it literally paid.
    ///
    /// AND IT IS APPLIED AFTER THE CLIENT'S OWN AWARD, NOT INSTEAD OF IT: the correction lands ahead of
    /// <c>OpEnd</c>, i.e. BEFORE the client runs its own <c>GiveExperienceForObjectives</c>, so stamping it in
    /// <c>ApplyOutcome</c> would simply be added on top of. A POSTFIX on that same method is the only seam that
    /// is both after the client's own arithmetic and before <c>UIStateBattleSummary</c> is built. It suppresses
    /// nothing — L427(d) still forbids a Prefix/Transpiler there.
    ///
    /// NOT A QUORUM: host→all, one shot, applied on arrival; a peer that never gets it keeps its own numbers.
    ///
    /// Falsify (verified RED, then restored): drop the trailing per-actor block from <c>HostBroadcast</c> → (a).
    /// </summary>
    internal static class L453_ThePerSoldierXpSplitComesFromTheHostRecord
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var outcome = typeof(TacticalObjectiveOutcome);
            var broadcast = outcome.GetMethod("HostBroadcast", All);
            var apply = outcome.GetMethod("ApplyOutcome", All);
            var reset = outcome.GetMethod("Reset", All);
            var stash = outcome.GetField("Earned", All);
            if (broadcast == null || apply == null || reset == null || stash == null)
            {
                yield return "L453 premise-changed: TacticalObjectiveOutcome.HostBroadcast/ApplyOutcome/Reset or " +
                             "the Earned stash no longer resolves. Without them the per-soldier split is each " +
                             "peer's own floor-and-remainder arithmetic over an unsynced Contribution, and the " +
                             "battle summary shows different XP for the same soldier on two peers.";
                yield break;
            }

            // ── (a) THE HOST SHIPS THE SPLIT IT PAID ────────────────────────────────────────────────
            // Through the CLOSURE too: the payload is written inside a `w => { ... }` lambda, whose IL lives on
            // a compiler-generated nested type, not on HostBroadcast itself. And with NO assembly filter —
            // ExperienceEarned and AddExperience are the GAME's, which is the whole point of reading them.
            var sent = DeepCallees(broadcast, outcome).ToList();
            if (!sent.Any(c => c.Name == "get_ExperienceEarned"))
                yield return "L453 split-not-sent: HostBroadcast never reads LevelProgression.ExperienceEarned, so " +
                             "the mission-end board carries the POOL only. The pool is not the split: " +
                             "GiveExperienceForObjectives:291-294 hands the floor remainder to the first N actors " +
                             "of a Contribution order that ties differently on every peer, and one soldier ends up " +
                             "1-2 XP apart in the summary table.";
            if (!sent.Any(c => c.Name == "Of" && c.DeclaringType == typeof(TacticalActorKey)))
                yield return "L453 split-not-addressed: HostBroadcast does not key the per-soldier block by " +
                             "TacticalActorKey.Of. Any other address — a list index, a name — is a per-peer " +
                             "ordering, which is the exact defect being corrected.";

            // ── (b) THE CLIENT APPLIES IT AFTER ITS OWN AWARD, AS A POSTFIX ──────────────────────────
            var seam = outcome.GetNestedTypes(All).FirstOrDefault(t => t
                .GetCustomAttributes(typeof(HarmonyPatch), false).OfType<HarmonyPatch>()
                .Any(a => a.info?.methodName == "GiveExperienceForObjectives"));
            if (seam == null)
                yield return "L453 split-not-applied: nothing in TacticalObjectiveOutcome postfixes " +
                             "TacticalFaction.GiveExperienceForObjectives. ApplyOutcome cannot do this job — the " +
                             "board lands AHEAD of OpEnd, so the client's own award runs afterwards and adds on " +
                             "top of anything stamped there.";
            else
            {
                if (seam.GetMethod("Postfix", All) == null)
                    yield return "L453 seam-is-not-a-postfix: " + seam.Name + " patches " +
                                 "GiveExperienceForObjectives without a Postfix. Only a postfix is both after the " +
                                 "client's own arithmetic and before UIStateBattleSummary is built; a prefix would " +
                                 "also break L427(d), which forbids suppressing the client's own computation.";
                else if (!Program.CalleeSequence(seam.GetMethod("Postfix", All))
                             .Any(c => c.Name == "AddExperience"))
                    yield return "L453 seam-awards-nothing: the GiveExperienceForObjectives postfix never calls " +
                                 "LevelProgression.AddExperience, so the host's per-soldier record is read and " +
                                 "thrown away and the summary still paints this peer's own split.";
            }

            // ── (c) DROPPED PER BATTLE ──────────────────────────────────────────────────────────────
            if (!Program.ReadsField(reset, stash))
                yield return "L453 stash-outlives-the-battle: TacticalObjectiveOutcome.Reset does not clear the " +
                             "per-soldier stash. Actor keys are rebuilt per battle, so a held entry pays the last " +
                             "mission's XP to whichever soldier inherits the key in this one.";

            // ── (d) OLD-FORMAT PAYLOADS STILL PARSE — THE POSITIVE CONTROL ──────────────────────────
            var read = Program.CalleeSequence(apply).ToList();
            if (!Program.ReadsField(apply, stash))
                yield return "L453 split-not-received: ApplyOutcome never touches the per-soldier stash, so the " +
                             "block the host ships is decoded into nothing and the client keeps its own split.";
            else if (!read.Any(c => c.Name == "get_BaseStream"))
                yield return "L453 no-back-compat-guard: ApplyOutcome reads the trailing per-soldier block without " +
                             "first checking that bytes remain. A peer running an older build sends the board " +
                             "without it, and an unguarded read throws inside the one message that tells this peer " +
                             "the mission ended at all.";
        }

        /// <summary>Callees of <paramref name="m"/> PLUS those of every compiler-generated nested type of
        /// <paramref name="owner"/> — the lambda bodies. No assembly filter: the facts asserted here are calls
        /// into the GAME.</summary>
        private static IEnumerable<MethodBase> DeepCallees(MethodBase m, System.Type owner)
        {
            foreach (var c in Program.CalleeSequence(m)) yield return c;
            foreach (var nested in owner.GetNestedTypes(All))
            {
                if (nested.Name.IndexOf('<') < 0) continue;
                foreach (var nm in nested.GetMethods(All))
                    foreach (var c in Program.CalleeSequence(nm)) yield return c;
            }
        }
    }
}
