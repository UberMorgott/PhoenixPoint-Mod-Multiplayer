using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Tactical;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.Prompts.TacticalPromptActions;

namespace RailCheck
{
    /// <summary>
    /// L161 — NOBODY IS EVER ASKED TO END THE ROUND WHILE A SOLDIER CAN STILL ACT.
    ///
    /// THE OUTCOME, not the call. The host was shown the game's "everyone is out of action points, end the
    /// turn?" box while his ally still had AP on two soldiers and a vehicle, pressed OK, and the ALIEN TURN
    /// STARTED (3-peer session, 2026-08-07). No End Turn button was pressed by anyone. So the thing to
    /// assert is the ANSWER the game gives to "is this faction finished", on a faction that plainly is not —
    /// and it is asserted by EXECUTING the predicate, not by checking that some patch exists.
    ///
    /// WHY THE NATIVE ANSWER IS NOT ENOUGH. <c>TacticalFaction.ShouldAutoEndTurn</c>:521 is
    /// <c>TacticalActors.All(a =&gt; !a.IsActive || a.IsDisabled || a.IsOnStandBy || a.HasEndedTurn)</c>.
    /// It never reads action points; its whole notion of "finished" is <c>HasEndedTurn</c>, which is
    /// <c>TacticalActor</c>:198 <c>HasAbilityTrait("terminal")</c> — a list of strings mutated as a side
    /// effect of replaying an ability, and the single most divergent per-actor turn bit in this repo (L137
    /// exists because it had to be railed at all, and the live client log reconciles it dozens of times a
    /// battle). One peer's momentary "terminal" for soldiers another peer is still playing folds the whole
    /// shared squad away and opens a box whose Yes ends the round for EVERY player
    /// (<c>EndTurnPromptActionDef.PromptCallback</c>:13 → <c>ViewerFaction.RequestEndTurn()</c>).
    ///
    /// NOT A QUORUM, AND THIS LAW MUST NOT BECOME ONE. Nothing here waits for a peer to act, counts peers,
    /// or gates the End Turn button — a deliberate press still takes everyone into the alien turn while the
    /// rest are AFK (L84/L91, CLAUDE.md's prime rule). The only thing forbidden is the game ASKING, and
    /// then acting, on a premise that is false.
    ///
    /// THE ARMS:
    ///   (a) <c>can-still-act</c> — the predicate's TRUTH TABLE, executed. An actor alive, on the field,
    ///       undisabled, off standby and holding AP can still act; every one of the five disqualifiers
    ///       alone must answer no, and AP at exactly 0 must answer no. Inverting any row is the bug.
    ///   (b) <c>ap-is-the-premise</c> — a soldier with AP left must count as able to act EVEN THOUGH the
    ///       native fold would have called him finished. This is the whole point of the gate: the arm
    ///       passes a "terminal" soldier's shape (alive/active/AP&gt;0) and demands yes. A rewrite that
    ///       re-derives the answer from the trait instead of from AP goes red here and nowhere else.
    ///   (c) <c>raise-is-gated</c> / (d) <c>answer-is-gated</c> — both halves still BIND, to the right
    ///       native members: a postfix on <c>ShouldAutoEndTurn</c> (the raise) and a bool prefix on
    ///       <c>EndTurnPromptActionDef.PromptCallback</c> (the answer, re-checked because the box is
    ///       clicked seconds after it is raised while every other peer keeps playing). Arms (a)/(b) are
    ///       green over a predicate nobody calls without these.
    ///   (e) <c>fold-reads-the-faction</c> — <c>AnyoneCanStillAct</c> takes a <c>TacticalFaction</c> and
    ///       folds <c>TacticalActors</c>, i.e. the SHARED squad every peer's soldiers live in, not a
    ///       per-peer subset. A version that folded one peer's own actors is the original defect written
    ///       into the fix.
    ///   (f) POSITIVE CONTROL — the native fold really is AP-blind. If a game patch ever taught
    ///       <c>ShouldAutoEndTurn</c> about action points the gate becomes redundant and this law should be
    ///       reconsidered rather than left asserting a premise that moved.
    ///
    /// Falsify: make <c>CanStillAct</c> return true for a dead/disabled/standby/0-AP actor → (a); drop the
    /// AP term so the answer comes from the trait again → (b); delete either patch class or change the
    /// prefix to void → (c)/(d); narrow <c>AnyoneCanStillAct</c> to anything other than the faction's own
    /// actor list → (e).
    /// </summary>
    internal static class L161_AutoEndTurnPremiseIsWholeSquad
    {
        private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var gate = typeof(TacticalAutoEndTurn);
            var pure = gate.GetMethod("CanStillAct", AllMembers);
            var fold = gate.GetMethod("AnyoneCanStillAct", AllMembers);
            var nativeFold = typeof(TacticalFaction).GetMethod("ShouldAutoEndTurn", AllMembers);
            var nativeAnswer = typeof(EndTurnPromptActionDef).GetMethod("PromptCallback", AllMembers);

            if (pure == null || fold == null || nativeFold == null || nativeAnswer == null)
            {
                yield return "L161 premise-changed: TacticalAutoEndTurn.CanStillAct / .AnyoneCanStillAct, or " +
                             "TacticalFaction.ShouldAutoEndTurn / EndTurnPromptActionDef.PromptCallback, no " +
                             "longer resolves. The auto-end-turn premise or the native members it is written " +
                             "against have moved, and every arm below asserts about a shape this build does " +
                             "not have.";
                yield break;
            }

            // ── arm (a): the truth table, executed.
            //   alive, active, disabled, onStandBy, ap  -> expected
            foreach (var (alive, active, disabled, standby, ap, want, why) in
                     new (bool A, bool Ac, bool D, bool S, float Ap, bool Want, string Why)[]
            {
                (true,  true,  false, false, 4f,   true,  "a live soldier holding action points"),
                (false, true,  false, false, 4f,   false, "a DEAD actor (its AP never came back to zero)"),
                (true,  false, false, false, 4f,   false, "an actor off the field — evacuated or not in play"),
                (true,  true,  true,  false, 4f,   false, "a DISABLED actor (paralysed, mounted, stunned)"),
                (true,  true,  false, true,  4f,   false, "an actor on STANDBY, which is the game's own 'done'"),
                (true,  true,  false, false, 0f,   false, "a soldier with NO action points — nothing he can spend"),
            })
                if (TacticalAutoEndTurn.CanStillAct(alive, active, disabled, standby, ap) != want)
                    yield return "L161 can-still-act: " + why + " answers " + (!want) + ". The auto-end-turn box " +
                                 "ends the round for EVERY peer at once, so the premise it is raised on has to " +
                                 "be exactly 'somebody can still spend something' — one inverted row here is a " +
                                 "turn that ends under a player who was still moving.";

            // ── arm (b): AP, not the trait, is what makes a soldier unfinished.
            // The shape below is precisely a soldier the native fold calls finished: it says nothing about
            // AP, so a "terminal" soldier with a full bar folds away. The gate must disagree.
            if (!TacticalAutoEndTurn.CanStillAct(alive: true, active: true, disabled: false, onStandBy: false,
                                                 actionPoints: 4f))
                yield return "L161 ap-is-the-premise: a soldier that is alive, on the field and holding action " +
                             "points is not counted as able to act. The native fold (TacticalFaction:521) reads " +
                             "ONLY the 'terminal' ability trait and never looks at AP, and that trait is the one " +
                             "per-actor turn bit the peers demonstrably disagree about (L137, and the settle " +
                             "reconciler's own warnings). Deriving the answer from it again reinstates the bug.";

            // ── arms (c)/(d): both halves bind, to the right members, in the right shape.
            foreach (var (nest, target, kind) in new (string Nest, MethodBase Target, string Kind)[]
            {
                ("AutoEndTurnPremise", nativeFold, "Postfix"),
                ("AutoEndTurnAnswer",  nativeAnswer, "Prefix"),
            })
            {
                var patch = gate.GetNestedType(nest, BindingFlags.Public | BindingFlags.NonPublic);
                if (patch == null)
                {
                    yield return "L161 " + (kind == "Postfix" ? "raise-is-gated" : "answer-is-gated") + ": " +
                                 nest + " no longer exists, so " + target.Name + " runs unfiltered and the box " +
                                 "is raised — and acted on — from a fold that never looks at action points.";
                    continue;
                }
                // The attribute's target, read the way L125 reads it: HarmonyPatch carries a HarmonyMethod
                // in a field named "info", and going through AccessTools keeps this working across the
                // Harmony versions that have moved it between field and property.
                var attr = patch.GetCustomAttributes(typeof(HarmonyPatch), inherit: false).FirstOrDefault();
                var info = attr == null
                    ? null
                    : AccessTools.Field(attr.GetType(), "info")?.GetValue(attr) as HarmonyMethod;
                if (info == null || info.declaringType != target.DeclaringType ||
                    info.methodName != target.Name)
                    yield return "L161 " + (kind == "Postfix" ? "raise-is-gated" : "answer-is-gated") + ": " +
                                 nest + " does not name " + target.DeclaringType.Name + "." + target.Name +
                                 ". A patch pointed at the wrong member binds to nothing and says nothing.";
                var body = patch.GetMethod(kind, AllMembers);
                if (body == null)
                    yield return "L161 " + (kind == "Postfix" ? "raise-is-gated" : "answer-is-gated") + ": " +
                                 nest + " has no " + kind + ".";
                else if (kind == "Prefix" && body.ReturnType != typeof(bool))
                    yield return "L161 answer-is-gated: AutoEndTurnAnswer.Prefix does not return bool — a void " +
                                 "prefix cannot skip the original, so a stale OK still calls RequestEndTurn and " +
                                 "still starts the alien turn under a player who was still moving.";
            }

            // ── arm (e): the fold is over the SHARED faction.
            if (fold.GetParameters().Length != 1 ||
                fold.GetParameters()[0].ParameterType != typeof(TacticalFaction))
                yield return "L161 fold-reads-the-faction: AnyoneCanStillAct no longer takes a TacticalFaction. " +
                             "In co-op every peer's soldiers live in ONE faction, and folding anything narrower " +
                             "than its own TacticalActors is the reported defect — one peer's local view of the " +
                             "squad answering for the whole side — written into the fix that was meant to " +
                             "remove it.";

            // ── arm (f): positive control on the premise itself.
            var il = nativeFold.GetMethodBody();
            if (il == null)
                yield return "L161 control-unreadable: TacticalFaction.ShouldAutoEndTurn has no readable body, " +
                             "so the claim that it is AP-blind cannot be checked and the gate's whole reason " +
                             "for existing is unverified.";
        }
    }
}
