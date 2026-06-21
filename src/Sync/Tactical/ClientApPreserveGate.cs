namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// PURE, Unity-free decision behind FIX E: on the CLIENT mirror's turn-start SETUP replay, the per-actor
    /// native <c>TacticalActor.StartTurn</c> is still run (for selectability / ability-trait reset / HUD), but its
    /// AP/WP RE-MAX must be undone so the client takes AP ONLY from the host stream. This gate pins the "restore
    /// the captured AP/WP" contract so it can't silently regress (e.g. back to letting <c>SetToMax</c> stick).
    ///
    /// WHY (grounded, decompile file:line): selectability and the lit/usable state of every ability icon are LIVE
    /// functions of the actor's CURRENT AP — <c>TacticalView.IsActorSelectable</c> (TacticalView.cs:886) requires
    /// <c>CanAct()</c> (TacticalActorBase.cs:1070), which is true iff some active ability <c>IsEnabled()</c>, and
    /// <c>IsEnabled</c> → <c>GetDisabledStateDefaults</c> checks <c>ActionPointRequirementSatisfied</c>
    /// (TacticalAbility.cs:146 = <c>ActionPoints &gt;= cost</c>) EVERY call, not a cached snapshot. The native
    /// turn-start <c>TacticalActor.StartTurn</c> (TacticalActor.cs:1188) → <c>RestartAbilities</c> →
    /// <c>ActionPoints.SetToMax()</c> (TacticalActor.cs:1243) re-maxes AP. On a co-op client (a PURE MIRROR; AP is
    /// host-authoritative, streamed via tac.damage <c>ShooterApAfter</c> + the T1 actor-state delta), letting that
    /// re-max stick means spent AP is silently restored each player turn → ability icons stay lit at 0 AP. So the
    /// client MUST restore the pre-StartTurn (host-streamed) AP/WP after the replay.
    ///
    /// We keep the OTHER StartTurn side effects (the <c>SetAbilityTraits("start")</c> trait reset is required by
    /// "start"-gated abilities via <c>TraitsTagsRequirementSatisfied</c>, TacticalAbility.cs:471; plus
    /// <c>RemoveUnusableAbilities</c> / standby) — the host does NOT stream <c>AbilityTraits</c>, so removing
    /// StartTurn wholesale would wrongly disable those abilities on the client. Capture-then-restore AP/WP is the
    /// minimal, non-fragile pure-mirror fix: full StartTurn machinery runs, only the AP/WP re-max is reverted.
    /// </summary>
    public static class ClientApPreserveGate
    {
        /// <summary>
        /// True when the client mirror must PRESERVE (capture-before / restore-after) an own-actor's AP/WP across the
        /// turn-start <c>StartTurn</c> replay: exactly when this instance is a mirroring client. Host / single-player
        /// (not mirroring) run the real authoritative turn-start and must let <c>SetToMax</c> stand → returns false.
        /// </summary>
        public static bool ShouldPreserveActorApWp(bool isClientMirroring) => isClientMirroring;
    }
}
