using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Sync.Tactical;
using UnityEngine;

namespace Multiplayer.Harmony.Tactical
{
    /// <summary>
    /// CLIENT view-freeze ROOT FIX (replaces the fragile after-the-fact ClientControlViewRecovery). PREVENTS the
    /// HUD-less wedge instead of trying to undo it.
    ///
    /// THE WEDGE (decompile-grounded): a mirroring client SUPPRESSES its own non-shoot ability activation —
    /// <c>MoveAbility.Activate</c> (<see cref="MoveAbilityActivatePatch"/>), <c>BashAbility</c>/<c>ShootAbility.Activate</c>
    /// (<see cref="AbilityActivateRelayPatch"/>), <c>OverwatchAbility.Activate</c>
    /// (<see cref="OverwatchAbilityActivatePatch"/>) — so the prefix relays a <c>tac.intent.*</c> and returns false.
    /// The native VIEW wrapper <c>TacticalViewState.ActivateAbility(ability, target, ClearStackAndPush=default)</c>
    /// (TacticalViewState.cs:289-307) had run <c>currentState = CurrentState; ability.Activate(target)</c> (300, now a
    /// no-op because suppressed) <c>; UpdateSquadMembersActionAndWillPoints()</c> (301); then
    /// <c>if (CurrentState == currentState) SwitchToState(new UIStateWaiting(), ClearStackAndPush)</c> (302-306) — the
    /// suppressed Activate did NOT change the stack, so the guard is TRUE and the engine EMPTIES the bare
    /// <c>[UIStateCharacterSelected]</c> control stack and pushes <c>UIStateWaiting</c> → the stack collapses to a dead
    /// <c>UIStateInitial</c> (TacticalView.Update guards the tick on <c>!IsEmpty</c>, TacticalView.cs:1051) → HUD gone,
    /// no control. Move-confirm (UIStateCharacterSelected.cs:958), bash-confirm (UIStateAbilitySelected.cs:667-671) and
    /// overwatch-confirm (UIStateOverwatchAbilitySelected.cs:323) ALL hit ActivateAbility with the DEFAULT
    /// ClearStackAndPush, so all three wedge.
    ///
    /// WHY FIRE IS UNAFFECTED (verified): the SHOOT confirm <c>UIStateShoot.ConfirmAndShoot</c> calls
    /// <c>ActivateAbility(_ability, target, StateStackAction.ReplaceTop, …)</c> (UIStateShoot.cs:1361) — EXPLICIT
    /// ReplaceTop, NOT the default — and <c>UIStateShoot</c> sits ON TOP of <c>UIStateCharacterSelected</c>
    /// (PushOnTop). So the shoot only replaces the top, leaving the control state underneath. Our gate fires ONLY when
    /// <c>stateStackActionToApply == ClearStackAndPush</c>, so a ReplaceTop shoot is never intercepted (and even if it
    /// were, the control state survives underneath). Fire keeps working unchanged.
    ///
    /// THE FIX: a CLIENT-only prefix on the BASE <c>TacticalViewState.ActivateAbility</c>. When mirroring AND the
    /// action is the wedging <c>ClearStackAndPush</c> AND the ability is a client-suppressed type
    /// (<see cref="TacticalAbilityRelay.IsClientSuppressedActivation"/> — the exact union of the three suppression
    /// sets), we REPLICATE the native pre-clear steps (<c>ability.Activate(target)</c> — which triggers the existing
    /// suppression prefix exactly ONCE: the intent is relayed, real execution suppressed — then
    /// <c>UpdateSquadMembersActionAndWillPoints()</c>) and RETURN FALSE to SKIP the native clear (lines 302-306). Net:
    /// the <c>[UIStateCharacterSelected]</c> control state is never emptied → HUD stays, client keeps control while the
    /// host-mirrored outcome animates. Single seam, covers move + melee + overwatch + any future suppressed non-shoot
    /// ability. Fully guarded + fail-open: any throw or missing member falls through to the native method (return true),
    /// never wedging worse than today.
    /// </summary>
    [HarmonyPatch]
    public static class SuppressedAbilityViewClearPatch
    {
        private static MethodBase _target;
        private static MethodInfo _activate;          // TacticalAbility.Activate(object)
        private static PropertyInfo _contextProp;     // TacticalViewState.Context (protected)
        private static FieldInfo _viewField;           // TacticalViewContext.View (public FIELD, not a property)
        private static MethodInfo _updateApWp;        // TacticalView.UpdateSquadMembersActionAndWillPoints()
        private static MethodInfo _resetCharSelected; // TacticalView.ResetCharacterSelectedState()
        private static object _clearStackAndPush;     // StateStackAction.ClearStackAndPush (boxed)

        // Full-stack recovery (pushed aim sub-state → fresh UIStateCharacterSelected). Resolved lazily off the live View.
        private static Type _charSelectedType;            // PhoenixPoint.Tactical.View.ViewStates.UIStateCharacterSelected (internal)
        private static ConstructorInfo _charSelectedCtor; // (bool showPlayersIntroText, bool cameraChaseSelectedCharacter)
        private static FieldInfo _statesStackField;       // TacticalView._statesStack (StateStack<TacticalViewContext>)
        private static MethodInfo _switchToState;         // StateStack.SwitchToState(IState, StateStackAction)

        public static bool Prepare()
        {
            var viewState = AccessTools.TypeByName("PhoenixPoint.Tactical.View.TacticalViewState");
            var ability = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbility");
            var target = AccessTools.TypeByName("PhoenixPoint.Tactical.Entities.Abilities.TacticalAbilityTarget");
            var action = AccessTools.TypeByName("Base.UI.StateStackAction");
            if (viewState == null || ability == null || target == null || action == null) return false;

            // protected virtual void ActivateAbility(TacticalAbility, TacticalAbilityTarget, StateStackAction,
            //                                        Func<TacticalAbility,bool>) — EXACT 4-param match (AccessTools
            // does exact param-type matching; an overload mismatch silently fails to bind).
            var funcType = typeof(Func<,>).MakeGenericType(ability, typeof(bool));
            _target = AccessTools.Method(viewState, "ActivateAbility", new[] { ability, target, action, funcType });
            if (_target == null) return false;

            _activate = AccessTools.Method(ability, "Activate", new[] { typeof(object) });
            _contextProp = AccessTools.Property(viewState, "Context");
            _updateApWp = null; // resolved lazily off the live View instance (TacticalView is the concrete type)
            if (_activate == null || _contextProp == null) return false;

            // TacticalViewContext.View is a public FIELD (TacticalViewContext.cs:13 `public TacticalView View;`),
            // NOT a property. The old AccessTools.Property(ctxType, "View") returned null → Prepare() false → the
            // WHOLE patch never bound (no overwatch/bash recovery ever ran). Resolve it as a Field.
            var ctxType = _contextProp.PropertyType;
            _viewField = AccessTools.Field(ctxType, "View");
            if (_viewField == null) return false;

            try { _clearStackAndPush = Enum.Parse(action, "ClearStackAndPush"); }
            catch { return false; }
            return true;
        }

        public static MethodBase TargetMethod() => _target;

        /// <summary>Returns false to SKIP the native ActivateAbility (we already ran its pre-clear steps) ONLY for a
        /// mirroring client's suppressed non-shoot ClearStackAndPush activation; true (run native) otherwise.</summary>
        public static bool Prefix(object __instance, object __0 /*ability*/, object __1 /*target*/, object __2 /*stateStackActionToApply*/)
        {
            try
            {
                if (!TacticalDeploySync.IsClientMirroring) return true;          // host / single-player → native
                if (__0 == null) return true;
                if (!TacticalAbilityRelay.IsClientSuppressedActivation(__0.GetType().Name)) return true; // not suppressed → native

                // NEW SHOOT CANON (rca-grenade-ui): a ShootAbility (shoot + grenade) runs NATIVELY on the origin
                // client (815634b) — its Activate is NOT a stack-unchanged no-op anymore, so the wedge rationale
                // above no longer applies to it and the native ActivateAbility view flow must run untouched. The
                // missed case was the GROUND-TARGETED grenade confirm: UIStateShoot confirms it via
                // ActivateAbility(_ability, AbilityTarget) with the DEFAULT ClearStackAndPush (UIStateShoot.cs
                // :985/:1303 → default at :1385) — NOT the enemy-target shot's explicit ReplaceTop (:1315) — so
                // this prefix intercepted it, skipped the native SwitchToState(UIStateWaiting) and left the
                // trajectory ribbon stuck in UIStateShoot forever (no terminal signal ever comes for the origin:
                // its own tac.fire.start echo is de-duped as predicted, and a structure-only blast raises no
                // tac.damage). Enemy-target ReplaceTop shots were never intercepted (gate below) — unchanged.
                //
                // rca-jetjump: same early-out for ORIGIN-NATIVE special moves (JetJump) — their Activate also
                // runs natively on the origin now, so the native ClearStackAndPush confirm from
                // UIStateAbilitySelected must run untouched: SwitchToState(UIStateWaiting) tears the aim
                // sub-state down and waits for the executing jump exactly like single-player.
                string abilityTypeName = __0.GetType().Name;
                if (TacticalAbilityRelay.ShouldBroadcastFireStart(abilityTypeName) ||
                    TacticalAbilityRelay.IsOriginNativeMove(abilityTypeName)) return true;

                // Resolve the live view + current view-state name UP FRONT: the state drives BOTH the gate below
                // (which ReplaceTop activations to intercept) AND the recovery choice. The suppressed Activate does
                // NOT change the view state, so reading it before Activate is equivalent to the old post-Activate read.
                object ctx = _contextProp.GetValue(__instance, null);
                object view = ctx != null ? _viewField.GetValue(ctx) : null;
                string stateName = ReadCurrentStateName(view);

                // GATE. Intercept ONLY the wedging ClearStackAndPush confirm (move / bash / overwatch): the
                // suppressed non-shoot Activate leaves the stack unchanged, so native would empty the bare control
                // state (move) or no-op the guarded sub-state reset (bash/overwatch) → HUD-less, camera-locked wedge.
                // A suppressed SHOOT confirm (ReplaceTop from UIStateShoot/UIStateFreeCam) is DELIBERATELY NOT
                // intercepted — it stays on the native follow-up loop so a multi-round volley keeps firing at native
                // speed; the single aimed-shot EXIT is driven separately by TryExitClientShootAimIfTerminal once the
                // host's authoritative post-shot AP lands. Any OTHER ReplaceTop/PushOnTop stays native too.
                bool clearStackAndPush = Equals(__2, _clearStackAndPush);
                if (!clearStackAndPush && !TacticalAbilityRelay.NeedsFullStackRecovery(stateName)) return true;

                // Replicate native ActivateAbility lines 300-301, then skip the wedging clear / UIStateWaiting push.
                //   ability.Activate(target) → triggers the EXISTING suppression prefix EXACTLY ONCE (returns false:
                //   the tac.intent.* is relayed, real execution suppressed + the client-predicted fire anim starts).
                //   No double-intent: native line 300 never runs because we return false below.
                _activate.Invoke(__0, new[] { __1 });

                if (view != null)
                {
                    var upd = _updateApWp ?? (_updateApWp = AccessTools.Method(view.GetType(), "UpdateSquadMembersActionAndWillPoints"));
                    upd?.Invoke(view, null);

                    // RE-GREY THE ABILITY BAR. The squad-portrait dots above refresh, but the ability buttons keep
                    // their pre-action lit/grey state: Button.IsEnabled is stamped ONCE in AbilityButtonController
                    // .SetButton and only re-evaluated when UIModuleAbilities.SetAbilities re-runs, which only happens
                    // when UIStateCharacterSelected is (re-)entered. Skipping the native clear above (the HUD-loss fix)
                    // means it is NOT re-entered → buttons stay lit even when AP is now insufficient (cosmetic only —
                    // the native AP gate still blocks the activation). TacticalView.ResetCharacterSelectedState
                    // (TacticalView.cs:306) re-pushes a fresh UIStateCharacterSelected via ClearStackAndPush →
                    // re-runs SetAbilities → every button re-evaluates ability.IsEnabled() (a LIVE AP check) and greys
                    // out. It SELF-GUARDS on `CurrentState is UIStateCharacterSelected` (NRE-safe + no-op when nothing
                    // is selected or we're in a melee/overwatch sub-state) and re-pushes CharacterSelected — NOT
                    // UIStateWaiting — so HUD and control are preserved. It does NOT change which actor is selected.
                    // stateName was read UP FRONT (before Activate, when the stack was untouched) — reuse it as BOTH
                    // the diag value and the input to the PURE recovery decision (NeedsFullStackRecovery). NRE-guarded.
                    int actorNet = -1;
                    try { object aActor = AccessTools.Property(__0.GetType(), "TacticalActorBase")?.GetValue(__0, null); if (aActor != null) actorNet = TacticalDeploySync.NetIdForLiveActor(aActor); }
                    catch { /* diag only */ }

                    if (TacticalAbilityRelay.NeedsFullStackRecovery(stateName))
                    {
                        // FULL-STACK RECOVERY (overwatch / bash / any pushed aim sub-state). The arm was confirmed from a
                        // sub-state PushOnTop ABOVE UIStateCharacterSelected, so ResetCharacterSelectedState would NO-OP
                        // (it self-guards on `CurrentState is UIStateCharacterSelected`, TacticalView.cs:308) → the
                        // sub-state never exits → HUD torn down + camera stuck (the reported unplayable wedge). Force-exit
                        // it exactly like the game's own back-out (UIStateOverwatchAbilitySelected.OnCancel:105-106 and
                        // TacticalView.ResetTacticalViewCmd:1190): _statesStack.SwitchToState(new UIStateCharacterSelected(
                        // showPlayersIntroText:false, cameraChaseSelectedCharacter:true), ClearStackAndPush). ClearStackAndPush
                        // runs StateStack.Clear → the sub-state's ExitState (StopDrawing + SetOverwatchVisuals,
                        // UIStateOverwatchAbilitySelected.cs:90-100) → a fresh UIStateCharacterSelected re-selects
                        // View.SelectedActor and DoCameraChases it (cameraChase:true un-sticks the DoCameraChaseParam camera
                        // left by SetInitialTargetPosition:135-141) + re-runs SetAbilities (re-grey). Mirrors native cancel.
                        bool driven = DriveFullStackRecovery(view);
                        Debug.Log("[Multiplayer][tac] CLIENT full-stack recovery@activate fired=" + driven +
                                  " (exited aim sub-state " + stateName + ") actorNet=" + actorNet);
                    }
                    else
                    {
                        // Bare control state (e.g. a Move confirm from UIStateCharacterSelected): the guarded re-grey works
                        // in place — re-push CharacterSelected so the ability bar re-evaluates live AP. (Unchanged path.)
                        var reset = _resetCharSelected ?? (_resetCharSelected = AccessTools.Method(view.GetType(), "ResetCharacterSelectedState"));
                        Debug.Log("[Multiplayer][tac] CLIENT re-grey@activate fired=" + (reset != null) +
                                  " state=" + stateName + " actorNet=" + actorNet);
                        reset?.Invoke(view, null);
                    }
                }

                Debug.Log("[Multiplayer][tac] CLIENT skipped view-clear for suppressed " + __0.GetType().Name +
                          " (kept UIStateCharacterSelected; intent relayed once)");
                return false;   // SKIP native → no SwitchToState(UIStateWaiting, ClearStackAndPush) → control preserved
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] SuppressedAbilityViewClearPatch.Prefix failed: " + ex);
                return true;    // fail-open: never wedge the native path on an unexpected error
            }
        }

        /// <summary>Read the current tactical view-state's runtime type name off the live View
        /// (<c>TacticalView.CurrentState</c>); "&lt;null&gt;" when the view or state is unreadable. NRE-guarded —
        /// a failed read degrades to the conservative "not an aim sub-state" decision (native path preserved).</summary>
        private static string ReadCurrentStateName(object view)
        {
            if (view == null) return "<null>";
            try { object cs = AccessTools.Property(view.GetType(), "CurrentState")?.GetValue(view, null); return cs?.GetType().Name ?? "<null>"; }
            catch { return "<null>"; }
        }

        /// <summary>CLIENT-only: force-exit a pushed aim sub-state by re-establishing a fresh
        /// <c>UIStateCharacterSelected</c> via <c>_statesStack.SwitchToState(new UIStateCharacterSelected(false, true),
        /// ClearStackAndPush)</c> — the SAME native call <c>ResetCharacterSelectedState</c> (TacticalView.cs:310) and the
        /// overwatch <c>OnCancel</c> (UIStateOverwatchAbilitySelected.cs:105-106) use, but UNCONDITIONAL (no
        /// `CurrentState is UIStateCharacterSelected` guard). ClearStackAndPush triggers the sub-state's ExitState
        /// teardown and re-selects View.SelectedActor; cameraChaseSelectedCharacter:true re-chases the actor to release
        /// the sub-state's DoCameraChaseParam camera lock. Returns true if the stack was driven; false (fail-open, never
        /// throws) if any native member could not be resolved.</summary>
        private static bool DriveFullStackRecovery(object view)
        {
            var charType = _charSelectedType ?? (_charSelectedType =
                AccessTools.TypeByName("PhoenixPoint.Tactical.View.ViewStates.UIStateCharacterSelected"));
            if (charType == null) return false;

            var ctor = _charSelectedCtor ?? (_charSelectedCtor =
                AccessTools.Constructor(charType, new[] { typeof(bool), typeof(bool) }));
            if (ctor == null) return false;

            var stackField = _statesStackField ?? (_statesStackField = AccessTools.Field(view.GetType(), "_statesStack"));
            object stack = stackField?.GetValue(view);
            if (stack == null) return false;

            // StateStack<T>.SwitchToState(IState<T>, StateStackAction) — single method named SwitchToState (no overload).
            var switchTo = _switchToState ?? (_switchToState = AccessTools.Method(stack.GetType(), "SwitchToState"));
            if (switchTo == null) return false;

            object fresh = ctor.Invoke(new object[] { false /*showPlayersIntroText*/, true /*cameraChaseSelectedCharacter*/ });
            switchTo.Invoke(stack, new[] { fresh, _clearStackAndPush });
            return true;
        }

        // Shoot AIM sub-states a mirroring client can end up wedged in after a SUPPRESSED relayed shot. UNLIKE
        // overwatch/bash these are NOT in AimSubStateViewNames: the shoot ReplaceTop stays on the native follow-up
        // loop (ShootAbilityFinishedExecutionHandler → SwitchToFollowupShootState re-enters a fresh UIStateShoot each
        // round) so a multi-round volley keeps firing; only the TERMINAL round's exit is driven here.
        private static readonly HashSet<string> _shootAimStates =
            new HashSet<string>(new[] { "UIStateShoot", "UIStateFreeCam" }, StringComparer.Ordinal);

        /// <summary>CLIENT terminal-driven aim-EXIT for the relayed SHOOT path. Called the instant the client applies
        /// a relayed shot's authoritative post-shot AP/WP (<c>TacticalCombatSync.HandleDamage</c> → <c>SetApWp</c>).
        /// The client never spends AP locally (its shot is suppressed) and its native
        /// <c>ShootAbilityFinishedExecutionHandler</c> runs BEFORE the host AP lands (the suppressed shot is never in
        /// <c>ExecutingAbilities</c>, so <c>ShouldViewWaitForMe</c> is false → <c>UIStateWaiting</c> fires the handler
        /// immediately), so that handler's terminal check (<c>GetDisabledState()!=NotDisabled</c>, UIStateShoot.cs:1346)
        /// always reads stale-ENABLED → it re-enters aim EVERY round, including the LAST (the reported single aimed-shot
        /// wedge — client stuck in the reticle). Here, with the AUTHORITATIVE AP now applied, we re-evaluate the SAME
        /// terminal condition on the aimed <c>ShootAbility</c> (<c>UIStateShoot._ability</c>, UIStateShoot.cs:46): still
        /// enabled → another round is available → LEAVE the native aim loop alone (fast multi-round volley); now
        /// disabled (out of AP/charges / no valid target) → the volley is done → force-exit the aim sub-state to a fresh
        /// <c>UIStateCharacterSelected</c> via the SAME <see cref="DriveFullStackRecovery"/> the move/bash/overwatch
        /// recovery uses. Fires at most once per terminal transition (the exit removes the aim state). Client-only,
        /// fully NRE-guarded / fail-safe (any failure leaves the native path untouched — never severs a live volley).</summary>
        public static bool TryExitClientShootAimIfTerminal(int shooterNetId)
        {
            try
            {
                if (!TacticalDeploySync.IsClientMirroring || shooterNetId < 0) return false;

                object tlc = TacticalDeploySync.LiveTlc;
                object view = tlc != null ? AccessTools.Property(tlc.GetType(), "View")?.GetValue(tlc, null) : null;
                if (view == null) return false;

                object cs = AccessTools.Property(view.GetType(), "CurrentState")?.GetValue(view, null);
                if (cs == null || !_shootAimStates.Contains(cs.GetType().Name)) return false; // not wedged in a shoot aim state

                // Only the shooter whose shot just resolved: the aim state's selected actor must BE that shooter,
                // so a shot resolving while the client aims a different soldier never yanks the wrong aim.
                object selected = AccessTools.Property(view.GetType(), "SelectedActor")?.GetValue(view, null);
                if (selected == null || TacticalDeploySync.NetIdForLiveActor(selected) != shooterNetId) return false;

                // SAME terminal condition as native ShootAbilityFinishedExecutionHandler (UIStateShoot.cs:1346):
                // GetDisabledState() with a null filter (the single public overload, TacticalAbility.cs:372).
                object aimAbility = AccessTools.Field(cs.GetType(), "_ability")?.GetValue(cs);
                if (aimAbility == null) return false;
                var getDisabled = AccessTools.Method(aimAbility.GetType(), "GetDisabledState");
                object gds = getDisabled?.Invoke(aimAbility, new object[] { null });
                if (gds == null || string.Equals(gds.ToString(), "NotDisabled", StringComparison.Ordinal))
                    return false; // still enabled → more rounds available → keep the native follow-up aim loop

                bool driven = DriveFullStackRecovery(view);
                Debug.Log("[Multiplayer][tac] CLIENT shoot-aim terminal exit fired=" + driven + " state=" +
                          cs.GetType().Name + " shooterNet=" + shooterNetId + " disabled=" + gds);
                return driven;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer][tac] TryExitClientShootAimIfTerminal failed: " + ex);
                return false; // fail-safe: leave the native path untouched
            }
        }
    }
}
