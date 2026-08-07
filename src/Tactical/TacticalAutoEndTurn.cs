using System;
using System.Linq;
using HarmonyLib;
using Multiplayer.Network;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.Prompts.TacticalPromptActions;
using PhoenixPoint.Tactical.View;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// THE AUTO-END-TURN PROMPT IS A GLOBAL DECISION TAKEN ON ONE PEER'S PREMISE — so the premise has to
    /// be true for the WHOLE shared squad, and it has to still be true when the human answers.
    ///
    /// THE REPORT (3-peer session, 2026-08-07). The host got the game's "everyone is out of action points,
    /// end the turn?" box while his ally still had AP on two soldiers and a vehicle. He pressed OK and the
    /// ALIEN TURN STARTED. Nobody pressed End Turn: the box did it.
    ///
    /// WHAT THE BOX ACTUALLY IS. <c>TacticalView.OnAbilityExecuted</c>:575-577 asks
    /// <c>ViewerFaction.ShouldAutoEndTurn()</c> after EVERY non-idle ability of the viewer faction, and
    /// answering Yes runs <c>EndTurnPromptActionDef.PromptCallback</c>:13 → <c>ViewerFaction
    /// .RequestEndTurn()</c> — the SAME native call the End Turn button makes, on the ONE faction all
    /// co-op peers share. So a single misjudged prompt ends the round for every player at once. That is
    /// not a quorum question and no wait fixes it: a deliberate End Turn press must still take everyone
    /// (CLAUDE.md's prime rule, laws L84/L91). What must never happen is the game ASKING on a premise that
    /// is false.
    ///
    /// WHY THE PREMISE IS FALSE HERE, EXACTLY. <c>TacticalFaction.ShouldAutoEndTurn</c>:521 is
    /// <c>TacticalActors.All(a =&gt; !a.IsActive || a.IsDisabled || a.IsOnStandBy || a.HasEndedTurn)</c> —
    /// it never looks at action points at all. Its whole notion of "this soldier is finished" is
    /// <c>HasEndedTurn</c>, and that is <c>TacticalActor</c>:198 <c>HasAbilityTrait("terminal")</c>: a
    /// LIST OF STRINGS mutated as a side effect of replaying an ability
    /// (<c>TacticalAbility.ApplyAbilityTraits</c>:915-919). It is the single most divergent per-actor turn
    /// bit this repo has — <see cref="TacticalCommandSync"/> ships a whole trait set on every settle and
    /// logs a reconcile each time the two peers disagreed, dozens of times per battle in the 2026-08-07
    /// client log ("ability traits of KS_Buggy_3 reconciled … [attack,shoot,ability] -&gt; [start]"). A
    /// peer whose trait list momentarily says "terminal" for soldiers the OWNING peer is still playing
    /// folds them all away and the box opens.
    ///
    /// THE GATE, one predicate, both ends of the question:
    ///  • RAISE — postfix on <c>ShouldAutoEndTurn</c>. In a live session, a faction holding an actor that
    ///    can still ACT is never "done", whatever the trait list says. "Can still act" is deliberately
    ///    grounded on ACTION POINTS, not on the trait: AP is a plain number, it rides the settle as a
    ///    number, and both peers agree about it (<see cref="TacticalCommandSync"/> settles ap= on every
    ///    keyed live actor). A soldier really out of AP can do nothing whatever its traits say, so this
    ///    can only ever make the box RARER, never wronger.
    ///  • ANSWER — prefix on <c>EndTurnPromptActionDef.PromptCallback</c>. The box is queued and answered
    ///    by a human seconds later, during which every other peer keeps playing; the premise it was raised
    ///    on is re-checked at the moment it is acted on. This is the arm that catches the reported case,
    ///    where the ally's soldiers were live when OK was pressed.
    ///
    /// THE COST, stated: a squad parked on OVERWATCH has AP left and the "terminal" trait, so the auto
    /// prompt no longer offers itself and the player presses End Turn himself. One click, against a round
    /// that ended under an ally who had not moved.
    ///
    /// NOT A WAIT AND NOT A QUORUM. Nothing here blocks, polls, or reads another peer's membership; it
    /// reads THIS peer's own live faction, the same object the native fold reads, and only ever turns a
    /// "yes" into a "no". The End Turn button, the hotkey and the client end-turn intent
    /// (<c>TacticalEntry.ClientEndTurnGate</c>) are untouched — one player can still send everybody into
    /// the alien turn while the rest are AFK, which is the point.
    /// </summary>
    internal static class TacticalAutoEndTurn
    {
        /// <summary>THE RULE, pure and free of game types so RailCheck L161 can hold it to its truth
        /// table. An actor still has a turn if it is alive, on the field, not disabled, not on standby and
        /// holds action points. Everything else the native fold already counted as done.</summary>
        internal static bool CanStillAct(bool alive, bool active, bool disabled, bool onStandBy, float actionPoints)
            => alive && active && !disabled && !onStandBy && actionPoints > 0f;

        /// <summary>The fold over the SHARED faction — every peer's soldiers, because in co-op they are
        /// all in one <c>TacticalFaction</c>. Never throws: a premise check that can except is a premise
        /// check that stops running.</summary>
        internal static bool AnyoneCanStillAct(TacticalFaction faction)
        {
            if (faction == null) return false;
            try
            {
                return faction.TacticalActors.Any(a => a != null && a.CharacterStats != null &&
                    CanStillAct(a.IsAlive, a.IsActive, a.IsDisabled, a.IsOnStandBy,
                                (float)a.CharacterStats.ActionPoints));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Multiplayer][tac] auto-end-turn premise could not be folded, leaving the " +
                                 "native answer alone: " + ex.Message);
                return false;
            }
        }

        private static bool InSession()
        {
            var engine = NetworkEngine.Instance;
            return engine != null && engine.IsActiveSession;
        }

        /// <summary>RAISE half. Solo play keeps the native answer verbatim.</summary>
        [HarmonyPatch(typeof(TacticalFaction), nameof(TacticalFaction.ShouldAutoEndTurn))]
        internal static class AutoEndTurnPremise
        {
            private static void Postfix(TacticalFaction __instance, ref bool __result)
            {
                if (!__result || !InSession()) return;
                if (!AnyoneCanStillAct(__instance)) return;
                __result = false;
                Debug.Log("[Multiplayer][tac] auto-end-turn prompt WITHHELD — '" +
                          (__instance.TacticalFactionDef == null ? "?" : __instance.TacticalFactionDef.name) +
                          "' still holds an actor with action points. The native fold reads only the " +
                          "'terminal' ability trait, which is the one per-actor turn bit that diverges " +
                          "between peers; ending the round here would end it for every player.");
            }
        }

        /// <summary>ANSWER half. The box was raised at one moment and is clicked at another, with every
        /// other peer playing in between — so the premise is asked again, on the live faction, before a
        /// click turns into <c>RequestEndTurn</c> for the whole session.</summary>
        [HarmonyPatch(typeof(EndTurnPromptActionDef), nameof(EndTurnPromptActionDef.PromptCallback))]
        internal static class AutoEndTurnAnswer
        {
            private static bool Prefix(TacticalViewContext context)
            {
                if (!InSession()) return true;
                var faction = context == null || context.View == null ? null : context.View.ViewerFaction;
                if (!AnyoneCanStillAct(faction)) return true;
                // Never silent: a box that does nothing when clicked is this repo's dominant bug class, and
                // the player is owed the reason his OK did not end the round.
                Debug.LogWarning("[Multiplayer][tac] auto-end-turn prompt ANSWERED but NOT acted on — by the time " +
                                 "OK was pressed the shared squad held an actor with action points again. The " +
                                 "prompt asked a question that had stopped being true; ending the turn on it " +
                                 "would have started the alien turn under a player who was still moving. The " +
                                 "End Turn button still ends the round for everyone whenever anyone presses it.");
                return false;
            }
        }
    }
}
