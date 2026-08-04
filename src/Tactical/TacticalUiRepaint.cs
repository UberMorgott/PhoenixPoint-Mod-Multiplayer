using System;
using System.Collections.Generic;
using System.Reflection;
using Base.Core;
using Base.Entities.Statuses;
using Base.Input;
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
    /// (<c>OpenUiRepaint.Repaint</c>). On the character screen it also fixes the dead-target defect for
    /// free, by re-running <c>UpdateValidShootTargets</c>/<c>UpdateHealingTargetIcons</c> (:261-264). It no
    /// longer does so for the AIM screen: 3d8e308 also listed <c>UIStateShoot</c>, whose
    /// <c>_selectedValidShoots</c> is a constructor snapshot (:193) that a re-enter would refilter — but
    /// that state's <c>EnterState</c> moves the state stack, which a repaint must never do. See
    /// <see cref="TacticalUiRepaint.AbilityBarStates"/>.
    ///
    /// WHICH SCREENS. Only states that call <c>UIModuleAbilities.SetAbilities</c> AND whose
    /// <c>EnterState</c> is a pure re-read. This is an ALLOW list rather than the drop list A5
    /// prefers, because here the unknown case is DANGEROUS, not merely unmirrored:
    /// <c>UIStateInventory.ExitState</c>:428-453 is not a teardown at all — it COMMITS the staged batch
    /// (<c>ApplyInventoryActions</c>:443) and calls <c>ActorSpawner.DestroyActor</c> (:451). An
    /// Exit+Enter there would commit a drag the player had not confirmed and destroy containers. Safe by
    /// default wins; a screen missing from this list simply behaves as it does today.
    /// </summary>
    internal static class TacticalUiRepaint
    {
        /// <summary>The <c>TacticalViewState</c>s whose <c>EnterState</c> reaches
        /// <c>UIModuleAbilities.SetAbilities</c> (UIStateCharacterSelected:247, UIStateAbilitySelected:189,
        /// UIStateOverwatchAbilitySelected:79) AND whose <c>EnterState</c> cannot move the state stack.
        /// Matched by NAME up the base chain, not by <c>is</c>: <c>UIStateCharacterSelected</c> is
        /// <c>internal</c> to Assembly-CSharp, and walking the chain lets subclasses ride free.
        ///
        /// <c>UIStateShoot</c> IS EXCLUDED, and with it <c>UIStateFreeCam : UIStateShoot</c>. Exit+Enter is
        /// only a repaint for a state whose <c>EnterState</c> is a pure re-read; <c>UIStateShoot</c>'s is not
        /// — it TRANSITIONS, twice over: <c>:348</c> <c>EnterFpsCamera()</c> pushes UIStateFreeCam, and
        /// <c>:352</c> calls <c>SwitchToPreviousState()</c> when the re-read leaves no valid shot. 3d8e308
        /// called that second one a feature ("leaves aim mode when the shot is gone"); it is the hazard.
        /// <c>StateStack.SwitchToPreviousState</c>:96-98 pops the state and then runs <c>Exit</c> +
        /// <c>Pop</c> on it — but this repaint has ALREADY run <c>Exit</c> one line earlier, so
        /// <c>UIStateShoot.ExitState</c>:1244 executes TWICE on one instance (its <c>FaceEnemy</c>, its
        /// scene-element teardown, its module unsubscribes) and the stack is left one entry short of what
        /// the repaint believed it had. The observable cost of excluding it: a peer sitting in aim mode
        /// keeps a stale ability bar and a dead target's crosshair until its own next transition. That is a
        /// stale pixel; the alternative is a corrupted state stack, and this repo's own rule is that
        /// playability outweighs a feature.
        ///
        /// ponytail: the exclusion is by NAME, not by a static "does EnterState move the stack" test — no
        /// such test exists at runtime. RailCheck L63 holds the invariant mechanically instead, so a future
        /// re-add of UIStateShoot (or of any state whose EnterState reaches SwitchToState) turns the harness
        /// red rather than shipping the double-exit again.</summary>
        private static readonly HashSet<string> AbilityBarStates = new HashSet<string>
        {
            "UIStateCharacterSelected", "UIStateAbilitySelected", "UIStateOverwatchAbilitySelected"
        };

        private static readonly FieldInfo StateStackField =
            AccessTools.Field(typeof(TacticalViewState), "_stateStack");

        private static bool _dirty;
        private static bool _squadBarDirty;

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
            _squadBarDirty = false;
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

                // The squad bar is flushed BEFORE the popped-state guard below and outside the _dirty gate:
                // it repaints Text fields on pooled row elements and touches no view state at all, so none of
                // the reasons that bail out of an Exit+Enter apply to it.
                if (_squadBarDirty) { _squadBarDirty = false; PaintSquadBar(view); }

                var actor = view.SelectedActor;
                float ap = 0f, wp = 0f;
                var stats = actor == null ? null : actor.CharacterStats;
                if (stats != null) { ap = stats.ActionPoints; wp = stats.WillPoints; }
                if (actor == _painted && (ap != _paintedAp || wp != _paintedWp)) _dirty = true;
                _painted = actor;
                _paintedAp = ap;
                _paintedWp = wp;

                if (!_dirty) return;
                // THE ENGINE'S OWN GUARD, which this postfix omitted (TacticalViewState.Update:53/74 applies
                // it three times inside the very method we run after): confirming an ability TRANSITIONS
                // inside Update (UIStateAbilitySelected.OnSelect → ActivateAbility:259-277 →
                // SwitchToState(UIStateWaiting, ClearStackAndPush) → StateStack.Clear:107-121), so without it
                // the repaint Exit+ENTERs a state that is already exited AND already popped —
                // UIStateAbilitySelected.EnterState:180 re-subscribes AbilityConfirmed on the SHARED
                // UIModuleAbilityConfirmationButton and nothing can ever Exit it again (Clear exits only the
                // TOP, :113-117), so the zombie keeps its _selectedAbility and fires FIRST on every later
                // confirmation (the JetJump that flew instead of shooting, AP charged for the shot).
                var stack = StateStackField == null
                    ? null
                    : StateStackField.GetValue(__instance) as StateStack<TacticalViewContext>;
                if (stack == null || !ReferenceEquals(stack.CurrentState, __instance)) return;  // keep _dirty:
                                                    // the REAL current state's own Update repaints next frame
                _dirty = false;
                if (IsAbilityBarState(__instance)) Repaint(__instance);
                else RepaintModules(view);
            }
        }

        /// <summary>
        /// THE SQUAD BAR — the row of portraits with each soldier's AP/HP/WP under it, and the ONE part of
        /// the tactical screen neither repaint path above can reach.
        ///
        /// Those numbers are written by exactly one method,
        /// <c>SquadMemberScrollerElement.UpdateActorStats</c>:38-78, and its only caller in the assembly is
        /// <c>TacticalView.UpdateSquadMembersActionAndWillPointsImpl</c>:284-295 — a deferred pass ARMED by
        /// the public <c>UpdateSquadMembersActionAndWillPoints</c>:278. Every native arming site is a LOCAL
        /// GESTURE: <c>TacticalViewState.ActivateAbility</c>:271 (this peer clicked an ability),
        /// <c>UIStateCharacterSelected.SelectCharacter</c>:245 (this peer clicked a soldier),
        /// <c>UIStateShoot.InitState</c>:415, <c>UIStateWaiting</c>:42, <c>UIStateInitial</c>:30. A mirrored
        /// order raises NONE of them on a peer that did not click, which is the reported defect verbatim:
        /// the AP under the portraits stays stale until you click one of the soldiers.
        ///
        /// Exit+Enter does not fix it either, and that is not an oversight: the state's own re-read reaches
        /// <c>SetSquad</c>:216 → <c>SquadMemberScrollerController.SetScroller</c>:119 →
        /// <c>InitSquadMemberElement</c>:179-218, which rebinds portrait, class icons, tooltip and
        /// selection — and never touches the AP/HP/WP texts. <c>UIUtil.EnsureActiveComponentsInContainer</c>
        /// REUSES the pooled elements, so after every <c>SetSquad</c> those numbers are literally the
        /// previous paint's until the deferred pass lands two frames later (:281 sets the countdown to 1 and
        /// :286 decrements before testing).
        ///
        /// THE WP FLICKER WAS NEVER THIS, AND THAT CALL WAS WRONG (corrected 2026-08-05). This block used to
        /// blame the client's +2/-2/+2 will-point flicker on the same two-frame paint window, on the premise
        /// that the settle's <c>WillPoints.Set</c> carries the host's number from AFTER the kill grant. It
        /// does not: a settle is captured at <c>ClearPlayingAction</c>, which fires a full second BEFORE that
        /// action's own damage resolves, so the number it carries is the PRE-kill one. The three steps are
        /// three real writes racing, not one value painted three ways — see
        /// <c>TacticalDamageSync.StatsAreStale</c> (law L105), which is where it is fixed. The bar was an
        /// honest mirror of the model throughout; it showed exactly what the model held.
        ///
        /// THE SEAM IS THE STAT ITSELF, not a list of the places a mirror writes one. <c>BaseStat.Set</c>:95
        /// funnels every stat write in the game through <c>OnStatChange</c>:111, so one postfix covers AP, WP,
        /// health and corruption, for every soldier, on host and client alike, whether the change came off the
        /// rail or out of native play — no per-syncer call-outs, and no polling (the AP/WP memo above watches
        /// the SELECTED actor only, which is why another soldier's AP never marked anything).
        ///
        /// IT DOES NOT ARM THE GAME'S OWN PASS, and that is the correction to the first version of this patch
        /// (2026-08-04). <c>UpdateSquadMembersActionAndWillPoints</c>:278-281 is not a request, it is a
        /// RESETTABLE 2-frame countdown — it sets <c>_updateSquadMembersWillAndActionPointsIn = 1</c> and
        /// <c>Impl</c>:286 only paints once <c>_in-- &lt;= 0</c>, so the pass needs two consecutive
        /// <c>TacticalView.Update</c>s with NO further arming in between. Natively that always holds, because
        /// every native arming site is a discrete human gesture. Off <c>BaseStat.OnStatChange</c> it does not:
        /// the stat stream is every stat of every actor, and while it runs at one write per frame or better
        /// the countdown is re-armed before it can expire and the bar never repaints at all — it simply holds
        /// the last paint, which at the top of a turn is every soldier at FULL AP. That starvation is worst on
        /// the HOST, which alone runs the real damage, status and reaction math (a client's is neutered at
        /// <c>DamageAccumulation.ApplyAddedDamage</c>:550), and "the host shows full AP for everyone while the
        /// clients agree with each other" is exactly how it was reported.
        ///
        /// So the flag is ours and the PAINT is the game's: <see cref="PaintSquadBar"/> is
        /// <c>Impl</c>:288-292 verbatim minus the countdown. Coalesced to at most one pass per frame by the
        /// <see cref="ViewStateUpdatePatch"/> flush, which is what the countdown was there for in the first
        /// place, so nothing is paid twice.
        ///
        /// Deliberately NOT <see cref="MarkDirty"/>: a stat change must not Exit+Enter a view state (law
        /// L63's whole point), and this needs no repaint of anything else. The postfix body is a single store
        /// on purpose — it sits on EVERY stat write in the game, including solo play, where the flush end
        /// bails on <see cref="LiveEngine"/> and the flag is simply never read.
        /// </summary>
        [HarmonyPatch(typeof(BaseStat), "OnStatChange")]
        internal static class SquadBarStatPatch
        {
            private static void Postfix(StatChangeType change)
            {
                if (change == StatChangeType.Value) _squadBarDirty = true;  // Max/Min/MinimumDelta paint nothing here
            }
        }

        /// <summary><c>TacticalView.UpdateSquadMembersActionAndWillPointsImpl</c>:288-292 with its countdown
        /// removed — the same <c>GetComponentsInChildren</c> over the pooled row elements and the same
        /// <c>UpdateActorStats</c> call, and the same <c>activeSelf</c> gate (:286) so a hidden bar costs one
        /// bool. Rows are pooled and <c>UpdateActorStats</c>:40 returns on a null <c>Actor</c>, so an
        /// over-count row is a no-op rather than a throw.</summary>
        private static void PaintSquadBar(TacticalView view)
        {
            var modules = view.TacticalModules;
            var squad = modules == null ? null : modules.SquadManagementModule;
            if (squad == null || !squad.gameObject.activeSelf || squad.SquadMemberScroller == null) return;
            try
            {
                var rows = squad.SquadMemberScroller
                    .GetComponentsInChildren<PhoenixPoint.Tactical.View.ViewControllers.SquadMemberScrollerElement>();
                for (int i = 0; i < rows.Length; i++) rows[i].UpdateActorStats();
            }
            catch (Exception ex)
            {
                if (_loggedFailures.Add("squadbar"))
                    Debug.LogWarning("[Multiplayer][tac] squad-bar repaint threw — the AP/WP under the " +
                                     "portraits may be stale (logged once per battle): " + ex);
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

        /// <summary>
        /// THE OTHER HALF OF LAW 11 FOR THE TACTICAL SCREEN — the states Exit+Enter may NOT be used on.
        /// <see cref="AbilityBarStates"/> excludes every state whose <c>EnterState</c> moves the state stack
        /// (<c>UIStateShoot</c> above all), and the cost was stated plainly there: a peer sitting in aim mode
        /// keeps a stale ability bar until its own next transition. Reported live as a bug on 2026-08-04 —
        /// two peers held the same sniper in aim mode, one fired, and the other's bottom bar kept the
        /// pre-shot AP with every ability still lit. Model fresh, view stale: this repo's dominant shape.
        ///
        /// A stale bar does not need the state re-entered, only the two MODULES that paint it re-read. Both
        /// are public, both take the actor and nothing else, and both are exactly what the excluded state's
        /// own init calls: <c>UIModuleAbilities.SetAbilities(TacticalActor, InputController)</c>:111-143
        /// (<c>UIStateShoot.InitSpecificUI</c>:449, and the same call every allow-listed state makes through
        /// its <c>EnterState</c>) re-asks every ability's <c>GetDisabledState()</c>, and
        /// <c>UIModuleActionBar.SetActionBar(TacticalActor)</c>:238 re-reads the AP bar off the live actor.
        /// Neither touches the state stack, so the double-<c>ExitState</c> hazard (law L63) is structurally
        /// out of reach — this path never calls <c>Exit</c> or <c>Enter</c> at all.
        ///
        /// GENERIC, NOT A SECOND ALLOW LIST. There is no state-name test here: the gate is the module's own
        /// <c>gameObject.activeInHierarchy</c>, which is how <c>UIModuleBehavior.SetStateID</c>:21-56 hides
        /// one (<c>gameObject.SetActive(false)</c> on its off state — these modules are
        /// <c>UIModuleBehavior</c> MonoBehaviours, not <c>Base.UI.UIModule</c>, so they have no
        /// <c>Active</c>). It is fail-safe in the right direction: a module hidden through the ANIMATOR arm
        /// of that same method keeps an active GameObject, so the worst this gate can do is refresh
        /// something already invisible — it can never skip a module that is on screen. That is also why it
        /// is safe on the states <see cref="AbilityBarStates"/> deliberately refuses to Exit+Enter for
        /// danger (<c>UIStateInventory</c>): this repaints, it does not commit anything and it destroys
        /// nothing.
        ///
        /// ponytail: the two bottom-bar modules only. The aim screen's other stale pixel — a dead target's
        /// crosshair, from the <c>_selectedValidShoots</c> constructor snapshot (<c>UIStateShoot</c>:193) —
        /// stays stale, because refreshing it means rebuilding the state, which is the thing L63 forbids.
        /// Upgrade path if it is ever reported: a targeted refilter of that field, never an Exit+Enter.
        /// </summary>
        private static void RepaintModules(TacticalView view)
        {
            var actor = view.SelectedActor;
            var modules = view.TacticalModules;
            if (actor == null || modules == null) return;
            try
            {
                // Law 8: a module re-read fires native UI events our own intent-capture seams listen to.
                using (SyncApplyScope.Enter())
                {
                    var abilities = modules.AbilitiesModule;
                    if (abilities != null && abilities.gameObject.activeInHierarchy)
                        abilities.SetAbilities(actor, GameUtl.GameComponent<InputController>());
                    var actionBar = modules.ActionBarModule;
                    if (actionBar != null && actionBar.gameObject.activeInHierarchy)
                        actionBar.SetActionBar(actor);
                }
            }
            catch (Exception ex)
            {
                // Same non-destructive posture as Repaint: a partial refresh, not a lost screen.
                if (_loggedFailures.Add("modules"))
                    Debug.LogWarning("[Multiplayer][tac] module repaint threw — bottom bar may be stale " +
                                     "(logged once per battle): " + ex);
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
        /// The state MUST still be current afterwards. 3d8e308 claimed the opposite ("legitimately not
        /// current, a fix rather than a fault", pointing at <c>UIStateShoot.EnterState</c>:352 leaving aim
        /// mode) — that was wrong, and it is why <c>UIStateShoot</c> left the allow list: a state whose
        /// <c>EnterState</c> pops or pushes gets its <c>ExitState</c> run a SECOND time by
        /// <c>StateStack.SwitchToPreviousState</c>:96-98, on top of the one this method already ran.</summary>
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
