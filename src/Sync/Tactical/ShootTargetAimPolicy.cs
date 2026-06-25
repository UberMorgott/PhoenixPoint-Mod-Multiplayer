namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// PURE, Unity-free decision behind the host's shoot-intent target REBUILD (TacticalCombatSync.BuildShootTarget):
    /// when the host reconstructs a <c>TacticalAbilityTarget</c> from a client's relayed shoot intent, should it carry
    /// the client's GROUND position (set <c>PositionToApply</c>) or stay POSITION-LESS so the host re-resolves the aim?
    ///
    /// WHY (the combat bug): the wire only carries the actor's GROUND pos (height ~0). If the rebuilt target's
    /// <c>PositionToApply</c> is set to that ground pos, it SUPPRESSES the host's body-part snapping
    /// (<c>ShootAbility.GetShootTarget</c> / SnapToBodyparts) → the shot fires LOW, clips cover → 0 damage. So when a
    /// target ACTOR is resolved, the rebuilt target must leave <c>PositionToApply</c> INVALID (NaN) and let the host
    /// re-snap the body-part authoritatively (host-authoritative aim). Only a bare-GROUND shot (no actor to snap to)
    /// carries the explicit <c>PositionToApply</c>. Pinned as a pure gate so the contract can't silently regress.
    /// </summary>
    public static class ShootTargetAimPolicy
    {
        /// <summary>
        /// True only when the host-rebuilt shoot target should carry the client's explicit GROUND position
        /// (<c>PositionToApply</c> set). False when a target actor is resolved — then the position is LEFT INVALID
        /// (NaN) so the host re-snaps the body-part. (= "apply the ground pos ⟺ there is NO target actor".)
        /// </summary>
        public static bool ShouldApplyGroundPosition(bool hasTargetActor) => !hasTargetActor;
    }
}
