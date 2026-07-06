namespace Multiplayer.Sync.Tactical
{
    /// <summary>
    /// PURE, Unity-free decision behind the host's shoot-intent target REBUILD (TacticalCombatSync.BuildShootTarget):
    /// when the host reconstructs a <c>TacticalAbilityTarget</c> from a client's relayed shoot intent, WHICH position
    /// should seed <c>PositionToApply</c>?
    ///
    /// WHY (the combat bug, CORRECTED): the wire only carries the actor's GROUND pos (height ~0). The OLD contract left
    /// <c>PositionToApply</c> INVALID (NaN) for an actor target on the false premise that the host re-snaps the body-part
    /// on re-Activate. It does NOT: <c>ShootAbility.Activate</c> (ShootAbility.cs:152-175) casts the raw target and
    /// enqueues Shoot with it — the snapper <c>GetShootTarget</c>/SnapToBodyparts (ShootAbility.cs:194-224) is only on the
    /// UI GetTargets path, never on the host re-invoke. So a NaN-position actor target falls through
    /// <c>TacticalAbilityTarget.GetWorkingPosition</c> (TacticalAbilityTarget.cs:175-192) to
    /// <c>GameObject.transform.position</c> = the actor ROOT/FEET (Y=0) → the shot hits the ground → 0 damage.
    ///
    /// THE FIX: an actor target SEEDS <c>PositionToApply</c> with the target's AIM POINT (center-of-mass,
    /// <c>TacticalActorBase.GetAimPoint().position</c>) — always NON-NaN — so the working position is the body (not the
    /// feet), and the host's best-effort <c>GetShootTarget</c> snap (run explicitly in HostOnAbilityIntent) has a valid
    /// seed to find the NEAREST body part. A bare-GROUND shot (no actor to snap to) seeds the explicit GROUND pos.
    /// Pinned as a pure gate so the contract can't silently regress.
    /// </summary>
    public static class ShootTargetAimPolicy
    {
        /// <summary>Which position the host-rebuilt shoot target carries (both are NON-NaN — the retired NaN-for-actor
        /// contract is gone).</summary>
        public enum AimSource
        {
            /// <summary>Actor target → seed <c>PositionToApply</c> with the actor's AIM POINT (center-of-mass); the host
            /// then best-effort snaps to the nearest body part.</summary>
            ActorAimPoint,

            /// <summary>Bare-ground shot (no actor) → seed <c>PositionToApply</c> with the client's explicit GROUND pos.</summary>
            GroundPosition,
        }

        /// <summary>Decide the seed position source for the host-rebuilt shoot target: an ACTOR target uses the actor's
        /// aim point (non-NaN, body-center); a bare-GROUND target (no actor) uses the explicit ground position.</summary>
        public static AimSource Decide(bool hasTargetActor)
            => hasTargetActor ? AimSource.ActorAimPoint : AimSource.GroundPosition;
    }
}
