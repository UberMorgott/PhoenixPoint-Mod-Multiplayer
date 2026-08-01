using System;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using Base.UI;
using HarmonyLib;
using Multiplayer.Network;
using Multiplayer.Network.Sync;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Levels;
using PhoenixPoint.Tactical.Prompts;
using PhoenixPoint.Tactical.View;
using UnityEngine;

namespace Multiplayer.Tactical
{
    /// <summary>
    /// LAW 11 FOR THE TACTICAL SCREEN. The geoscape half of reactivity has had a seam since day one
    /// (<c>OpenUiRepaint</c>, <c>GeoscapeViewState</c>-typed); the tactical half had NONE — grep the repo
    /// before this file and no tactical UI is ever repainted by anything. That is the whole defect: peer A
    /// fires a soldier, the AP and the ability availability settle correctly INTO THE MODEL on peer B
    /// (<c>TacticalCommandSync.ApplySettle</c> writes <c>ActionPoints.Set</c> / <c>WillPoints.Set</c>
    /// through the native writers), and peer B's open screen keeps painting the pre-shot numbers with every
    /// ability icon lit. MODEL fresh, VIEW stale — the icons are not a live query, they are baked once by
    /// <c>UIModuleAbilities.SetAbilities</c>:112-143, which asks each ability's <c>GetDisabledState()</c> at
    /// the moment it runs and never again.
    ///
    /// WHY A PEER NEVER REPAINTS ITSELF. On the acting peer the repaint is a side effect of the VIEW-STATE
    /// LIFECYCLE, not of the model: a click runs <c>TacticalViewState.ActivateAbility</c>:259-277, which
    /// pushes <c>UIStateWaiting</c> and comes back, and the returning state's <c>EnterState</c> re-reads
    /// everything (<c>UIStateCharacterSelected.EnterState</c>:379 → <c>SelectCharacter</c>:224-273 →
    /// <c>SetAbilities</c>:247; <c>UIStateShoot.EnterState</c>:392 →
    /// <c>InitState</c>:414-450 → <c>SetAbilities</c>:450). A peer that did not click gets no transition, so
    /// it gets no repaint — for as long as it sits on that screen. Nothing else in the game re-reads AP for
    /// the ability bar: <c>TacticalView.OnAbilityExecuted</c>:542-578 updates only the (debug) AP pool and
    /// the prompt manager.
    ///
    /// THE REPAINT IS THE STATE'S OWN <c>Enter</c>, not a hand-picked module call. Every screen that paints
    /// ability availability rebuilds itself in <c>EnterState</c>, so <c>Exit</c>+<c>Enter</c> on the SAME
    /// state instance is the native path, and it is the same last-resort recipe the geoscape seam uses
    /// (<c>OpenUiRepaint.Repaint</c>). It also fixes the dead-target defect for free and for the same
    /// reason: the corpse's crosshair survives because <c>UIStateShoot._selectedValidShoots</c> is a
    /// CONSTRUCTOR SNAPSHOT (:193) taken before the kill arrived, and re-entering runs
    /// <c>InitAbilityTargetActor</c>:434 → <c>GetDefaultTarget</c>:454-461, whose first statement drops
    /// every dead actor from that very list; on the character screen the same re-enter re-runs
    /// <c>UpdateValidShootTargets</c>/<c>UpdateHealingTargetIcons</c> (:261-264).
    ///
    /// WHICH SCREENS. Only the four states that call <c>UIModuleAbilities.SetAbilities</c> — i.e. exactly
    /// the ones that can show a stale ability bar. This is an ALLOW list rather than the drop list A5
    /// prefers, because here the unknown case is DANGEROUS, not merely unmirrored:
    /// <c>UIStateInventory.ExitState</c>:428-453 is not a teardown at all — it COMMITS the staged batch
    /// (<c>ApplyInventoryActions</c>:443) and calls <c>ActorSpawner.DestroyActor</c> (:451). An
    /// Exit+Enter there would commit a drag the player had not confirmed and destroy containers. Safe by
    /// default wins; a screen missing from this list simply behaves as it does today.
    /// </summary>
    internal static class TacticalUiRepaint
    {
        /// <summary>The four <c>TacticalViewState</c>s whose <c>EnterState</c> reaches
        /// <c>UIModuleAbilities.SetAbilities</c> (UIStateCharacterSelected:247, UIStateShoot:450,
        /// UIStateAbilitySelected:189, UIStateOverwatchAbilitySelected:79). Matched by NAME up the base
        /// chain, not by <c>is</c>: <c>UIStateCharacterSelected</c> is <c>internal</c> to Assembly-CSharp,
        /// and walking the chain lets subclasses ride free (<c>UIStateFreeCam : UIStateShoot</c>).</summary>
        private static readonly HashSet<string> AbilityBarStates = new HashSet<string>
        {
            "UIStateCharacterSelected", "UIStateShoot", "UIStateAbilitySelected", "UIStateOverwatchAbilitySelected"
        };

        private static readonly FieldInfo StateStackField =
            AccessTools.Field(typeof(TacticalViewState), "_stateStack");

        private static bool _dirty;

        // What the last paint actually showed. NOT a cache of the model: the gap between these and the live
        // stats IS the second dirty source (see the Update postfix).
        private static TacticalActor _painted;
        private static float _paintedAp, _paintedWp;

        private static readonly HashSet<string> _loggedFailures = new HashSet<string>();

        /// <summary>Something changed that the open tactical screen may be painting. Coalesced — the flush
        /// is once per frame at most, no matter how many marks land in it.</summary>
        internal static void MarkDirty() => _dirty = true;

        /// <summary>Battle/session teardown (driven from <c>TacticalTurnSync.Reset</c>). A leaked
        /// <c>_painted</c> would compare the next battle's first frame against a dead actor.</summary>
        internal static void Reset()
        {
            _dirty = false;
            _painted = null;
            _loggedFailures.Clear();
        }

        private static NetworkEngine LiveEngine()
        {
            var engine = NetworkEngine.Instance;
            if (engine == null || !engine.IsActiveSession) return null;
            var coord = engine.SaveTransfer;
            return coord != null && coord.SessionStarted ? engine : null;
        }

        private static TacticalLevelController Tlc()
        {
            var level = GameUtl.CurrentLevel();
            return level == null ? null : level.GetComponent<TacticalLevelController>();
        }

        /// <summary>
        /// Driven from the view's OWN frame slot — a postfix on <c>TacticalViewState.Update</c>:46-93, which
        /// the state stack calls every frame for whichever state is current. No tick of ours is involved on
        /// purpose: this must run on the HOST too (a client's order executes natively on the host with no
        /// host-side view transition, so the host's screen goes stale exactly the same way), and the two
        /// tactical <c>ClientTick</c>s are client-only.
        ///
        /// SECOND DIRTY SOURCE, and the reason a settle can never be missed: the selected actor's AP/WP are
        /// compared against what the last paint SHOWED. The settle deliberately lands late — it is held
        /// until that actor stops executing (<c>TacticalCommandSync.ClientTick</c>) — so it can and does
        /// arrive after the ability-executed mark below has already been flushed. Asking the model instead
        /// of ordering the seams makes the correction arrive whatever the order was, and it costs one
        /// reference compare and two float compares per frame with no allocation. A selection CHANGE only
        /// re-baselines: the native path already repainted for that.
        ///
        /// ponytail: the memo watches the SELECTED actor only, so another soldier's AP moving while this
        /// peer looks at a third one repaints one beat late (on the next mark, selection change or state
        /// transition). Widening it means walking <c>TacticalFaction.TacticalActors</c>, which is a LINQ
        /// <c>GetTacActors</c> query (<c>TacticalFaction</c>:70) — a per-frame allocation in the tactical
        /// hot loop. Upgrade path if the squad bar is ever observed stale in play: mark from the stat's own
        /// <c>BaseStat.StatChangeEvent</c>:50 instead of polling.
        /// </summary>
        [HarmonyPatch(typeof(TacticalViewState), nameof(TacticalViewState.Update))]
        internal static class ViewStateUpdatePatch
        {
            private static void Postfix(TacticalViewState __instance)
            {
                if (LiveEngine() == null) { _painted = null; return; }
                var tlc = Tlc();
                var view = tlc == null ? null : tlc.View;
                if (view == null) { _painted = null; return; }

                var actor = view.SelectedActor;
                float ap = 0f, wp = 0f;
                var stats = actor == null ? null : actor.CharacterStats;
                if (stats != null) { ap = stats.ActionPoints; wp = stats.WillPoints; }
                if (actor == _painted && (ap != _paintedAp || wp != _paintedWp)) _dirty = true;
                _painted = actor;
                _paintedAp = ap;
                _paintedWp = wp;

                if (!_dirty) return;
                _dirty = false;
                if (IsAbilityBarState(__instance)) Repaint(__instance);
            }
        }

        /// <summary>
        /// FIRST DIRTY SOURCE: the game's own <c>AbilityExecutedEvent</c> — literally the event the view
        /// already listens to (<c>TacticalView.cs</c>:250). Its single raise site in the assembly is
        /// <c>TacticalAbility</c>:1057, inside <c>ClearPlayingAction</c>, which is the same instant the host
        /// ships the A3a settle — so every mirrored activation, on every peer, marks the screen here without
        /// this file knowing anything about the rail. Deaths ride it too: a die ability is an ability, so
        /// the mirrored death (forced through <c>Health.Set(0)</c> → <c>Die</c>) reaches this line and the
        /// corpse's crosshair is dropped by the re-enter.
        ///
        /// The two AMBIENT abilities are skipped, taken from the game's own ambient set on that same line
        /// (<c>TacticalLevelController</c>:1183 excludes them from its panic sweep for the same reason the
        /// view excludes idle at <c>TacticalView</c>:544): <c>IdleAbility</c> fires per idle and
        /// <c>AIEvaluationAbility</c> per AI DECISION, so an enemy turn raises dozens of them and neither
        /// changes anything a screen paints. The rest of that set (enter-play, panic, hurt-reaction) is
        /// deliberately NOT skipped — those are rare and they do move what is on screen.
        /// </summary>
        [HarmonyPatch(typeof(TacticalLevelController), nameof(TacticalLevelController.AbilityExecuted))]
        internal static class AbilityExecutedPatch
        {
            private static void Postfix(TacticalAbility ability)
            {
                if (!(ability is IdleAbility) && !(ability is AIEvaluationAbility)) MarkDirty();
            }
        }

        /// <summary>
        /// LAW 11, TEARDOWN HALF: a tactical prompt asks a question that is now answered GLOBALLY, so it must
        /// die on every peer the moment the turn really ends — not only on the peer whose click ended it.
        ///
        /// The prompt system is a purely LOCAL decision UI. <c>TacticalView.OnAbilityExecuted</c>:575-577 asks
        /// <c>ViewerFaction.ShouldAutoEndTurn()</c> after EVERY non-idle ability of the viewer faction, and
        /// under A5 every peer executes every mirrored order — so when the squad runs dry the "end turn?"
        /// prompt opens on ALL THREE screens and each further mirrored ability queues ANOTHER copy
        /// (<c>TacticalPromptsManager.ShowPrompt</c>:72-77 tests <c>Contains</c> on a freshly allocated
        /// <c>TacticalPrompt</c>, so the reference compare never matches and the pending list only grows).
        ///
        /// Nothing native tears a SHOWN prompt down. <c>MessageBoxPromptController.Invoke</c>:255-260 does
        /// close the box before the callback, so the peer that clicks Yes is not left holding the box it
        /// clicked — but its manager immediately shows the NEXT queued copy from the very next
        /// <c>Update</c>:21-27 (still a valid state: the local turn has not ended yet, because a client's
        /// <c>RequestEndTurn</c> is converted to an intent and the real end arrives with the host's cursor a
        /// few frames later). Identical box, identical position, mouse still on the button — indistinguishable
        /// from "the window never closed", which is exactly how it was reported. On the peers that did NOT
        /// click, the ORIGINAL box simply survives the turn edge with its <c>InputConsumer</c> active
        /// (<c>MessageBox</c>:162), and answering it during the alien turn would call
        /// <c>ViewerFaction.RequestEndTurn()</c> (<c>EndTurnPromptActionDef</c>:13) and pre-end the squad's
        /// NEXT turn. <c>TacticalView.OnNewTurn</c>:1151 calls <c>PromptsManager.Cleanup</c>, but that only
        /// drops PENDING entries — the open <c>MessageBox</c> and <c>_currentPrompt</c> are untouched.
        ///
        /// The seam is the game's own "the viewer's turn just ended" edge, <c>TacticalView</c>:1140-1146
        /// (<c>FactionEndedTurnEvent</c>, raised on every peer running the native turn machine), and it fires
        /// BEFORE <c>OnNewTurn</c>. <c>ForceCloseAllPrompts</c>:182-192 is the native teardown, but it does
        /// NOT run the callback — so <c>_currentPrompt</c> must be nulled by hand or the manager is wedged for
        /// the rest of the battle and no interact/evac prompt ever shows again.
        /// </summary>
        [HarmonyPatch(typeof(TacticalView), "OnViewerFactionEndedTurn")]
        internal static class PromptTurnEdgeTeardown
        {
            private static readonly FieldInfo CurrentPromptField =
                AccessTools.Field(typeof(TacticalPromptsManager), "_currentPrompt");

            private static void Postfix(TacticalView __instance, TacticalFaction prevFaction)
            {
                if (LiveEngine() == null) return;                       // solo play keeps native behaviour
                if (prevFaction == null || !prevFaction.IsViewerFaction) return;
                var prompts = __instance == null ? null : __instance.PromptsManager;
                if (prompts == null || CurrentPromptField == null) return;

                prompts.ClearPending();   // the co-op pile-up: one queued copy per mirrored order
                if (CurrentPromptField.GetValue(prompts) == null) return;

                GameUtl.GetMessageBox()?.ForceCloseAllPrompts();
                CurrentPromptField.SetValue(prompts, null);
                Debug.Log("[Multiplayer][tac] tactical prompt closed at the turn edge — the turn ended for " +
                          "every peer, so a prompt still asking about it is stale on this one.");
            }
        }

        private static bool IsAbilityBarState(TacticalViewState state)
        {
            for (var t = state.GetType(); t != null && t != typeof(TacticalViewState); t = t.BaseType)
                if (AbilityBarStates.Contains(t.Name)) return true;
            return false;
        }

        /// <summary>Exit+Enter of the SAME state instance. Instance-scoped fields survive it, which is what
        /// keeps this from yanking the camera: <c>UIStateCharacterSelected._isCharacterAlreadyChased</c> is
        /// set at :251 and is never cleared by <c>ExitState</c>, so <c>SelectCharacter</c>:248 skips its
        /// <c>DoCameraChase</c> on every repaint after the first.
        ///
        /// Law 8: a re-enter fires native UI events that our own intent-capture seams listen to — most
        /// concretely <c>SelectCharacter</c>:751 / <c>InitState</c>-time equipment reads against A7's prefix
        /// on <c>EquipmentComponent.SetSelectedEquipment</c>. <c>SyncApplyScope</c> is what stops a repaint
        /// from echoing an order back to the host.
        ///
        /// The state may legitimately NOT be current afterwards, and that is a fix rather than a fault:
        /// <c>UIStateShoot.EnterState</c>:352 leaves aim mode when the re-read leaves no valid shot — which
        /// is exactly the screen-B case, a soldier still aiming with the AP already spent elsewhere.</summary>
        private static void Repaint(TacticalViewState state)
        {
            var stack = StateStackField == null ? null : StateStackField.GetValue(state) as StateStack<TacticalViewContext>;
            if (stack == null) return;
            try
            {
                using (SyncApplyScope.Enter())
                {
                    try { state.Exit(stack); }
                    catch (Exception exitEx)
                    {
                        // Exit removes the input handler BEFORE ExitState (TacticalViewState:115-120), so
                        // bailing out here would leave the screen DEAF. Always fall through to Enter — its
                        // AddUnique re-subscribes idempotently.
                        if (_loggedFailures.Add(state.GetType().Name + ":Exit"))
                            Debug.LogWarning("[Multiplayer][tac] repaint Exit for " + state.GetType().Name +
                                             " threw — entering anyway (logged once per screen): " + exitEx);
                    }
                    state.Enter(stack);
                }
            }
            catch (Exception ex)
            {
                // NON-DESTRUCTIVE, same posture as the geoscape seam: a throw inside EnterState is a PARTIAL
                // repaint, not a lost screen, and law 11 outranks log tidiness — we keep repainting this
                // screen on later marks and stay quiet after the first report.
                if (_loggedFailures.Add(state.GetType().Name))
                    Debug.LogWarning("[Multiplayer][tac] repaint of " + state.GetType().Name +
                                     " threw — screen kept, part of it may be stale (logged once per screen): " + ex);
            }
        }
    }
}
