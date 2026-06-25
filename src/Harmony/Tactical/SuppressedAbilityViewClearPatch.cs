using System;
using System.Reflection;
using HarmonyLib;
using Multipleer.Sync.Tactical;
using UnityEngine;

namespace Multipleer.Harmony.Tactical
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
        private static PropertyInfo _viewProp;         // TacticalViewContext.View
        private static MethodInfo _updateApWp;        // TacticalView.UpdateSquadMembersActionAndWillPoints()
        private static MethodInfo _resetCharSelected; // TacticalView.ResetCharacterSelectedState()
        private static object _clearStackAndPush;     // StateStackAction.ClearStackAndPush (boxed)

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

            var ctxType = _contextProp.PropertyType;
            _viewProp = AccessTools.Property(ctxType, "View");
            if (_viewProp == null) return false;

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
                if (!Equals(__2, _clearStackAndPush)) return true;              // ReplaceTop/PushOnTop are safe (incl. shoot)
                if (!TacticalAbilityRelay.IsClientSuppressedActivation(__0.GetType().Name)) return true; // not suppressed → native

                // Replicate native ActivateAbility lines 300-301, then skip the wedging clear (302-306).
                //   ability.Activate(target) → triggers the EXISTING suppression prefix EXACTLY ONCE (returns false:
                //   the tac.intent.* is relayed, real execution suppressed). No double-intent: native line 300 never
                //   runs because we return false below.
                _activate.Invoke(__0, new[] { __1 });

                object ctx = _contextProp.GetValue(__instance, null);
                object view = ctx != null ? _viewProp.GetValue(ctx, null) : null;
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
                    var reset = _resetCharSelected ?? (_resetCharSelected = AccessTools.Method(view.GetType(), "ResetCharacterSelectedState"));

                    // DIAG (re-grey site #1 — activation time): report whether the re-grey path runs and WHAT view state it
                    // sees. ResetCharacterSelectedState SELF-GUARDS on `CurrentState is UIStateCharacterSelected`
                    // (TacticalView.cs:306-312) → it is a NO-OP in any other state, so logging CurrentState here shows what
                    // (if anything) blocks the re-grey. NRE-guarded; reads CurrentState BEFORE the reset mutates it.
                    string stateName = "<null>";
                    try { object cs = AccessTools.Property(view.GetType(), "CurrentState")?.GetValue(view, null); stateName = cs?.GetType().Name ?? "<null>"; }
                    catch { /* diag only */ }
                    int actorNet = -1;
                    try { object aActor = AccessTools.Property(__0.GetType(), "TacticalActorBase")?.GetValue(__0, null); if (aActor != null) actorNet = TacticalDeploySync.NetIdForLiveActor(aActor); }
                    catch { /* diag only */ }
                    Debug.Log("[Multipleer][tac] CLIENT re-grey@activate fired=" + (reset != null) +
                              " state=" + stateName + " actorNet=" + actorNet);

                    reset?.Invoke(view, null);
                }

                Debug.Log("[Multipleer][tac] CLIENT skipped view-clear for suppressed " + __0.GetType().Name +
                          " (kept UIStateCharacterSelected; intent relayed once)");
                return false;   // SKIP native → no SwitchToState(UIStateWaiting, ClearStackAndPush) → control preserved
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multipleer][tac] SuppressedAbilityViewClearPatch.Prefix failed: " + ex);
                return true;    // fail-open: never wedge the native path on an unexpected error
            }
        }
    }
}
