using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Entities.Abilities;

namespace RailCheck
{
    /// <summary>
    /// L168 — A PLAYER WHO RE-SELECTS A SOLDIER ANOTHER PEER IS DRIVING CANNOT RESTART THE MOVE-RANGE SWEEP.
    ///
    /// THE GAP WAS THE WORD "ONCE". L139 closed the ACTIVATION instant:
    /// <c>TacticalCommandSync.ReleaseLocalUiHolding</c> takes this peer's view off the actor at the moment a
    /// mirrored order lands, gated on <c>view.SelectedActor</c> being that actor AT THAT INSTANT
    /// (<c>LocalUiMustRelease</c>). It is a one-shot, and a mirrored order lasts seconds. A player who clicks
    /// the busy soldier again a moment later re-enters <c>UIStateCharacterSelected</c>, whose
    /// <c>ValidMoves</c>:153-160 reads <c>ActorMoveAbility.GetTargetsData()</c> EVERY FRAME it draws
    /// (:142-148, :1594-1597), and nothing releases him a second time. The owner's log carries 470 of the
    /// engine's own <c>GetTargetsData() shouldn't be called while … is executing abilities</c> across four and a
    /// half minutes on TWO host-owned actors — a per-frame storm, not one line per order.
    ///
    /// THE HARM IS NOT THE LOG LINE. <c>MoveAbility.GetTargetsData</c>:170-173 logs and then carries on with NO
    /// early return, into <c>GetTargetsDataInRange</c>:207-236 → <c>MoveAbilityTargets.CacheTargets</c>:17-28,
    /// which <c>Clear()</c>s and <c>Calculate()</c>s a path request WHILE that actor's navigation is live and
    /// assigns the STATIC <c>NavigationSettings.Defaults</c> singleton (<c>TacticalPathRequest</c>:34-38;
    /// <c>NavigationSettings</c>:46 declares it <c>static readonly</c>), switching
    /// <c>PathRequestPostProcess</c> off for the whole level — read at <c>TacticalPathRequest</c>:64, where it
    /// clears <c>PointInfos</c> and returns. The measured consequence is the mirrored soldier who covered 0.14
    /// of 1.41 units in 403 frames and had to be force-settled at the 10 s ceiling.
    ///
    /// WHY THE SWEEP AND NOT THE SELECTION. Yanking the selection back every time the player clicks a busy
    /// teammate fights the state stack from inside a transition and punishes him for looking; withholding the
    /// SWEEP costs him only that soldier's move overlay, which is the honest signal — L146 is going to refuse
    /// the move anyway. It is also one seam for EVERY caller of <c>GetTargetsData</c> instead of the one UI
    /// state that happened to be reported, and the game itself already answers <c>null</c> from
    /// <c>ValidMoves</c> whenever the ability is not enabled, so an empty sweep is a value those callers were
    /// always written to receive. Arm (d) holds the empty answer to being non-null, because
    /// <c>ValidMoves</c> calls <c>.ToList()</c> on it.
    ///
    /// ARM (a) IS THE STANDING PROPERTY, EXECUTED AS A CONTRAST. It runs
    /// <see cref="TacticalCommandSync.LocalUiStillHoldsAfterMirror"/> in the state the bug reported — the
    /// one-shot has been and gone, the player has re-selected, so the view IS holding him again — and requires
    /// <see cref="TacticalCommandSync.MovePollMustBeWithheld"/> to STILL answer "withhold" in exactly that
    /// state. A gate that only answered while the one-shot's precondition held would fail here.
    ///
    /// POSITIVE CONTROL, arm (c): the sweep still runs for THIS peer's own busy soldier and in a solo game.
    /// Without it the cheap wrong fix — withhold whenever the actor is executing anything — passes every other
    /// arm while deleting the move overlay for the player who is actually driving.
    ///
    /// Falsified 2026-08-07, both ways on every arm. Make <c>MovePollMustBeWithheld</c> return true
    /// unconditionally → <c>L168 own-order-loses-its-move-range</c>, <c>L168 solo-game-changed</c>,
    /// <c>L168 idle-actor-loses-its-move-range</c>; return false unconditionally →
    /// <c>L168 reselection-restarts-the-sweep</c>; drop the <c>drivenByAnotherPeer</c> axis →
    /// <c>L168 own-order-loses-its-move-range</c>; stop calling it from the prefix →
    /// <c>L168 gate-is-decorative</c>; make the prefix a postfix or drop its <c>bool</c> return →
    /// <c>L168 prefix-cannot-withhold</c>; make the withheld answer null → <c>L168 empty-answer-is-null</c>.
    /// </summary>
    internal static class L168_MoveRangeIsNotSweptOnAnActorAnotherPeerDrives
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var cmd = typeof(TacticalCommandSync);
            var decision = cmd.GetMethod("MovePollMustBeWithheld", All);
            var stillHolds = cmd.GetMethod("LocalUiStillHoldsAfterMirror", All);
            var patch = cmd.Assembly.GetType(
                "Multiplayer.Tactical.MoveRangeIsNotSweptWhileAnotherPeerDrivesTheActor");
            var prefix = patch == null ? null : patch.GetMethod("Prefix", All);
            if (decision == null || stillHolds == null || patch == null || prefix == null)
            {
                yield return "L168 premise-changed: TacticalCommandSync.MovePollMustBeWithheld / " +
                             "LocalUiStillHoldsAfterMirror / MoveRangeIsNotSweptWhileAnotherPeerDrivesTheActor." +
                             "Prefix no longer resolves. Nothing else stops a re-selected mirrored soldier " +
                             "restarting the move-range sweep once a frame — re-read this law before assuming " +
                             "the one-shot release covers it.";
                yield break;
            }

            // THE ENGINE SIDE WE COPIED. The gate asks the engine's own question with the engine's own two
            // exceptions; if either signature moved, we are asking a different question than the one the
            // engine calls invalid.
            var native = typeof(MoveAbility).GetMethod("GetTargetsData", All);
            var hasExecuting = typeof(PhoenixPoint.Tactical.Entities.TacticalActorBase)
                               .GetMethod("HasExecutingAbility", All);
            if (native == null || hasExecuting == null ||
                hasExecuting.GetParameters().Length == 0 ||
                hasExecuting.GetParameters()[0].ParameterType != typeof(TacticalAbility[]))
            {
                yield return "L168 premise-changed: MoveAbility.GetTargetsData or " +
                             "TacticalActorBase.HasExecutingAbility(TacticalAbility[], bool) has changed shape " +
                             "in this build of the game. The gate reproduces the engine's own precondition, " +
                             "including the PanicAbility/AIEvaluationAbility exceptions it ignores — a copy " +
                             "whose original moved is our invention with a citation on it.";
                yield break;
            }

            // ── (a) THE STANDING PROPERTY, in the exact state the one-shot has already lost ──
            if (!TacticalCommandSync.LocalUiStillHoldsAfterMirror(true, releaseReached: false))
                yield return "L168 outcome-is-vacuous: the state this law is about — the release did NOT run " +
                             "and this peer's view IS holding the actor — is no longer expressible, so the " +
                             "contrast below proves nothing.";
            else if (!TacticalCommandSync.MovePollMustBeWithheld(true, true, true))
                yield return "L168 reselection-restarts-the-sweep: the one-shot release has been and gone, the " +
                             "player has re-selected a soldier another peer is driving, and the move-range sweep " +
                             "is allowed to run again. That is the reported defect verbatim — 470 engine errors " +
                             "over four and a half minutes, a path request recalculated mid-navigation, and the " +
                             "static NavigationSettings.PathRequestPostProcess turned off under everyone.";

            // ── (b) it must still be withheld however often it is asked ──────
            for (int i = 0; i < 3; i++)
                if (!TacticalCommandSync.MovePollMustBeWithheld(true, true, true))
                {
                    yield return "L168 gate-is-a-one-shot: the same question answered differently on a later " +
                                 "ask. A standing gate has no memory to run out of; if this ever fails, the " +
                                 "decision has grown state and the storm comes back on the second selection.";
                    break;
                }

            // ── (c) POSITIVE CONTROL: what must NOT change ───────────────────
            if (TacticalCommandSync.MovePollMustBeWithheld(true, true, false))
                yield return "L168 own-order-loses-its-move-range: the sweep is withheld for a soldier THIS peer " +
                             "is driving. The move overlay then disappears for the one player entitled to it, " +
                             "which is a worse bug than the one being fixed and is what the cheap version of " +
                             "this gate (withhold whenever the actor is busy) does.";
            if (TacticalCommandSync.MovePollMustBeWithheld(false, true, true))
                yield return "L168 solo-game-changed: the sweep is withheld outside a shared session. A mod that " +
                             "is not in a co-op battle must leave the engine's own behaviour byte for byte.";
            if (TacticalCommandSync.MovePollMustBeWithheld(true, false, true))
                yield return "L168 idle-actor-loses-its-move-range: the sweep is withheld from an actor that is " +
                             "NOT executing anything. The engine's complaint is exactly about executing actors; " +
                             "withholding wider than the engine's own precondition is a behaviour change wearing " +
                             "a bug fix's clothes.";

            // ── (d) the seam: a PREFIX that can withhold, routed through the decision, answering non-null ──
            if (prefix.ReturnType != typeof(bool))
                yield return "L168 prefix-cannot-withhold: the patch method no longer returns bool, so it cannot " +
                             "skip the original. Harmony runs the engine's body regardless and the gate is a " +
                             "log line attached to the storm it was meant to stop.";
            if (!Reaches(prefix, decision, cmd.Assembly))
                yield return "L168 gate-is-decorative: the prefix no longer consults MovePollMustBeWithheld, so " +
                             "every arm above is proved about a decision the live seam does not make. This is " +
                             "the shape this repo keeps paying for (L132, four days green while the gate it " +
                             "named refused every client overwatch).";
            var empty = patch.GetField("Nothing", All);
            var value = empty == null ? null : empty.GetValue(null) as IEnumerable;
            if (value == null || value.GetEnumerator().MoveNext())
                yield return "L168 empty-answer-is-null: the withheld sweep does not answer with an empty, " +
                             "non-null sequence. UIStateCharacterSelected.ValidMoves:156 calls .ToList() on " +
                             "whatever comes back, so null throws inside the game's own property — trading a " +
                             "logged error for an unhandled one.";
        }

        /// <summary>DOES THE SEAM REACH THE DECISION — directly, or through ONE of our own helpers.
        ///
        /// AMENDED 2026-08-08, and this is the amendment L80 taught: arm (d) used to demand the decision be
        /// called from the prefix ITSELF, which is a shape we control, not an outcome. L310 then had to give the
        /// withhold a SECOND caller — the postfix that feeds <c>MoveAbilitySceneViewElement.ValidMoves</c> an
        /// empty list instead of the null the withhold makes it return — and the only way to keep those two
        /// asking ONE question is a shared <c>SweepIsWithheldFor</c>. The old arm turned red on the fix, while
        /// the property it was written to protect (the live seam consults the decision) had never been
        /// stronger. One hop is deliberate and not "any depth": it keeps the arm able to catch a prefix that
        /// wanders off to some unrelated predicate, which is the failure it exists for.</summary>
        private static bool Reaches(MethodBase seam, MethodBase decision, Assembly ours)
        {
            foreach (var direct in Program.Callees(seam, ours))
            {
                if (direct.MetadataToken == decision.MetadataToken) return true;
                if (direct.DeclaringType == null || direct.DeclaringType.Assembly != ours) continue;
                if (Program.Callees(direct, ours).Any(c => c.MetadataToken == decision.MetadataToken))
                    return true;
            }
            return false;
        }
    }
}
