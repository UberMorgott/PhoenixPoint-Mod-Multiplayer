namespace Multipleer.Sync.Tactical
{
    /// <summary>
    /// PURE (engine-free) policy for the CLIENT targeting/selection gate that keeps the targetable set in lockstep
    /// with the mirrored HOST vision. Native target acquisition (<c>TacticalAbility.TargetFilterPredicate</c> →
    /// <c>GetTargetActors</c>) gates enemies only on the per-weapon <c>FactionVisibility</c> flag and otherwise on a
    /// GEOMETRIC line-of-sight raycast against the LOCAL map — where a host-forgotten / invisible enemy is still
    /// physically present. So an enemy the host has lost vision of (already dropped from the mirrored
    /// <c>KnownActors</c> by <see cref="TacticalVisionSync"/>.ForgetActor → <c>IsRevealed</c>/<c>IsLocated</c> ==
    /// false) can still be selected/targeted/shot on the client → desync / a bad-shot window. This predicate decides
    /// when such a candidate must be REMOVED: only on a mirroring client, only for an ENEMY target of a player-faction
    /// source, and only when the mirrored vision knows it no longer. The Unity/Harmony wiring
    /// (<c>TargetVisionGatePatch</c>) computes the booleans and flips the native result; the host, single-player,
    /// friendly/neutral/self targets, and still-known enemies are never touched.
    /// </summary>
    public static class ClientVisionTargetGate
    {
        /// <param name="isClientMirroring">this instance is a synced-session client inside a mirrored mission.</param>
        /// <param name="sourceIsPlayerFaction">the acting actor belongs to the shared player faction (whose vision is mirrored).</param>
        /// <param name="targetIsEnemy">the candidate target is an ENEMY of the source (friendlies/neutrals/self excluded).</param>
        /// <param name="hostKnowsTarget">the mirrored host vision still knows the target (Located or Revealed).</param>
        /// <returns>true → drop this candidate (the host has forgotten it); false → leave the native decision untouched.</returns>
        public static bool ShouldBlockTarget(bool isClientMirroring, bool sourceIsPlayerFaction, bool targetIsEnemy, bool hostKnowsTarget)
            => isClientMirroring && sourceIsPlayerFaction && targetIsEnemy && !hostKnowsTarget;
    }
}
