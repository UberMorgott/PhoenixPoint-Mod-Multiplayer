namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// PURE, Unity-free decision behind the CLIENT non-shoot-action VIEW-FREEZE fix. On a mirroring client a
    /// NON-SHOOT player action (plain MOVE — and therefore melee/charge, which begin with a move — plus OVERWATCH)
    /// is SUPPRESSED locally: the patched <c>MoveAbility.Activate</c> / <c>OverwatchAbility.Activate</c> returns
    /// false (the intent is relayed to the host instead). The native view, however, ran
    /// <c>TacticalViewState.ActivateAbility</c> (TacticalViewState.cs:289-307) BEFORE the suppress took effect:
    /// it did <c>SwitchToState(new UIStateWaiting(), stateStackActionToApply)</c> with the DEFAULT
    /// <c>StateStackAction.ClearStackAndPush</c> (TacticalViewState.cs:289 default param), which EMPTIES the view
    /// state stack of the live control state (<c>UIStateCharacterSelected</c>) and pushes <c>UIStateWaiting</c>.
    ///
    /// WHY FIRE does NOT freeze but MOVE does (grounded, decompile file:line): the SHOOT confirm goes through
    /// <c>UIStateShoot.ConfirmAndShoot</c> → <c>ActivateAbility(_ability, target, StateStackAction.ReplaceTop, …)</c>
    /// (UIStateShoot.cs:1361) — explicitly <c>ReplaceTop</c>, NOT the default — and <c>UIStateShoot</c> sits ON TOP
    /// of <c>UIStateCharacterSelected</c> (pushed PushOnTop, UIStateCharacterSelected.cs:665/680/685). So the
    /// suppressed shoot only REPLACES the top, leaving the control state underneath, and the view recovers. The
    /// MOVE confirm calls <c>ActivateAbility(ActorMoveAbility, target)</c> straight from
    /// <c>UIStateCharacterSelected</c> with the DEFAULT <c>ClearStackAndPush</c> (UIStateCharacterSelected.cs:958),
    /// on a BARE <c>[UIStateCharacterSelected]</c> stack → the stack is emptied and never returns to a control
    /// state. <c>TacticalView.Update</c> guards the tick on <c>!_statesStack.IsEmpty</c> (TacticalView.cs:1051), so
    /// an empty stack = a silent, HUD-less hang.
    ///
    /// The <c>ClearStackAndPush</c> is therefore NATIVE (issued by the engine's own <c>ActivateAbility</c>), not
    /// mod code — so option B (push-over) is not available. The fix is option A: after the client APPLIES the
    /// mirrored completion of its own non-shoot action, RE-ESTABLISH the control view exactly the way the engine
    /// itself recovers — <c>TacticalView.ResetViewState()</c> (TacticalView.cs:262-268 =
    /// <c>SwitchToState(new UIStateInitial(), ClearStackAndPush)</c>), which re-pushes <c>UIStateInitial</c> whose
    /// <c>InitialStateUpdateCrt</c> advances to <c>UIStateCharacterSelected</c> for the (own, still-controllable)
    /// actor (UIStateInitial.cs:66-87). This is VIEW/selection only — no game-state mutation (no AP/HP).
    ///
    /// This gate pins the PRECISE wedge-detection so the recovery NEVER disrupts a HEALTHY view: recover ONLY when
    /// this is a mirroring client AND the live top view-state is one of the no-control "wedge" states — none
    /// (empty stack), <c>UIStateWaiting</c>, or <c>UIStateInitial</c>. If the view already shows a live control
    /// state (<c>UIStateCharacterSelected</c>/<c>UIStateShoot</c>/…) — e.g. a HOST or ENEMY move broadcast that
    /// never wedged this client's view — the gate returns false and <c>ResetViewState</c> is never called, so a
    /// healthy <c>UIStateCharacterSelected</c> is never clobbered. The recovery is thus correct regardless of which
    /// actor's action arrived; it only ever un-sticks an actually-wedged client view. The engine glue
    /// (<c>ClientControlViewRecovery.RestoreClientControlView</c>) binds game types via reflection and is NOT linked.
    /// </summary>
    public static class ClientControlViewRecoveryGate
    {
        /// <summary>
        /// True when the client mirror must re-establish its tactical control view after a non-shoot suppressed
        /// action completed. Exactly: this is a mirroring client AND the live top view-state is a no-control
        /// "wedge" state — <paramref name="currentStateTypeName"/> is null/empty (empty stack), or
        /// <c>"UIStateWaiting"</c>, or <c>"UIStateInitial"</c>. Any live control state (e.g.
        /// <c>"UIStateCharacterSelected"</c>) → false (the view is healthy; never touch it).
        /// </summary>
        /// <param name="isClientMirroring">Live <see cref="TacticalDeploySync.IsClientMirroring"/>.</param>
        /// <param name="currentStateTypeName">Type NAME of the live top view-state (<c>Type.Name</c>), or null
        /// when the state stack is empty.</param>
        public static bool ShouldRecoverControlView(bool isClientMirroring, string currentStateTypeName)
        {
            if (!isClientMirroring) return false;
            if (string.IsNullOrEmpty(currentStateTypeName)) return true;   // empty stack = the worst wedge
            return currentStateTypeName == "UIStateWaiting" || currentStateTypeName == "UIStateInitial";
        }
    }
}
